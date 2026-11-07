namespace Auction.Application.Services.Bid;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IBidService
{
    /// <summary>Lấy lịch sử đặt giá của một phiên (mới nhất trước), tên người đặt giá đã được che.</summary>
    Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Lấy các lượt đặt giá của một người dùng qua mọi phiên, mới nhất trước.</summary>
    Task<PagedData<MyBidDto>> ListMyBidsAsync(Guid bidderId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đặt giá cho một phiên. RowVersion của phiên phải khớp (If-Match), và cùng Idempotency-Key với cùng nội dung
    /// thì trả kết quả lần đầu thay vì tạo bid thứ hai.
    /// </summary>
    Task<PlaceBidResponse> PlaceBidAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, string? ifMatch,
        PlaceBidRequest request, CancellationToken cancellationToken = default);
}