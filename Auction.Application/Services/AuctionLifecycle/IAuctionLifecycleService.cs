namespace Auction.Application.Services.AuctionLifecycle;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Tự chuyển trạng thái phiên đấu giá theo thời gian: Scheduled sang Running khi tới giờ bắt đầu,
/// Running sang Ended (có người thắng) hoặc Failed (không có bid hợp lệ) khi hết giờ kết thúc.
/// Ngoài ra đóng các lời mời mua thay (winner offer) đã quá hạn mà bidder không phản hồi.
/// Trạng thái Draft không được dùng nên không bao giờ được xử lý.
/// </summary>
public interface IAuctionLifecycleService
{
    /// <summary>Tìm mã các phiên Scheduled đã tới giờ bắt đầu, phiên bắt đầu sớm nhất trước, tối đa limit phiên.</summary>
    Task<IReadOnlyList<Guid>> FindAuctionsToStartAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Tìm mã các phiên Running đã hết giờ kết thúc (đã tính gia hạn), phiên hết sớm nhất trước, tối đa limit phiên.</summary>
    Task<IReadOnlyList<Guid>> FindAuctionsToEndAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Chuyển một phiên từ Scheduled sang Running. Trả false nếu phiên không còn đủ điều kiện hoặc bị xử lý đồng thời bởi nơi khác.</summary>
    Task<bool> StartAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Kết thúc một phiên Running: Ended nếu có người thắng, Failed nếu không có bid hợp lệ. Trả false nếu phiên không còn đủ điều kiện hoặc bị xử lý đồng thời.</summary>
    Task<bool> EndAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Tìm mã các lời mời mua thay còn Pending nhưng đã quá hạn phản hồi, lời mời hết hạn sớm nhất trước, tối đa limit lời mời.</summary>
    Task<IReadOnlyList<Guid>> FindWinnerOffersToExpireAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đóng một lời mời quá hạn: lời mời sang Expired; phiên đang Ended thì sang Failed và nhả sản phẩm.
    /// Trả false nếu lời mời không còn đủ điều kiện (đã được phản hồi, chưa tới hạn) hoặc bị xử lý đồng thời bởi nơi khác.
    /// </summary>
    Task<bool> ExpireWinnerOfferAsync(Guid offerId, CancellationToken cancellationToken = default);
}