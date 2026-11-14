namespace Auction.Infrastructure.Clients;

using System.Net;
using System.Text;
using System.Text.Json;
using Auction.Application.Interfaces;
using Auction.Domain.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

/// <summary>
/// Settings of <see cref="UserClient"/>. BypassUserCheck is for local tests only and is refused in Production.
/// DefaultTimeout (check-reputation, batch-public-info) and BidTimeout (verify-bid-eligibility) are optional;
/// when empty the built-in values (2 seconds and 800 milliseconds) are used. Raise them for local tests with a remote database.
/// </summary>
public sealed record UserClientOptions(bool BypassUserCheck, TimeSpan? DefaultTimeout = null, TimeSpan? BidTimeout = null);

/// <summary>REST client of the User service (internal APIs used by Auction).</summary>
public sealed class UserClient : IUserClient
{
    private const string CheckReputationPath = "/api/v1/internal/users/check-reputation";
    private const string VerifyBidPath = "/api/v1/internal/users/verify-bid-eligibility";
    private const string BatchInfoPath = "/api/v1/internal/users/batch-public-info";
    private const string DepositProfilePathFormat = "/api/v1/internal/users/{0}/auction-deposit-profile";
    private const string RecordViolationPathFormat = "/api/v1/internal/users/{0}/auction-violations";
    private const int BatchLimit = 100;

    // The deposit profile is read on every bid, so it is cached briefly. A newly restricted bidder can still bid for up to this long.
    private static readonly TimeSpan DepositProfileCacheLifetime = TimeSpan.FromSeconds(45);

    // Bidding is a hot path: a slow User service must not hold connections of Auction.
    private static readonly TimeSpan BuiltInBidTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan BuiltInDefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly UserClientOptions _options;
    private readonly ILogger<UserClient> _logger;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _bidTimeout;
    private readonly TimeSpan _defaultTimeout;

