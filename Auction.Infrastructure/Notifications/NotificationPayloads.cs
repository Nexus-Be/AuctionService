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

    /// <summary>One serializer for queueing and for sending, so the stored payload and the sent body are the same text.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathOf(string kind) => "/api/v1/notifications/" + kind;

    public static Guid DeterministicGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    // ------------------------------------------------------------------ event ids

    public static Guid OutbidId(BidOutbidNotification n) => DeterministicGuid($"auction-outbid:{n.TriggerBidId:N}");
    public static Guid WinnerId(AuctionWinnerNotification n) => DeterministicGuid($"auction-winner:{n.AuctionId:N}:{n.ResultId:N}");
    public static Guid FailedId(AuctionFailedNotification n) => DeterministicGuid($"auction-failed:{n.AuctionId:N}:{n.FailedAt:O}");
    public static Guid CancelledId(AuctionCancelledNotification n) => DeterministicGuid($"auction-cancelled:{n.AuctionId:N}:{n.CancelledAt:O}");
    public static Guid OfferId(ReplacementWinnerOfferNotification n) => DeterministicGuid($"auction-winner-offer:{n.OfferId:N}");
    public static Guid OrderPaidId(AuctionOrderPaidNotification n) => DeterministicGuid($"auction-order-paid:{n.ResultId:N}:{n.OrderId:N}");

    // ------------------------------------------------------------------ bodies

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
