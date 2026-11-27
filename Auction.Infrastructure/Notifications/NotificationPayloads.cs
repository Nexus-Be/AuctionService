namespace Auction.Infrastructure.Notifications;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Auction.Application.Interfaces;

/// <summary>
/// The request bodies of the notification routes and the event id of each one. The event id is derived from business data, so queueing or
/// sending the same event again always carries the same sourceEventId and Notification answers with the first result instead of sending twice.
/// </summary>
public static class NotificationPayloads
{
    public const string Outbid = "auction-outbids";
    public const string Winner = "auction-winners";
    public const string Failed = "auction-failed";
    public const string Cancelled = "auction-cancelled";
    public const string WinnerOffer = "auction-winner-offers";
    public const string OrderPaid = "auction-order-payments";
    public const string WatchlistNewBid = "auction-watchlist-bids";
    public const string ReportResolved = "auction-report-resolved";
    public const string Deposit = "auction-deposits";
    public const string ReserveApprovalNeeded = "auction-reserve-approval-needed";
    public const string ReserveRestriction = "auction-reserve-restriction";
    public const string SellerResult = "auction-seller-results";
    public const string PaymentTimeout = "auction-payment-timeouts";
    public const string SellerPaymentTimeout = "auction-seller-payment-timeouts";
    public const string Started = "auction-starts";
    public const string InApp = "in-app";

    /// <summary>One serializer for queueing and for sending, so the stored payload and the sent body are the same text.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathOf(string kind) => "/api/v1/notifications/" + kind;

    public static Guid DeterministicGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    // ------------------------------------------------------------------ event ids

    public static Guid OutbidId(BidOutbidNotification n) => DeterministicGuid($"auction-outbid:{n.TriggerBidId:N}");
    public static Guid WinnerId(AuctionWinnerNotification n) => DeterministicGuid($"auction-winner:{n.AuctionId:N}:{n.ResultId:N}:{n.WinnerUserId:N}");
    public static Guid FailedId(AuctionFailedNotification n) => DeterministicGuid($"auction-failed:{n.AuctionId:N}:{n.FailedAt:O}");
    public static Guid CancelledId(AuctionCancelledNotification n) => DeterministicGuid($"auction-cancelled:{n.AuctionId:N}:{n.CancelledAt:O}");
    public static Guid OfferId(ReplacementWinnerOfferNotification n) => DeterministicGuid($"auction-winner-offer:{n.OfferId:N}");
    public static Guid OrderPaidId(AuctionOrderPaidNotification n) => DeterministicGuid($"auction-order-paid:{n.ResultId:N}:{n.OrderId:N}");
    public static Guid WatchlistNewBidId(WatchlistNewBidNotification n) => DeterministicGuid($"auction-watchlist-bid:{n.TriggerBidId:N}");
    public static Guid ReportResolvedId(AuctionReportResolvedNotification n) => DeterministicGuid($"auction-report-resolved:{n.AuctionId:N}:{n.Outcome}:{n.ResolvedAt:O}");
    public static Guid ReserveApprovalId(ReserveApprovalNeededNotification n) => DeterministicGuid($"auction-reserve-approval:{n.AuctionId:N}:{n.ApprovalDeadlineAt:O}");
    public static Guid ReserveRestrictionId(ReserveRestrictionNotification n) => DeterministicGuid($"auction-reserve-restriction:{n.SellerUserId:N}:{n.ConsecutiveDeclineCount}:{n.OccurredAt:O}");
    public static Guid DepositId(AuctionDepositNotification n) => DeterministicGuid($"auction-deposit:{n.DepositId:N}:{n.DepositStatus}");
    public static Guid SellerResultId(AuctionSellerResultNotification n) => DeterministicGuid($"auction-seller-result:{n.AuctionId:N}:{n.ResultId:N}:{n.Outcome}:{n.WinnerUserId}");
    public static Guid PaymentTimeoutId(AuctionPaymentTimeoutNotification n) => DeterministicGuid($"auction-payment-timeout:{n.AuctionId:N}:{n.OrderId:N}:{n.WinnerUserId:N}");
    public static Guid SellerPaymentTimeoutId(AuctionSellerPaymentTimeoutNotification n) => DeterministicGuid($"auction-seller-payment-timeout:{n.AuctionId:N}:{n.OrderId:N}:{n.WinnerUserId:N}");
    public static Guid StartedId(AuctionStartedNotification n) => DeterministicGuid($"auction-start:{n.AuctionId:N}:{n.StartedAt:O}");
    public static Guid SubmittedId(AuctionSubmittedNotification n) => DeterministicGuid($"auction-submitted:{n.AuctionId:N}:{n.SubmittedAt:O}");
    public static Guid ApprovedId(AuctionApprovedNotification n) => DeterministicGuid($"auction-approved:{n.AuctionId:N}:{n.ApprovedAt:O}");

