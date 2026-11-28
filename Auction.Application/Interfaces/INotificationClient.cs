namespace Auction.Application.Interfaces;

using System;
using System.Collections.Generic;

/// <summary>
/// Queues a notification for the Notification service. The methods only <b>add a row to the notification outbox of the current unit of work</b>
/// (they do not save and do not call the network), so the caller must call them <b>before</b> the SaveChanges that stores the business change:
/// the event is then committed in the same transaction and a crash right after the commit cannot lose it. A worker sends the rows afterwards,
/// with retries, using the same event id and payload. When notifications are disabled the methods do nothing.
/// </summary>
public interface INotificationClient
{
    /// <summary>Báo cho bidder vừa bị vượt giá. Gọi sau khi lượt đặt giá mới đã được lưu.</summary>
    void NotifyBidOutbid(BidOutbidNotification notification);

    /// <summary>Báo cho người thắng khi phiên kết thúc. Gọi sau khi kết quả và settlement đã được lưu.</summary>
    void NotifyAuctionWinner(AuctionWinnerNotification notification);

    /// <summary>Báo cho seller khi phiên chuyển sang Failed sau khi trạng thái đã được lưu.</summary>
    void NotifyAuctionFailed(AuctionFailedNotification notification);

    /// <summary>Báo cho seller khi phiên bị huỷ sau khi trạng thái đã được lưu.</summary>
    void NotifyAuctionCancelled(AuctionCancelledNotification notification);

    /// <summary>Báo cho bidder kế tiếp khi họ được mời thay winner trước đó.</summary>
    void NotifyReplacementWinnerOffer(ReplacementWinnerOfferNotification notification);

    /// <summary>
    /// Tells buyer and seller that an auction order was paid. Queue it in the same SaveChanges that stores the settlement as Paid.
    /// Fields Commerce did not send (OrderNo, TotalAmount, SellerAcceptDeadline) are fetched by the worker before anything is sent.
    /// </summary>
    void NotifyAuctionOrderPaid(AuctionOrderPaidNotification notification);

    /// <summary>Notifies watchers (who opted in for bid alerts) that a new bid was placed on a watched auction.</summary>
    void NotifyWatchlistNewBid(WatchlistNewBidNotification notification);

    /// <summary>Notifies reporters of the admin's decision (thank-you after manual force-cancel, or dismissed with no violation found).</summary>
    void NotifyAuctionReportResolved(AuctionReportResolvedNotification notification);

    /// <summary>Notifies a bidder when their deposit is held, released, captured, or refunded.</summary>
    void NotifyAuctionDeposit(AuctionDepositNotification notification);

    /// <summary>Tells the seller that the highest bid is below the reserve price and a decision is needed before the approval deadline.</summary>
    void NotifyReserveApprovalNeeded(ReserveApprovalNeededNotification notification);

    /// <summary>Warns the seller after the 2nd consecutive refusal, or restricts them from the 3rd one (including a timeout).</summary>
    void NotifyReserveRestriction(ReserveRestrictionNotification notification);

    /// <summary>Tells the seller the outcome when an auction ends with a selected winner or without valid bids.</summary>
    void NotifyAuctionSellerResult(AuctionSellerResultNotification notification);

    /// <summary>Tells the buyer that their winning auction order timed out before payment was completed.</summary>
    void NotifyAuctionPaymentTimeout(AuctionPaymentTimeoutNotification notification);

    /// <summary>Tells the seller that the winning buyer did not pay within the 24-hour payment deadline.</summary>
    void NotifyAuctionSellerPaymentTimeout(AuctionSellerPaymentTimeoutNotification notification);

    /// <summary>Tells the seller that their auction has started running.</summary>
    void NotifyAuctionStarted(AuctionStartedNotification notification);

    /// <summary>Tells the seller that their newly created auction has been submitted and is awaiting staff approval.</summary>
    void NotifyAuctionSubmitted(AuctionSubmittedNotification notification);

    /// <summary>Tells the seller that their pending-approval auction has been approved by staff/admin and scheduled to start.</summary>
    void NotifyAuctionApproved(AuctionApprovedNotification notification);

    /// <summary>Tells the highest bidder that the auction ended below the reserve price and is awaiting seller approval.</summary>
    void NotifyBuyerBelowReservePending(BuyerBelowReservePendingNotification notification);

    /// <summary>Tells the highest bidder why the auction failed when the seller declined selling below the reserve price.</summary>
    void NotifyBuyerBelowReserveRejected(BuyerBelowReserveRejectedNotification notification);

    /// <summary>Sends a condolence in-app notification to bidders who participated in the auction but did not win.</summary>
    void NotifyAuctionLosers(AuctionLosersNotification notification);
}

public sealed record BuyerBelowReservePendingNotification(
    Guid AuctionId,
    Guid BuyerUserId,
    string? AuctionTitle,
    decimal HighestBidAmount,
    DateTimeOffset ApprovalDeadlineAt,
    string? TraceId = null);

public sealed record BuyerBelowReserveRejectedNotification(
    Guid AuctionId,
    Guid BuyerUserId,
    string? AuctionTitle,
    string? Reason,
    DateTimeOffset RejectedAt,
    string? TraceId = null);

public sealed record AuctionLosersNotification(
    Guid AuctionId,
    string? AuctionTitle,
    IReadOnlyList<Guid> LoserUserIds,
    DateTimeOffset EndedAt,
    string? TraceId = null);

