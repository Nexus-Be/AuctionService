namespace Auction.Infrastructure.Notifications;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auction.Application.Interfaces;
using Auction.Domain.Entities;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sends the notifications saved in the outbox (see <see cref="NotificationClient"/>) to the Notification service through the Gateway.
/// A row is picked with a lease (an atomic conditional UPDATE), so several instances never send the same row. Rows that still miss data
/// (AwaitingData) are completed first and only a complete payload is ever frozen and sent. A temporary failure (timeout, 408, 429, 5xx) is
/// retried with growing delays and always with the same event id and payload; a permanent failure (a 4xx answer: the contract, the token,
/// a conflicting event) is not retried and the row becomes NeedsAttention. A row that has not worked 24 hours after it was created also
/// becomes NeedsAttention. Notification treats a repeated event id as the same event, so sending a row again after a crash is safe.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    /// <summary>Named HttpClient that calls the Gateway (registered in Program.cs).</summary>
    public const string NotificationHttpClient = "notification-service";

    /// <summary>Named HttpClient that calls the User service for contact and shop data (registered in Program.cs).</summary>
    public const string UserHttpClient = "notification-user-contact";

    private const string UserContactPath = "/api/v1/internal/users/contact-info";
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IServiceScopeFactory scopes, IHttpClientFactory httpFactory, NotificationOptions options, TimeProvider clock, ILogger<NotificationDispatcher> logger)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Notifications are disabled (Notification:Enabled is false).");
            return;
        }

        if (!ServiceTokenFactory.IsUsable(_options.SigningKey))
            _logger.LogError("Notification:SigningKey (AUCTION_NOTIFICATION_SIGNING_KEY) is missing or shorter than 32 bytes: nothing can be sent, queued notifications will wait.");

        _logger.LogInformation("Notification dispatcher started. Gateway: {BaseUrl}.", _options.BaseUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The notification outbox scan failed; it will run again.");
            }

            try
            {
                await Task.Delay(handled > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One scan: gives up rows that are too old, then claims and handles the rows that are due. Returns how many rows it handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _clock.GetUtcNow();

        await db.NotificationOutbox
            .Where(x => (x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.AwaitingData) && x.GiveUpAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, NotificationOutboxStatus.NeedsAttention)
                .SetProperty(x => x.LastError, x => x.LastError ?? "GAVE_UP_AFTER_24H")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        var due = await db.NotificationOutbox.AsNoTracking()
            .Where(x => (x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.AwaitingData)
                        && x.NextRetryAt <= now && (x.LockedUntil == null || x.LockedUntil < now))
            .OrderBy(x => x.NextRetryAt)
            .Select(x => x.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var handled = 0;
        foreach (var id in due)
        {
            // Atomic claim: only one instance gets the row. The lease frees it again if this instance dies half way.
            var claimed = await db.NotificationOutbox
                .Where(x => x.Id == id
                            && (x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.AwaitingData)
                            && x.NextRetryAt <= now && (x.LockedUntil == null || x.LockedUntil < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.LockedUntil, now.Add(Lease))
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
            if (claimed == 0)
                continue;

            db.ChangeTracker.Clear();
            var item = await db.NotificationOutbox.SingleAsync(x => x.Id == id, cancellationToken);
            await HandleAsync(scope.ServiceProvider, db, item, cancellationToken);
            handled++;
        }

        return handled;
    }

    private async Task HandleAsync(IServiceProvider services, AppDbContext db, NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Status == NotificationOutboxStatus.AwaitingData)
            {
                var completion = await CompleteAsync(services, item, cancellationToken);
                if (completion.Payload is null)
                {
                    await FinishAsync(db, item, completion.Permanent ? Outcome.Permanent : Outcome.Temporary, completion.Error, cancellationToken);
                    return;
                }

                item.PayloadJson = completion.Payload;
                item.Status = NotificationOutboxStatus.Pending;
                item.ContextJson = null;
                await db.SaveChangesAsync(cancellationToken);
            }

            var result = await SendAsync(item, cancellationToken);
            await FinishAsync(db, item, result.Outcome, result.Error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handling the {Kind} notification {EventId} failed.", item.Kind, item.EventId);
            await FinishAsync(db, item, Outcome.Temporary, ex.GetType().Name + ": " + ex.Message, cancellationToken);
        }
    }

    public enum Outcome { Accepted, Temporary, Permanent }

    public sealed record Attempt(Outcome Outcome, string? Error);

    private async Task FinishAsync(AppDbContext db, NotificationOutboxItem item, Outcome outcome, string? error, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        item.LockedUntil = null;
        item.UpdatedAt = now;
        item.LastError = NotificationPayloads.Clip(error, 500);

        switch (outcome)
        {
            case Outcome.Accepted:
                item.Status = NotificationOutboxStatus.Sent;
                item.SentAt = now;
                item.LastError = null;
                _logger.LogInformation("The {Kind} notification {EventId} of auction {AuctionId} was accepted by the Notification service.",
                    item.Kind, item.EventId, item.AuctionId);
                break;

            case Outcome.Permanent:
                item.Status = NotificationOutboxStatus.NeedsAttention;
                _logger.LogError("The {Kind} notification {EventId} of auction {AuctionId} needs attention and will not be retried: {Error}",
                    item.Kind, item.EventId, item.AuctionId, item.LastError);
                break;

            default:
                if (now >= item.GiveUpAt)
                {
                    item.Status = NotificationOutboxStatus.NeedsAttention;
                    item.LastError ??= "GAVE_UP_AFTER_24H";
                    _logger.LogError("The {Kind} notification {EventId} of auction {AuctionId} was given up after 24 hours: {Error}",
                        item.Kind, item.EventId, item.AuctionId, item.LastError);
                }
                else
                {
                    item.NextRetryAt = now.Add(NotificationOptions.Backoff(item.AttemptCount));
                    _logger.LogWarning("The {Kind} notification {EventId} of auction {AuctionId} will be retried at {RetryAt} (attempt {Attempt}): {Error}",
                        item.Kind, item.EventId, item.AuctionId, item.NextRetryAt, item.AttemptCount, item.LastError);
                }

                break;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ sending

    private async Task<Attempt> SendAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        if (!ServiceTokenFactory.IsUsable(_options.SigningKey))
            return new Attempt(Outcome.Temporary, "SIGNING_KEY_MISSING");

        var http = _httpFactory.CreateClient(NotificationHttpClient);
        using var request = new HttpRequestMessage(HttpMethod.Post, NotificationPayloads.PathOf(item.Kind))
        {
            Content = new StringContent(item.PayloadJson ?? "{}", Encoding.UTF8, "application/json")
        };
        // A fresh token per attempt: it lives two minutes, a retry minutes later needs a new one.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", ServiceTokenFactory.Create(_options.SigningKey!, _options.ServiceUserId, _clock.GetUtcNow()));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return Classify((int)response.StatusCode, response.IsSuccessStatusCode ? null : await ReadSnippetAsync(response, cancellationToken));
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return new Attempt(Outcome.Temporary, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>2xx accepted; 408, 429 and 5xx are temporary; every other answer is a permanent refusal of this exact request.</summary>
    public static Attempt Classify(int status, string? detail)
    {
        if (status >= 200 && status < 300)
            return new Attempt(Outcome.Accepted, null);
        if (status >= 500 || status == 408 || status == 429)
            return new Attempt(Outcome.Temporary, $"HTTP {status}: {detail}");
        return new Attempt(Outcome.Permanent, $"HTTP {status}: {detail}");
    }

    // ------------------------------------------------------------------ completing rows that wait for data

    private sealed record Completion(string? Payload, bool Permanent, string? Error);

    private static Completion Done(string payload) => new(payload, false, null);
    private static Completion Wait(string error) => new(null, false, error);
    private static Completion Fail(string error) => new(null, true, error);

    private async Task<Completion> CompleteAsync(IServiceProvider services, NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ContextJson))
            return Fail("CONTEXT_MISSING");

        switch (item.Kind)
        {
            case NotificationPayloads.Winner:
            {
                var notice = JsonSerializer.Deserialize<AuctionWinnerNotification>(item.ContextJson, NotificationPayloads.Json);
                if (notice is null)
                    return Fail("CONTEXT_INVALID");

                var contact = await ResolveWinnerContactAsync(notice.WinnerUserId, cancellationToken);
                return contact is null
                    ? Wait("WINNER_EMAIL_UNAVAILABLE")
                    : Done(JsonSerializer.Serialize(NotificationPayloads.BuildWinner(notice, contact.Email, contact.FullName), NotificationPayloads.Json));
            }

            case NotificationPayloads.OrderPaid:
            {
                var notice = JsonSerializer.Deserialize<AuctionOrderPaidNotification>(item.ContextJson, NotificationPayloads.Json);
                return notice is null ? Fail("CONTEXT_INVALID") : await CompleteOrderPaidAsync(services, notice, cancellationToken);
            }

            default:
                return Fail("UNKNOWN_KIND_" + item.Kind);
        }
    }

    private async Task<Completion> CompleteOrderPaidAsync(IServiceProvider services, AuctionOrderPaidNotification notice, CancellationToken cancellationToken)
    {
        var orderNo = notice.OrderNo;
        var total = notice.TotalAmount;
        var deadline = notice.SellerAcceptDeadline;
        var paidAt = notice.PaidAt;

        // Callbacks that predate the extra fields (or a Commerce that was not upgraded) did not carry the order data: ask Commerce.
        if (string.IsNullOrWhiteSpace(orderNo) || total is null || total <= 0m || deadline is null)
        {
            try
            {
                var commerce = services.GetRequiredService<ICommerceClient>();
                var snapshot = await commerce.GetOrderNotificationSnapshotAsync(notice.OrderId, cancellationToken);
                if (snapshot is null)
                    return Fail("ORDER_SNAPSHOT_UNAVAILABLE");
                if (snapshot.BuyerId != notice.BuyerUserId || snapshot.SellerId != notice.SellerUserId)
                    return Fail("ORDER_SNAPSHOT_MISMATCH");
                if (string.Equals(snapshot.Status, "PendingPayment", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(snapshot.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                    return Fail("ORDER_SNAPSHOT_MISMATCH");

                orderNo = snapshot.OrderNo;
                total = snapshot.TotalAmount;
                deadline = snapshot.SellerRespondDeadlineAt;
                paidAt = snapshot.PaidAt;
            }
            catch (DomainException ex) when (ex.StatusCode == 503)
            {
                return Wait("ORDER_SNAPSHOT_TEMPORARILY_UNAVAILABLE");
            }
        }

        var shop = await ResolveShopAsync(notice.SellerUserId, cancellationToken);
        if (shop.Temporary)
            return Wait("SHOP_LOOKUP_TEMPORARILY_UNAVAILABLE");
        if (shop.ShopId is null)
            return Fail("SHOP_ID_UNRESOLVED");

        var complete = notice with { PaidAt = paidAt };
        return Done(JsonSerializer.Serialize(
            NotificationPayloads.BuildOrderPaid(complete, shop.ShopId.Value, orderNo!.Trim(), total!.Value, deadline!.Value), NotificationPayloads.Json));
    }

    private sealed record ShopLookup(Guid? ShopId, bool Temporary);

    /// <summary>The shop id is the seller profile id, not the seller's user id: the two are different values.</summary>
    private async Task<ShopLookup> ResolveShopAsync(Guid sellerUserId, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpFactory.CreateClient(UserHttpClient);
            using var response = await http.GetAsync($"/api/v1/internal/users/{sellerUserId}/shop", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ShopLookup(null, false);
            if (!response.IsSuccessStatusCode)
                return new ShopLookup(null, true);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;

            return root.TryGetProperty("shopId", out var shopId) && shopId.ValueKind == JsonValueKind.String
                && Guid.TryParse(shopId.GetString(), out var parsed) && parsed != Guid.Empty
                    ? new ShopLookup(parsed, false)
                    : new ShopLookup(null, false);
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not get the shop of seller {UserId}: {Message}", sellerUserId, ex.Message);
            return new ShopLookup(null, true);
        }
    }

    private sealed record WinnerContact(string Email, string? FullName);

    /// <summary>The winner's mail address: the test address when set, otherwise the User service. Null when it cannot be had (yet).</summary>
    private async Task<WinnerContact?> ResolveWinnerContactAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_options.DevWinnerEmail is not null)
            return new WinnerContact(_options.DevWinnerEmail, null);

        try
        {
            var http = _httpFactory.CreateClient(UserHttpClient);
            using var content = new StringContent(JsonSerializer.Serialize(new { userId }, NotificationPayloads.Json), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(UserContactPath, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The User service answered HTTP {Status} when asked for the contact of user {UserId}.", (int)response.StatusCode, userId);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;

            var email = ReadString(root, "email");
            return string.IsNullOrWhiteSpace(email) ? null : new WinnerContact(email.Trim(), ReadString(root, "fullName"));
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not get the contact of user {UserId}: {Message}", userId, ex.Message);
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length <= 300 ? text : text[..300];
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
        {
            return string.Empty;
        }
    }
}
