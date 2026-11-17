namespace Auction.Infrastructure.Clients;

using System.Net;
using System.Net.Http.Json;
using System.Linq;
using System.Text.Json;
using Auction.Application.Interfaces;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Microsoft.Extensions.Logging;

/// <summary>
/// REST client of the Nexus Wallet service for auction deposit holds (Direction C: W1–W5).
/// Synchronous balance locking without external gateway redirect.
/// </summary>
public sealed class WalletClient : IWalletClient
{
    // Wallet exposes plain REST routes under /api/v1/wallets/holds (Direction C: W1–W5).
    private const string HoldsPath = "/api/v1/wallets/holds";

    private static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<WalletClient> _logger;

    public WalletClient(HttpClient http, ILogger<WalletClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<DepositHoldCreated> CreateDepositHoldAsync(
        CreateDepositHoldCommand command, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            referenceType = "AUCTION_DEPOSIT",
            referenceId = command.DepositId,
            ownerUserId = command.PayerUserId,
            amount = command.Amount,
            currency = command.Currency,
            metadata = new { auctionId = command.AuctionId, policyVersion = command.PolicyVersion }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, HoldsPath)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", ToIdempotencyKey(idempotencyKey));

        var (status, payload) = await SendAsync(request, CreateTimeout, cancellationToken);
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
            throw MapFailure(status, payload);

        var holdId = RequireString(payload, "holdId");
        // A missing status means the hold was just created (Held). An unknown one must not be read as Authorized,
        // or the buyer could bid without locked money; the deposit then stays Pending and is reconciled by reference.
        var statusText = OptionalString(payload, "status");
        var state = statusText is null
            ? DepositHoldState.Authorized
            : ParseHoldState(statusText, fallback: null) ?? throw InvalidResponse($"unknown hold status '{statusText}'");
        var authorizedAt = OptionalDate(payload, "authorizedAt") ?? OptionalDate(payload, "heldAt") ?? OptionalDate(payload, "createdAt");

        return new DepositHoldCreated(holdId, state, authorizedAt);
    }

    public async Task<DepositHoldSnapshot> GetDepositHoldAsync(string holdId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{HoldsPath}/{Uri.EscapeDataString(holdId)}");

        var (status, payload) = await SendAsync(request, ReadTimeout, cancellationToken);
        if (status == HttpStatusCode.NotFound)
            throw new DomainException("HOLD_NOT_FOUND", "The payment hold was not found.", 404);
        if (status != HttpStatusCode.OK)
            throw MapFailure(status, payload);

        return ParseSnapshot(payload, holdId);
    }

    public async Task<DepositHoldSnapshot?> GetDepositHoldByReferenceAsync(Guid referenceId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{HoldsPath}/by-reference/AUCTION_DEPOSIT/{Uri.EscapeDataString(referenceId.ToString())}");

        var (status, payload) = await SendAsync(request, ReadTimeout, cancellationToken);
        if (status == HttpStatusCode.NotFound)
            return null;
        if (status != HttpStatusCode.OK)
            throw MapFailure(status, payload);

        var holdId = RequireString(payload, "holdId");
        return ParseSnapshot(payload, holdId);
    }