    public UserClient(HttpClient http, UserClientOptions options, ILogger<UserClient> logger, IMemoryCache cache)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _cache = cache;
        _bidTimeout = options.BidTimeout ?? BuiltInBidTimeout;
        _defaultTimeout = options.DefaultTimeout ?? BuiltInDefaultTimeout;
    }

    public async Task<UserReputationCheck> CheckSellerReputationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_options.BypassUserCheck)
        {
            _logger.LogWarning("BypassUserCheck is on: the seller check was skipped for user {UserId}.", userId);
            return new UserReputationCheck(userId, true, true, "Active", null);
        }

        var payload = await PostAsync(CheckReputationPath, new CheckReputationQuery("SELL", userId), _defaultTimeout, cancellationToken);

        var allowed = RequireBool(payload, "allowed");
        var approved = RequireBool(payload, "isApprovedSeller");
        var reason = OptionalString(payload, "failureReason");
        EnsureSameUser(payload, userId);

        // Contradictions mean the answer cannot be trusted.
        if (allowed && (!approved || !string.IsNullOrWhiteSpace(reason)))
            throw InvalidResponse("allowed is true but the seller is not approved or a failure reason is present");

        return new UserReputationCheck(userId, allowed, approved, OptionalString(payload, "userStatus"), reason);
    }

    public async Task<UserBidEligibility> VerifyBidEligibilityAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_options.BypassUserCheck)
        {
            _logger.LogWarning("BypassUserCheck is on: the bid check was skipped for user {UserId}.", userId);
            return new UserBidEligibility(userId, true, null);
        }

        var payload = await PostAsync(VerifyBidPath, new BidEligibilityQuery(userId), _bidTimeout, cancellationToken);

        // The field is named "allowed" or "isEligible" depending on the version of the User service.
        var eligible = HasProperty(payload, "allowed")
            ? RequireBool(payload, "allowed")
            : RequireBool(payload, "isEligible");
        var reason = OptionalString(payload, "failureReason");
        EnsureSameUser(payload, userId);

        if (eligible && !string.IsNullOrWhiteSpace(reason))
            throw InvalidResponse("eligible is true but a failure reason is present");

        return new UserBidEligibility(userId, eligible, reason);
    }

    public async Task<UserAuctionDepositProfile> GetAuctionDepositProfileAsync(
        Guid userId, bool bypassCache = false, CancellationToken cancellationToken = default)
    {
        if (_options.BypassUserCheck)
        {
            _logger.LogWarning("BypassUserCheck is on: the deposit profile was skipped for user {UserId}.", userId);
            return new UserAuctionDepositProfile(userId, "ACTIVE", 100, false, 0, false, null, null, null);
        }

        var cacheKey = $"user-deposit-profile:{userId:N}";
        if (!bypassCache && _cache.TryGetValue(cacheKey, out UserAuctionDepositProfile? cached) && cached is not null)
            return cached;

        var payload = await GetAsync(string.Format(DepositProfilePathFormat, userId), _bidTimeout, cancellationToken);
        var profile = ParseDepositProfile(payload, userId);

        _cache.Set(cacheKey, profile, DepositProfileCacheLifetime);
        return profile;
    }

    public async Task<IReadOnlyDictionary<Guid, UserPublicInfo>> GetPublicInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, UserPublicInfo>();
        if (userIds.Count == 0)
            return result;

        // User accepts at most 100 ids per call.
        foreach (var chunk in userIds.Distinct().Chunk(BatchLimit))
        {
            var payload = await PostAsync(BatchInfoPath, new BatchInfoQuery(chunk), _defaultTimeout, cancellationToken);
            if (payload.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("the list of users is not an array");

            foreach (var item in payload.EnumerateArray())
            {
                // One bad item must not hide the other names: it is skipped.
                var id = OptionalGuid(item, "userId");
                var name = OptionalString(item, "maskedName");
                if (id is null || id == Guid.Empty || string.IsNullOrWhiteSpace(name))
                {
                    _logger.LogWarning("User service returned a public profile without a user id or masked name.");
                    continue;
                }

                var deleted = item.TryGetProperty("isDeleted", out var d) && d.ValueKind == JsonValueKind.True;
                result[id.Value] = new UserPublicInfo(id.Value, name, deleted);
            }
        }

        return result;
    }

    public async Task<RecordAuctionViolationResult> RecordAuctionViolationAsync(
        RecordAuctionViolationCommand command, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"user-deposit-profile:{command.UserId:N}";
        _cache.Remove(cacheKey);

        if (_options.BypassUserCheck)
        {
            _logger.LogWarning("BypassUserCheck is on: recording auction violation was skipped for user {UserId}.", command.UserId);
            return new RecordAuctionViolationResult(Guid.NewGuid(), true, 0, 1, 100, 95, "Warning", null);
        }

        var path = string.Format(RecordViolationPathFormat, command.UserId);
        var body = new RecordViolationBody(
            command.ViolationType,
            command.AuctionId,
            command.OrderId,
            command.OccurredAt,
            command.DepositForfeitedAmount);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_defaultTimeout);

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content, timeoutSource.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DomainException("USER_NOT_FOUND", "The user was not found.", 404);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"User service answered {(int)response.StatusCode} to auction-violations.");

            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var payload = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                          data.ValueKind == JsonValueKind.Object
                ? data
                : root;

            var violationId = OptionalGuid(payload, "violationId") ?? Guid.Empty;
            var created = HasProperty(payload, "created") && RequireBool(payload, "created");
            var previousStreak = OptionalInt(payload, "previousStreak") ?? 0;
            var newStreak = OptionalInt(payload, "newStreak") ?? 0;
            var repBefore = OptionalInt(payload, "reputationBefore") ?? 0;
            var repAfter = OptionalInt(payload, "reputationAfter") ?? 0;

            string? sanctionType = null;
            DateTimeOffset? sanctionUntil = null;
            if (payload.TryGetProperty("sanction", out var sanction) && sanction.ValueKind == JsonValueKind.Object)
            {
                sanctionType = OptionalString(sanction, "type");
                if (OptionalString(sanction, "until") is { } untilText && DateTimeOffset.TryParse(
                        untilText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedUntil))
                {
                    sanctionUntil = parsedUntil;
                }
            }

            return new RecordAuctionViolationResult(
                violationId, created, previousStreak, newStreak, repBefore, repAfter, sanctionType, sanctionUntil);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "User service call failed when recording auction violation for user {UserId}.", command.UserId);
            throw new DomainException("USER_SERVICE_UNAVAILABLE", "The user service is unavailable.", 503);
        }
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>Posts a body and returns the useful part of the answer (the "data" object when there is one).</summary>
    private async Task<JsonElement> PostAsync<TBody>(
        string path, TBody body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content, timeoutSource.Token);
            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token);

            // 403, 404 and 423 can carry a business reason. Anything else that is not a success is a failure of the service.
            var businessStatus = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or (HttpStatusCode)423;
            if (!response.IsSuccessStatusCode && !businessStatus)
                throw new HttpRequestException($"User service answered {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
                throw InvalidResponse("the answer says it was not successful");

            var payload = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                          data.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? data
                : root;

            if (!response.IsSuccessStatusCode && !HasReason(payload))
                throw InvalidResponse("an error status without a reason");

            return payload.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "User service call failed.");
            throw new DomainException("USER_SERVICE_UNAVAILABLE", "The user service is unavailable.", 503);
        }
    }

    /// <summary>Gets a resource and returns the useful part of the answer. 404 means the user does not exist; any other failure means User is unavailable.</summary>
    private async Task<JsonElement> GetAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await _http.GetAsync(path, timeoutSource.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DomainException("USER_NOT_FOUND", "The user was not found.", 404);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"User service answered {(int)response.StatusCode}.");

            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var payload = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                          data.ValueKind == JsonValueKind.Object
                ? data
                : root;
            return payload.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "User service call failed.");
            throw new DomainException("USER_SERVICE_UNAVAILABLE", "The user service is unavailable.", 503);
        }
    }

    private static UserAuctionDepositProfile ParseDepositProfile(JsonElement payload, Guid requested)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw InvalidResponse("the profile is not an object");
        EnsureSameUser(payload, requested);

        var status = OptionalString(payload, "accountStatus")?
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (status is not ("PENDINGVERIFICATION" or "ACTIVE" or "BANNED" or "DELETED"))
            throw InvalidResponse("'accountStatus' is missing or unknown");

        int? reputation = null;
        if (payload.TryGetProperty("reputationScore", out var score) && score.ValueKind == JsonValueKind.Number)
        {
            if (!score.TryGetInt32(out var scoreValue))
                throw InvalidResponse("'reputationScore' is not a whole number");
            reputation = scoreValue;
        }

        var isNewUser = RequireBool(payload, "isNewUser");

        if (!payload.TryGetProperty("violationStreak", out var streak) || streak.ValueKind != JsonValueKind.Number ||
            !streak.TryGetInt32(out var streakValue) || streakValue < 0)
            throw InvalidResponse("'violationStreak' is missing or is not a whole number");

        var restricted = false;
        string? restrictionType = null;
        DateTimeOffset? until = null;
        string? reason = null;
        if (payload.TryGetProperty("auctionRestriction", out var restriction) && restriction.ValueKind == JsonValueKind.Object)
        {
            restricted = RequireBool(restriction, "isRestricted");
            restrictionType = OptionalString(restriction, "type");
            reason = OptionalString(restriction, "reason");
            if (OptionalString(restriction, "until") is { } untilText && DateTimeOffset.TryParse(
                    untilText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                until = parsed;
        }

        return new UserAuctionDepositProfile(
            requested, status, reputation, isNewUser, streakValue, restricted, restrictionType, until, reason);
    }

    private static bool HasReason(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(OptionalString(payload, "failureReason"));

    // ------------------------------------------------------------------ Parsing helpers

    private static bool RequireBool(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        throw InvalidResponse($"'{name}' is missing or is not a boolean");
    }

    private static bool HasProperty(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out _);

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Guid? OptionalGuid(JsonElement payload, string name) =>
        OptionalString(payload, name) is { } text && Guid.TryParse(text, out var id) ? id : null;

    private static int? OptionalInt(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static void EnsureSameUser(JsonElement payload, Guid requested)
    {
        var returned = OptionalGuid(payload, "userId");
        if (returned is not null && returned != requested)
            throw InvalidResponse("the answer is about a different user");
    }

    private static DomainException InvalidResponse(string detail) =>
        new("USER_RESPONSE_INVALID", $"The user service returned an invalid response: {detail}.", 502);

    private sealed record CheckReputationQuery(string Action, Guid UserId);

    private sealed record BidEligibilityQuery(Guid UserId);

    private sealed record BatchInfoQuery(Guid[] UserIds);

    private sealed record RecordViolationBody(
        string ViolationType,
        Guid AuctionId,
        Guid OrderId,
        DateTimeOffset OccurredAt,
        decimal DepositForfeitedAmount);
}