public sealed record AuctionSubmittedNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset SubmittedAt,
    string? TraceId = null);

public sealed record AuctionApprovedNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ApprovedAt,
    string? TraceId = null);

public sealed record AuctionStartedNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset StartedAt,
    DateTimeOffset AuctionEndsAt,
    string? TraceId = null);

public sealed record AuctionSellerResultNotification(
    Guid AuctionId,
    Guid ResultId,
    Guid SellerUserId,
    string Outcome,
    string? AuctionTitle,
    DateTimeOffset DecidedAt,
    Guid? WinningBidId = null,
    Guid? WinnerUserId = null,
    Guid? OrderId = null,
    decimal? WinningAmount = null,
    DateTimeOffset? PaymentDeadline = null,
    string? TraceId = null);

public sealed record AuctionPaymentTimeoutNotification(
    Guid AuctionId,
    Guid ResultId,
    Guid WinningBidId,
    Guid OrderId,
    Guid WinnerUserId,
    Guid SellerUserId,
    string? AuctionTitle,
    DateTimeOffset SettledAt,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset TimedOutAt,
    string? TraceId = null);

public sealed record AuctionSellerPaymentTimeoutNotification(
    Guid AuctionId,
    Guid ResultId,
    Guid WinningBidId,
    Guid OrderId,
    Guid WinnerUserId,
    Guid SellerUserId,
    string? AuctionTitle,
    DateTimeOffset SettledAt,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset TimedOutAt,
    string? TraceId = null);

public sealed record ReserveApprovalNeededNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    decimal HighestBidAmount,
    decimal ReservePriceAmount,
    DateTimeOffset ApprovalDeadlineAt,
    string? TraceId = null);

public sealed record ReserveRestrictionNotification(
    Guid AuctionId,
    Guid SellerUserId,
    int ConsecutiveDeclineCount,
    bool IsRestricted,
    DateTimeOffset? RestrictedUntil,
    DateTimeOffset OccurredAt,
    string? TraceId = null);

public sealed record AuctionDepositNotification(
    Guid AuctionId,
    string? AuctionTitle,
    Guid DepositId,
    Guid BidderUserId,
    decimal Amount,
    string DepositStatus,
    string? Reason,
    DateTimeOffset OccurredAt,
    string? TraceId = null);

public sealed record WatchlistNewBidNotification(
    Guid AuctionId,
    string? AuctionTitle,
    Guid TriggerBidId,
    decimal NewAmount,
    IReadOnlyList<Guid> WatcherUserIds,
    DateTimeOffset OccurredAt,
    DateTimeOffset AuctionEndsAt,
    string? TraceId);

public sealed record AuctionReportResolvedNotification(
    Guid AuctionId,
    string? AuctionTitle,
    string Outcome,
    string Message,
    string? AdminReason,
    IReadOnlyList<Guid> ReporterUserIds,
    DateTimeOffset ResolvedAt,
    string? TraceId);

/// <summary>An auction order that was just paid. The auction itself is still Ended (it becomes Settled only after the seller accepts).</summary>
public sealed record AuctionOrderPaidNotification(
    Guid AuctionId,
    Guid ResultId,
    Guid WinningBidId,
    Guid OrderId,
    Guid BuyerUserId,
    Guid SellerUserId,
    DateTimeOffset PaidAt,
    string AuctionStatus,
    string? OrderNo,
    decimal? TotalAmount,
    DateTimeOffset? SellerAcceptDeadline,
    string? TraceId);

/// <summary>Dữ liệu thông báo bị vượt giá: bid mới (TriggerBidId) đã vượt bid đang dẫn đầu trước đó (PreviousBidId). Tiền tệ luôn là VND.</summary>
public sealed record BidOutbidNotification(
    Guid AuctionId,
    string? AuctionTitle,
    Guid TriggerBidId,
    Guid PreviousBidId,
    Guid PreviousHighestBidderUserId,
    Guid NewHighestBidderUserId,
    Guid SellerUserId,
    decimal PreviousAmount,
    decimal NewAmount,
    DateTimeOffset OccurredAt,
    DateTimeOffset AuctionEndsAt,
    string? TraceId);

/// <summary>Dữ liệu thông báo người thắng: kết quả đã chốt, giá chốt và hạn thanh toán. Email và tên người thắng do bên gửi tự tra từ User service.</summary>
public sealed record AuctionWinnerNotification(
    Guid AuctionId,
    string? AuctionTitle,
    Guid ResultId,
    Guid WinningBidId,
    Guid WinnerUserId,
    Guid SellerUserId,
    decimal WinningAmount,
    DateTimeOffset SettledAt,
    DateTimeOffset PaymentDeadline);

public sealed record AuctionFailedNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    string Reason,
    DateTimeOffset FailedAt,
    string? TraceId);

public sealed record AuctionCancelledNotification(
    Guid AuctionId,
    Guid SellerUserId,
    string? AuctionTitle,
    string? Reason,
    DateTimeOffset CancelledAt,
    bool Forced,
    string? TraceId);

public sealed record ReplacementWinnerOfferNotification(
    Guid AuctionId,
    Guid OfferId,
    Guid BidId,
    Guid BidderUserId,
    Guid PreviousWinnerUserId,
    Guid SellerUserId,
    string? AuctionTitle,
    decimal Amount,
    DateTimeOffset OfferedAt,
    DateTimeOffset ExpiresAt,
    string? TraceId);