    // ------------------------------------------------------------------ bodies

    public static object BuildReserveApprovalNeeded(ReserveApprovalNeededNotification n) => new
    {
        sourceEventId = ReserveApprovalId(n),
        auctionId = n.AuctionId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        highestBidAmount = n.HighestBidAmount,
        reservePriceAmount = n.ReservePriceAmount,
        currency = "VND",
        approvalDeadlineAt = n.ApprovalDeadlineAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildReserveRestriction(ReserveRestrictionNotification n) => new
    {
        sourceEventId = ReserveRestrictionId(n),
        sellerUserId = n.SellerUserId,
        consecutiveDeclineCount = n.ConsecutiveDeclineCount,
        isRestricted = n.IsRestricted,
        restrictedUntil = n.IsRestricted ? n.RestrictedUntil : null,
        occurredAt = n.OccurredAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildOutbid(BidOutbidNotification n) => new
    {
        sourceEventId = OutbidId(n),
        auctionId = n.AuctionId,
        triggerBidId = n.TriggerBidId,
        previousBidId = n.PreviousBidId,
        previousHighestBidderUserId = n.PreviousHighestBidderUserId,
        newHighestBidderUserId = n.NewHighestBidderUserId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        previousAmount = n.PreviousAmount,
        newAmount = n.NewAmount,
        currency = "VND",
        occurredAt = n.OccurredAt,
        auctionEndsAt = n.AuctionEndsAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildWinner(AuctionWinnerNotification n, string email, string? fullName) => new
    {
        sourceEventId = WinnerId(n),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        winningBidId = n.WinningBidId,
        winnerUserId = n.WinnerUserId,
        sellerUserId = n.SellerUserId,
        winnerEmail = email,
        winnerFullName = Clip(fullName, 200),
        auctionTitle = CleanTitle(n.AuctionTitle),
        winningAmount = n.WinningAmount,
        currency = "VND",
        settledAt = n.SettledAt,
        paymentDeadline = n.PaymentDeadline
    };

    public static object BuildFailed(AuctionFailedNotification n) => new
    {
        sourceEventId = FailedId(n),
        auctionId = n.AuctionId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        reason = Clip(n.Reason, 1000) ?? "Auction failed.",
        failedAt = n.FailedAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildCancelled(AuctionCancelledNotification n) => new
    {
        sourceEventId = CancelledId(n),
        auctionId = n.AuctionId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        reason = Clip(n.Reason, 1000) ?? "Auction cancelled.",
        cancelledAt = n.CancelledAt,
        forced = n.Forced,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildReplacementWinnerOffer(ReplacementWinnerOfferNotification n) => new
    {
        sourceEventId = OfferId(n),
        auctionId = n.AuctionId,
        offerId = n.OfferId,
        bidId = n.BidId,
        bidderUserId = n.BidderUserId,
        previousWinnerUserId = n.PreviousWinnerUserId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        amount = n.Amount,
        currency = "VND",
        offeredAt = n.OfferedAt,
        expiresAt = n.ExpiresAt,
        traceId = Clip(n.TraceId, 100)
    };

    /// <summary>Only called once every field is known: the order data from Commerce and the shop id from the User service.</summary>
    public static object BuildOrderPaid(AuctionOrderPaidNotification n, Guid shopId, string orderNo, decimal totalAmount, DateTimeOffset sellerAcceptDeadline) => new
    {
        sourceEventId = OrderPaidId(n),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        winningBidId = n.WinningBidId,
        orderId = n.OrderId,
        shopId,
        buyerUserId = n.BuyerUserId,
        sellerUserId = n.SellerUserId,
        orderNo,
        totalAmount,
        currency = "VND",
        orderStatus = "Paid",
        auctionStatus = n.AuctionStatus,
        settlementStatus = "Paid",
        sellerAcceptStartedAt = n.PaidAt,
        paidAt = n.PaidAt,
        sellerAcceptDeadline,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildWatchlistNewBid(WatchlistNewBidNotification n) => new
    {
        sourceEventId = WatchlistNewBidId(n),
        auctionId = n.AuctionId,
        triggerBidId = n.TriggerBidId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        newAmount = n.NewAmount,
        currency = "VND",
        watcherUserIds = n.WatcherUserIds,
        occurredAt = n.OccurredAt,
        auctionEndsAt = n.AuctionEndsAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildReportResolved(AuctionReportResolvedNotification n) => new
    {
        sourceEventId = ReportResolvedId(n),
        auctionId = n.AuctionId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        outcome = n.Outcome,
        message = Clip(n.Message, 1000) ?? "Your auction report has been reviewed.",
        adminReason = Clip(n.AdminReason, 1000),
        reporterUserIds = n.ReporterUserIds,
        resolvedAt = n.ResolvedAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildDeposit(AuctionDepositNotification n) => new
    {
        sourceEventId = DepositId(n),
        auctionId = n.AuctionId,
        depositId = n.DepositId,
        bidderUserId = n.BidderUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        amount = n.Amount,
        currency = "VND",
        depositStatus = n.DepositStatus,
        reason = Clip(n.Reason, 200),
        occurredAt = n.OccurredAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildSellerResult(AuctionSellerResultNotification n) => new
    {
        sourceEventId = SellerResultId(n),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        sellerUserId = n.SellerUserId,
        outcome = n.Outcome,
        auctionTitle = CleanTitle(n.AuctionTitle),
        decidedAt = n.DecidedAt,
        winningBidId = n.WinningBidId,
        winnerUserId = n.WinnerUserId,
        orderId = n.OrderId,
        winningAmount = n.WinningAmount,
        currency = n.WinningAmount.HasValue ? "VND" : null,
        paymentDeadline = n.PaymentDeadline,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildPaymentTimeout(AuctionPaymentTimeoutNotification n, string email, string? fullName) => new
    {
        sourceEventId = PaymentTimeoutId(n),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        winningBidId = n.WinningBidId,
        orderId = n.OrderId,
        winnerUserId = n.WinnerUserId,
        sellerUserId = n.SellerUserId,
        winnerEmail = email,
        winnerFullName = Clip(fullName, 200),
        auctionTitle = CleanTitle(n.AuctionTitle),
        orderStatus = "Cancelled",
        auctionStatus = "Failed",
        settledAt = n.SettledAt,
        paymentDeadline = n.PaymentDeadline,
        timedOutAt = n.TimedOutAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildSellerPaymentTimeout(AuctionSellerPaymentTimeoutNotification n) => new
    {
        sourceEventId = SellerPaymentTimeoutId(n),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        winningBidId = n.WinningBidId,
        orderId = n.OrderId,
        winnerUserId = n.WinnerUserId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        orderStatus = "Cancelled",
        auctionStatus = "Failed",
        settledAt = n.SettledAt,
        paymentDeadline = n.PaymentDeadline,
        timedOutAt = n.TimedOutAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildStarted(AuctionStartedNotification n) => new
    {
        sourceEventId = StartedId(n),
        auctionId = n.AuctionId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        auctionStatus = "Active",
        scheduledStartAt = n.ScheduledStartAt,
        startedAt = n.StartedAt,
        auctionEndsAt = n.AuctionEndsAt,
        traceId = Clip(n.TraceId, 100)
    };

    public static object BuildSubmitted(AuctionSubmittedNotification n)
    {
        var title = CleanTitle(n.AuctionTitle);
        var vnStart = n.ScheduledStartAt.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm dd/MM/yyyy");
        return new
        {
            sourceEventId = SubmittedId(n),
            userId = n.SellerUserId,
            title = "Phiên đấu giá đang chờ duyệt",
            body = $"Phiên đấu giá \"{title}\" (lịch mở bán {vnStart}) đã được tạo thành công và đang chờ nhân viên sàn phê duyệt.",
            notificationType = "AuctionStarted",
            payload = JsonSerializer.Serialize(new
            {
                type = "AuctionStarted",
                auctionId = n.AuctionId,
                audience = "seller",
                status = "PendingApproval"
            }, Json)
        };
    }

    public static object BuildApproved(AuctionApprovedNotification n)
    {
        var title = CleanTitle(n.AuctionTitle);
        var vnStart = n.ScheduledStartAt.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm dd/MM/yyyy");
        return new
        {
            sourceEventId = ApprovedId(n),
            userId = n.SellerUserId,
            title = "Phiên đấu giá đã được duyệt",
            body = $"Phiên đấu giá \"{title}\" đã được phê duyệt và sẽ tự động mở bán vào lúc {vnStart}.",
            notificationType = "AuctionStarted",
            payload = JsonSerializer.Serialize(new
            {
                type = "AuctionStarted",
                auctionId = n.AuctionId,
                audience = "seller",
                status = "Scheduled"
            }, Json)
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The title Notification asks for: not empty, no control characters, at most 200 characters.</summary>
    public static string CleanTitle(string? title)
    {
        var cleaned = new string((title ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return "Auction";

        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    public static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
