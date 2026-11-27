namespace Auction.Infrastructure.Notifications;

using System;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Auction.Application.Interfaces;
using Auction.Domain.Entities;
using Auction.Domain.Enums;

/// <summary>
/// Settings for the notifications sent to the Notification service through the Gateway.
/// Enabled (Notification:Enabled, off by default); BaseUrl (Notification:BaseUrl): the Gateway, not Notification itself;
/// ServiceUserId (Notification:ServiceUserId): the subject of the service token; SigningKey (AUCTION_NOTIFICATION_SIGNING_KEY or
/// Notification:SigningKey): the secret shared with the Gateway and Notification, at least 32 bytes, never logged;
/// DevWinnerEmail (Notification:DevWinnerEmail): test only, every winner mail goes to this address and the user lookup is skipped.
/// </summary>
public sealed record NotificationOptions(bool Enabled, string BaseUrl, Guid ServiceUserId, string? DevWinnerEmail, string? SigningKey = null)
{
    /// <summary>How long a notification may keep failing or waiting for data before it needs a person.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>Longest wait between two attempts.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    public static readonly Guid DefaultServiceUserId = new("00000000-0000-0000-0000-0000000000a1");

    public static NotificationOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = !bool.TryParse(configuration["Notification:Enabled"], out var parsedEnabled) || parsedEnabled;
        var baseUrl = configuration["Notification:BaseUrl"];
        var serviceUser = Guid.TryParse(configuration["Notification:ServiceUserId"], out var parsedId) && parsedId != Guid.Empty
            ? parsedId
            : DefaultServiceUserId;
        var devEmail = configuration["Notification:DevWinnerEmail"];
        var key = Environment.GetEnvironmentVariable("AUCTION_NOTIFICATION_SIGNING_KEY")
            ?? configuration["Notification:SigningKey"]
            ?? configuration["AuctionNotificationAuth:SigningKey"]
            ?? configuration["InternalSecurity:ServiceKey"];

        return new NotificationOptions(
            enabled,
            string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8007" : baseUrl.Trim(),
            serviceUser,
            string.IsNullOrWhiteSpace(devEmail) ? null : devEmail.Trim(),
            string.IsNullOrWhiteSpace(key) ? null : key);
    }

    /// <summary>Delay before the next attempt: 15 s, 30 s, 1 min, 2 min ... capped at <see cref="MaxBackoff"/>.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = 15d * Math.Pow(2, Math.Max(0, attempt - 1));
        var delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
        return delay;
    }
}

/// <summary>
/// Writes notifications to the outbox of the current unit of work (see <see cref="INotificationClient"/>). It never saves and never calls the
/// network: the row is committed together with the business change that caused it, and <see cref="NotificationDispatcher"/> sends it.
/// Scoped, so it shares the DbContext of the service that calls it.
/// </summary>
public sealed class NotificationClient : INotificationClient
{
    private readonly IAuctionDbContext _db;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationClient> _logger;