    public async Task<DepositReleaseOutcome> ReleaseDepositHoldAsync(
        string holdId, string reason, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{HoldsPath}/{Uri.EscapeDataString(holdId)}/release")
        {
            Content = JsonContent.Create(new { reason }, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", ToIdempotencyKey(idempotencyKey));

        var (status, payload) = await SendAsync(request, ReadTimeout, cancellationToken);
        switch (status)
        {
            case HttpStatusCode.OK:
                return DepositReleaseOutcome.Released;
            case HttpStatusCode.NotFound:
                return DepositReleaseOutcome.AlreadyGone;
            case HttpStatusCode.Conflict:
                var code = ErrorCode(payload);
                if (code == "HOLD_ALREADY_CAPTURED")
                {
                    // Wallet answers this for every hold that is no longer Held, including one that was refunded.
                    // Only a hold that is really Captured still has the buyer's money with someone else.
                    var current = await GetDepositHoldAsync(holdId, cancellationToken);
                    return current.Status == DepositHoldState.Captured
                        ? DepositReleaseOutcome.AlreadyCaptured
                        : DepositReleaseOutcome.AlreadyGone;
                }
                if (code == "HOLD_EXPIRED")
                    return DepositReleaseOutcome.AlreadyGone;
                throw MapFailure(status, payload);
            default:
                throw MapFailure(status, payload);
        }
    }

    public async Task CaptureDepositHoldAsync(
        string holdId, CaptureDepositHoldCommand command, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            purpose = string.Equals(command.Purpose, "Forfeit", StringComparison.OrdinalIgnoreCase) ? "FORFEIT" : "ORDER_PAYMENT",
            orderId = command.OrderId,
            transfers = command.Transfers.Select(transfer => new
            {
                toType = transfer.DestinationType,
                toOwnerId = transfer.DestinationUserId,
                amount = transfer.Amount
            })
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{HoldsPath}/{Uri.EscapeDataString(holdId)}/capture")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", ToIdempotencyKey(idempotencyKey));

        var (status, payload) = await SendAsync(request, ReadTimeout, cancellationToken);
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
            throw MapFailure(status, payload);
    }

    public async Task RefundCapturedHoldAsync(
        string holdId, Guid orderId, decimal amount, string reason, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            reason,
            orderId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{HoldsPath}/{Uri.EscapeDataString(holdId)}/refund-captured")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", ToIdempotencyKey(idempotencyKey));

        var (status, payload) = await SendAsync(request, ReadTimeout, cancellationToken);
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
            throw MapFailure(status, payload);
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>Sends the request and returns the status with the useful part of the body (the "data" object when there is one).</summary>
    private async Task<(HttpStatusCode Status, JsonElement Payload)> SendAsync(
        HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await _http.SendAsync(request, timeoutSource.Token);
            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token);

            JsonElement payload = default;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    payload = (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                               data.ValueKind == JsonValueKind.Object
                        ? data
                        : root).Clone();
                }
                catch (JsonException)
                {
                    // A body that is not JSON only matters for the error code, and the status is enough without it.
                }
            }

            return (response.StatusCode, payload);
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Wallet service call failed.");
            throw new DomainException("WALLET_UNAVAILABLE", "The wallet service is unavailable.", 503);
        }
    }

    private DomainException MapFailure(HttpStatusCode status, JsonElement payload)
    {
        var code = ErrorCode(payload);
        _logger.LogWarning("Wallet service answered {Status} with code {Code}.", (int)status, code ?? "(none)");

        return (int)status switch
        {
            422 when code == "INSUFFICIENT_AVAILABLE_BALANCE" => new DomainException(
                code, "Insufficient available balance in wallet to hold the deposit.", 422)
            {
                Details = ExtractInsufficientBalanceDetails(payload)
            },
            // Wallet answers 403 for a frozen wallet and 404 for a missing wallet. Both are business refusals for the payer, so they map to 422.
            403 or 422 when code == "WALLET_FROZEN" => new DomainException(
                code, "The wallet is currently frozen.", 422),
            404 when code == "WALLET_NOT_FOUND" => new DomainException(
                code, "The payer wallet was not found.", 422),
            // The deposit reference already has a hold with other terms: it was closed before and cannot be reused (no re-deposit).
            409 when code == "HOLD_REFERENCE_CONFLICT" => new DomainException(
                "DEPOSIT_HOLD_NOT_ACTIVE", "The payment hold for this deposit is no longer active.", 409),
            422 when code == "PAYER_NOT_ELIGIBLE" => new DomainException(
                code, "The payer is not allowed to make this payment.", 422),
            >= 500 => new DomainException("WALLET_UNAVAILABLE", "The wallet service is unavailable.", 503),
            // 400, 401, 409 and other 422: the request itself is wrong (contract or key problem), which retrying will not fix.
            _ => new DomainException("PAYMENT_SERVICE_ERROR", "The wallet service rejected the request.", 502)
        };
    }

    private static object? ExtractInsufficientBalanceDetails(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        var source = payload.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Object
            ? detailsElement
            : payload;

        decimal? available = OptionalDecimal(source, "availableBalance");
        decimal? required = OptionalDecimal(source, "required");
        decimal? shortfall = OptionalDecimal(source, "shortfall");

        if (available is not null || required is not null || shortfall is not null)
        {
            return new
            {
                availableBalance = available ?? 0m,
                required = required ?? 0m,
                shortfall = shortfall ?? 0m
            };
        }

        if (payload.TryGetProperty("details", out var rawDetails) && rawDetails.ValueKind == JsonValueKind.Object)
            return rawDetails.Clone();

        return null;
    }

    private static DepositHoldSnapshot ParseSnapshot(JsonElement payload, string fallbackHoldId)
    {
        var stateText = RequireString(payload, "status");
        var state = ParseHoldState(stateText, fallback: null)
                    ?? throw InvalidResponse($"unknown hold status '{stateText}'");

        if (!payload.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number)
            throw InvalidResponse("'amount' is missing or is not a number");

        Guid? referenceId = Guid.TryParse(OptionalString(payload, "referenceId"), out var reference) ? reference : null;
        return new DepositHoldSnapshot(
            OptionalString(payload, "holdId") ?? fallbackHoldId, referenceId, state, OptionalString(payload, "mode"),
            amount.GetDecimal(), OptionalDate(payload, "authorizedAt") ?? OptionalDate(payload, "heldAt"),
            OptionalDate(payload, "providerHoldExpiresAt"),
            OptionalString(payload, "failureCode"));
    }

    private static DepositHoldState? ParseHoldState(string? stateText, DepositHoldState? fallback)
    {
        if (string.IsNullOrWhiteSpace(stateText))
            return fallback;

        if (string.Equals(stateText.Trim(), "Held", StringComparison.OrdinalIgnoreCase))
            return DepositHoldState.Authorized;

        if (Enum.TryParse<DepositHoldState>(stateText.Trim(), ignoreCase: true, out var state) && Enum.IsDefined(state))
            return state;

        return fallback;
    }

    /// <summary>
    /// Wallet expects the Idempotency-Key header to be a UUID. The same logical key (for example "deposit:{id}:1") always maps
    /// to the same UUID, so a retry is still recognised as the same request.
    /// </summary>
    private static string ToIdempotencyKey(string logicalKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(logicalKey))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40); // version 4 layout
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes).ToString();
    }

    // ------------------------------------------------------------------ Parsing helpers

    private static string? ErrorCode(JsonElement payload) => OptionalString(payload, "errorCode") ?? OptionalString(payload, "code");

    private static string RequireString(JsonElement payload, string name) =>
        OptionalString(payload, name) is { Length: > 0 } value
            ? value
            : throw InvalidResponse($"'{name}' is missing");

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? OptionalDecimal(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var dec)
            ? dec
            : null;

    private static DateTimeOffset? OptionalDate(JsonElement payload, string name) =>
        OptionalString(payload, name) is { } text &&
        DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    private static DomainException InvalidResponse(string detail) =>
        new("PAYMENT_RESPONSE_INVALID", $"The wallet service returned an invalid response: {detail}.", 502);
}
