namespace Auction.Application.Interfaces;

using System;

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
}

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
