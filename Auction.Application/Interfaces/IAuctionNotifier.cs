namespace Auction.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Services.Auction;
using global::Auction.Application.Services.Bid;

/// <summary>
/// Thông báo realtime cho những người đang xem một phiên đấu giá. Chỉ được gọi sau khi dữ liệu đã được
/// lưu thành công. Các lớp cài đặt không được ném lỗi ra ngoài, vì thông báo thất bại không được làm
/// hỏng thao tác đã hoàn tất (đặt giá, huỷ phiên).
/// </summary>
public interface IAuctionNotifier
{
    /// <summary>Báo cho người đang xem phiên rằng vừa có một lượt đặt giá mới được lưu.</summary>
    Task BidPlacedAsync(Guid auctionId, BidPlacedMessage message, CancellationToken cancellationToken = default);

    /// <summary>Báo cho người đang xem phiên rằng phiên vừa bị huỷ.</summary>
    Task AuctionCancelledAsync(Guid auctionId, AuctionCancelledMessage message, CancellationToken cancellationToken = default);

    /// <summary>Báo cho người đang xem phiên rằng trạng thái phiên vừa đổi (Running, Ended, Settled, Failed...).</summary>
    Task AuctionStatusChangedAsync(Guid auctionId, AuctionStatusChangedMessage message, CancellationToken cancellationToken = default);
}