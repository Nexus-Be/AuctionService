namespace Auction.Infrastructure.Clients;

using System.Net;
using System.Text;
using System.Text.Json;
using Auction.Application.Interfaces;
using Auction.Domain.Exceptions;
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
    private const int BatchLimit = 100;

    // Bidding is a hot path: a slow User service must not hold connections of Auction.
    private static readonly TimeSpan BuiltInBidTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan BuiltInDefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly UserClientOptions _options;
    private readonly ILogger<UserClient> _logger;
    private readonly TimeSpan _bidTimeout;
    private readonly TimeSpan _defaultTimeout;

    public UserClient(HttpClient http, UserClientOptions options, ILogger<UserClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
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
}