    public NotificationClient(IAuctionDbContext db, NotificationOptions options, ILogger<NotificationClient> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public void NotifyBidOutbid(BidOutbidNotification notification) =>
        Add(NotificationPayloads.Outbid, notification.AuctionId, NotificationPayloads.OutbidId(notification),
            NotificationPayloads.BuildOutbid(notification), context: null);

    public void NotifyAuctionWinner(AuctionWinnerNotification notification) =>
        // The mail address is not known here: the worker fetches it from the User service, then freezes the payload.
        Add(NotificationPayloads.Winner, notification.AuctionId, NotificationPayloads.WinnerId(notification),
            payload: null, context: notification);

    public void NotifyAuctionFailed(AuctionFailedNotification notification) =>
        Add(NotificationPayloads.Failed, notification.AuctionId, NotificationPayloads.FailedId(notification),
            NotificationPayloads.BuildFailed(notification), context: null);

    public void NotifyAuctionCancelled(AuctionCancelledNotification notification) =>
        Add(NotificationPayloads.Cancelled, notification.AuctionId, NotificationPayloads.CancelledId(notification),
            NotificationPayloads.BuildCancelled(notification), context: null);

    public void NotifyReplacementWinnerOffer(ReplacementWinnerOfferNotification notification) =>
        Add(NotificationPayloads.WinnerOffer, notification.AuctionId, NotificationPayloads.OfferId(notification),
            NotificationPayloads.BuildReplacementWinnerOffer(notification), context: null);

    public void NotifyAuctionOrderPaid(AuctionOrderPaidNotification notification) =>
        // The shop id (User service) and, for callbacks that did not carry them, the order number, total and seller deadline (Commerce)
        // are fetched by the worker first; only a complete payload is ever frozen and sent.
        Add(NotificationPayloads.OrderPaid, notification.AuctionId, NotificationPayloads.OrderPaidId(notification),
            payload: null, context: notification);

    public void NotifyWatchlistNewBid(WatchlistNewBidNotification notification)
    {
        if (notification.WatcherUserIds.Count == 0)
            return;

        Add(NotificationPayloads.WatchlistNewBid, notification.AuctionId, NotificationPayloads.WatchlistNewBidId(notification),
            NotificationPayloads.BuildWatchlistNewBid(notification), context: null);
    }

    public void NotifyAuctionReportResolved(AuctionReportResolvedNotification notification)
    {
        if (notification.ReporterUserIds.Count == 0)
            return;

        Add(NotificationPayloads.ReportResolved, notification.AuctionId, NotificationPayloads.ReportResolvedId(notification),
            NotificationPayloads.BuildReportResolved(notification), context: null);
    }

    public void NotifyAuctionDeposit(AuctionDepositNotification notification) =>
        Add(NotificationPayloads.Deposit, notification.AuctionId, NotificationPayloads.DepositId(notification),
            NotificationPayloads.BuildDeposit(notification), context: null);

    public void NotifyReserveApprovalNeeded(ReserveApprovalNeededNotification notification) =>
        Add(NotificationPayloads.ReserveApprovalNeeded, notification.AuctionId, NotificationPayloads.ReserveApprovalId(notification),
            NotificationPayloads.BuildReserveApprovalNeeded(notification), context: null);

    public void NotifyReserveRestriction(ReserveRestrictionNotification notification)
    {
        // Only the 2nd refusal (warning) and the 3rd and later (restriction) are sent; the 1st carries no message.
        if (notification.ConsecutiveDeclineCount < 2)
            return;

        Add(NotificationPayloads.ReserveRestriction, notification.AuctionId, NotificationPayloads.ReserveRestrictionId(notification),
            NotificationPayloads.BuildReserveRestriction(notification), context: null);
    }

    public void NotifyAuctionSellerResult(AuctionSellerResultNotification notification) =>
        Add(NotificationPayloads.SellerResult, notification.AuctionId, NotificationPayloads.SellerResultId(notification),
            NotificationPayloads.BuildSellerResult(notification), context: null);

    public void NotifyAuctionPaymentTimeout(AuctionPaymentTimeoutNotification notification) =>
        Add(NotificationPayloads.PaymentTimeout, notification.AuctionId, NotificationPayloads.PaymentTimeoutId(notification),
            payload: null, context: notification);

    public void NotifyAuctionSellerPaymentTimeout(AuctionSellerPaymentTimeoutNotification notification) =>
        Add(NotificationPayloads.SellerPaymentTimeout, notification.AuctionId, NotificationPayloads.SellerPaymentTimeoutId(notification),
            NotificationPayloads.BuildSellerPaymentTimeout(notification), context: null);

    public void NotifyAuctionStarted(AuctionStartedNotification notification) =>
        Add(NotificationPayloads.Started, notification.AuctionId, NotificationPayloads.StartedId(notification),
            NotificationPayloads.BuildStarted(notification), context: null);

    public void NotifyAuctionSubmitted(AuctionSubmittedNotification notification) =>
        Add(NotificationPayloads.InApp, notification.AuctionId, NotificationPayloads.SubmittedId(notification),
            NotificationPayloads.BuildSubmitted(notification), context: null);

    public void NotifyAuctionApproved(AuctionApprovedNotification notification) =>
        Add(NotificationPayloads.InApp, notification.AuctionId, NotificationPayloads.ApprovedId(notification),
            NotificationPayloads.BuildApproved(notification), context: null);

    private void Add(string kind, Guid auctionId, Guid eventId, object? payload, object? context)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Notifications are disabled: the {Kind} notification of auction {AuctionId} was not queued.", kind, auctionId);
            return;
        }

        // The same event queued twice in one unit of work is one event.
        if (_db.NotificationOutbox.Local.Any(x => x.EventId == eventId))
            return;

        var now = DateTimeOffset.UtcNow;
        _db.NotificationOutbox.Add(new NotificationOutboxItem
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            Kind = kind,
            AuctionId = auctionId,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, NotificationPayloads.Json),
            ContextJson = context is null ? null : JsonSerializer.Serialize(context, NotificationPayloads.Json),
            Status = payload is null ? NotificationOutboxStatus.AwaitingData : NotificationOutboxStatus.Pending,
            NextRetryAt = now,
            GiveUpAt = now.Add(NotificationOptions.GiveUpAfter),
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
