namespace Auction.Application.Interfaces;

using System;

/// <summary>
/// Gửi thông báo sang Notification service. Các hàm chỉ đưa thông báo vào hàng đợi rồi trả về ngay, không chờ và không ném lỗi,
/// để việc gửi thông báo không bao giờ làm chậm hay làm hỏng nghiệp vụ đấu giá. Việc gửi thật chạy nền và chỉ ghi log khi lỗi.
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
}

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
