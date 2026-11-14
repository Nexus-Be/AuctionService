namespace Auction.Application.Services.Auction;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IAuctionService
{
    /// <summary>Lấy danh sách phiên đấu giá có phân trang, lọc theo trạng thái (tuỳ chọn).</summary>
    Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách phiên của riêng một seller, có phân trang và lọc theo trạng thái.</summary>
    Task<PagedData<AuctionSummaryDto>> ListSellerAuctionsAsync(Guid sellerId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Lấy số liệu thống kê các phiên thành công và thất bại của seller.</summary>
    Task<SellerAuctionStatisticsDto> GetSellerAuctionStatisticsAsync(Guid sellerId, CancellationToken cancellationToken = default);

    /// <summary>Lấy lịch sử đổi trạng thái của một phiên (mới nhất trước). Chỉ seller sở hữu phiên hoặc admin/staff được xem.</summary>
    Task<PagedData<AuctionStatusHistoryDto>> GetStatusHistoryAsync(ActorContext actor, bool isPrivileged, Guid auctionId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Lấy kết quả của phiên (giai đoạn hậu đấu giá, người thắng, giá chốt, settlement, cọc của tôi và lời mời người về nhì) theo vai trò người gọi (API 06).</summary>
    Task<AuctionResultDto> GetResultAsync(Guid auctionId, Guid? callerUserId = null, bool isPrivileged = false, CancellationToken cancellationToken = default);

    /// <summary>Người về nhì xem lời mời mua lại của chính mình trong một phiên đấu giá cụ thể (API 09).</summary>
    Task<MySecondChanceOfferDto> GetMySecondChanceOfferAsync(Guid auctionId, Guid bidderId, CancellationToken cancellationToken = default);

    /// <summary>Tiếp nhận kết quả thanh toán từ Commerce và chuyển phiên đã Ended sang Settled hoặc Failed. Gửi lại cùng eventId thì trả kết quả cũ.</summary>
    Task<SettlementPaymentStatusResponse> HandlePaymentStatusAsync(Guid auctionId, SettlementPaymentStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary>Dành cho Catalog: lấy trạng thái phiên đấu giá gần nhất của một sản phẩm.</summary>
    Task<ProductAuctionStatusDto> GetProductAuctionStatusAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>Dành cho Catalog: lấy giá, bước giá, người dẫn đầu và lịch của một phiên.</summary>
    Task<AuctionInternalSummaryDto> GetInternalSummaryAsync(Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Lấy chi tiết một phiên đấu giá.</summary>
    Task<AuctionDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Seller tạo phiên đấu giá mới, trạng thái ban đầu là Scheduled.</summary>
    Task<AuctionCreatedResponse> CreateAsync(ActorContext actor, CreateAuctionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Seller sửa phiên của mình trước khi phiên bắt đầu.</summary>
    Task<AuctionUpdatedResponse> UpdateAsync(ActorContext actor, Guid auctionId, UpdateAuctionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Seller huỷ phiên của mình trước khi phiên bắt đầu.</summary>
    Task<AuctionCancelledResponse> CancelAsync(ActorContext actor, Guid auctionId, CancelAuctionRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Admin huỷ cưỡng chế phiên chưa kết thúc, bắt buộc có lý do.</summary>
    Task<AuctionCancelledResponse> ForceCancelAsync(ActorContext actor, Guid auctionId, ForceCancelAuctionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Admin xem tiến trình hậu đấu giá (saga) của một phiên: các bước, bước hiện tại, hạn đang chờ và cờ quá hạn. Phiên không tồn tại trả 404.</summary>
    Task<AuctionSagaDto> GetSagaAsync(Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Bidder được mời chấp nhận trở thành người thắng thay người thắng trước. Phiên phải còn Ended và lời mời còn Pending, chưa hết hạn.</summary>
    Task<WinnerOfferResponse> AcceptWinnerOfferAsync(ActorContext actor, Guid auctionId, Guid offerId, CancellationToken cancellationToken = default);

    /// <summary>Bidder được mời từ chối lời mời. Phiên chuyển sang Failed và sản phẩm được nhả.</summary>
    Task<WinnerOfferResponse> DeclineWinnerOfferAsync(ActorContext actor, Guid auctionId, Guid offerId, DeclineWinnerOfferRequest request, CancellationToken cancellationToken = default);

    /// <summary>Người về nhì chấp nhận lời mời mua theo giá bid của chính mình (API 07: POST /api/v1/auctions/{id}/second-chance/accept).</summary>
    Task<SecondChanceAcceptResponse> AcceptSecondChanceOfferAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, AcceptSecondChanceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Người về nhì từ chối lời mời mua lại (API 08: POST /api/v1/auctions/{id}/second-chance/decline).</summary>
    Task<SecondChanceDeclineResponse> DeclineSecondChanceOfferAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, DeclineWinnerOfferRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lấy các lời mời của chính bidder (mới nhất trước, có phân trang, lọc theo trạng thái lời mời).</summary>
    Task<PagedData<WinnerOfferDto>> ListMyWinnerOffersAsync(Guid bidderId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Dành cho Commerce: lấy thông tin chốt kết quả của một phiên (sản phẩm, SKU, seller, người thắng, giá chốt, hạn thanh toán) để tạo order.</summary>
    Task<AuctionSettlementDto> GetSettlementAsync(Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Dành cho Commerce: lấy danh sách các phiên đã chốt người thắng (cũ nhất trước, có phân trang), lọc theo trạng thái settlement và thời điểm chốt.</summary>
    Task<PagedData<AuctionSettlementDto>> ListSettlementsAsync(int page = 1, int pageSize = 20, string? status = null, DateTimeOffset? determinedFrom = null, CancellationToken cancellationToken = default);

    /// <summary>Seller tạo lại phiên mới từ một phiên Failed của mình.</summary>
    Task<AuctionRecreatedResponse> RecreateAsync(ActorContext actor, Guid auctionId, RecreateAuctionRequest request, CancellationToken cancellationToken = default);
}