namespace Auction.Application.Services.Auction;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IAuctionService
{
    /// <summary>Lấy danh sách phiên đấu giá có phân trang, tìm kiếm, lọc và sắp xếp.</summary>
    Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, AuctionListFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách phiên của riêng một seller, có phân trang và lọc theo trạng thái.</summary>
    Task<PagedData<AuctionSummaryDto>> ListSellerAuctionsAsync(Guid sellerId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách phiên đấu giá mà caller đã tham gia (gom theo từng phiên).</summary>
    Task<PagedData<MyParticipatedAuctionDto>> ListMyAuctionsAsync(
        Guid userId, int page = 1, int pageSize = 20, string? participationState = null, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Admin/Staff xem danh sách phiên đấu giá toàn sàn với bộ lọc vận hành.</summary>
    Task<PagedData<AdminAuctionSummaryDto>> ListAdminAuctionsAsync(
        int page = 1, int pageSize = 20, string? status = null, Guid? sellerId = null, Guid? productId = null,
        bool? hasAttentionDeposit = null, bool? overdueSettlement = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

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

    /// <summary>Admin/Staff xem danh sách các phiên đang bị kẹt lỗi cọc, đồng bộ hoặc quá hạn settlement cần xử lý.</summary>
    Task<PagedData<AdminAuctionSummaryDto>> ListAttentionRequiredAuctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Admin/Staff kích hoạt chạy lại đồng bộ Catalog / Commerce / User cho một phiên bị kẹt.</summary>
    Task<AdminSyncRetryResponse> RetrySyncAsync(ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Admin/Staff vô hiệu hoá một lượt bid gian lận khi phiên đang chạy và tính lại giá dẫn đầu.</summary>
    Task<InvalidateBidResponse> InvalidateBidAsync(
        ActorContext actor, Guid auctionId, Guid bidId, InvalidateBidRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Buyer yêu thích / theo dõi phiên đấu giá (và bật/tắt thông báo khi có bid mới).</summary>
    Task<WatchAuctionResponse> WatchAuctionAsync(
        ActorContext actor, Guid auctionId, WatchAuctionRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Buyer bỏ yêu thích / ngừng theo dõi phiên đấu giá.</summary>
    Task<WatchAuctionResponse> UnwatchAuctionAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Buyer xem danh sách các phiên đấu giá mình đã yêu thích / theo dõi.</summary>
    Task<PagedData<MyWatchlistItemDto>> ListMyWatchlistAsync(
        Guid userId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Buyer gửi báo cáo vi phạm cho một phiên đấu giá đang mở.</summary>
    Task<CreateAuctionReportResponse> CreateReportAsync(
        ActorContext actor, Guid auctionId, CreateAuctionReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Buyer xem lịch sử các báo cáo mình đã gửi và phản hồi từ Admin.</summary>
    Task<PagedData<MyAuctionReportDto>> ListMyReportsAsync(
        Guid userId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Admin/Staff xem danh sách báo cáo phiên đấu giá trên toàn sàn.</summary>
    Task<PagedData<AdminAuctionReportDto>> ListAdminReportsAsync(
        int page = 1, int pageSize = 20, string? status = null, Guid? auctionId = null, CancellationToken cancellationToken = default);

    /// <summary>Admin/Staff đánh dấu các báo cáo của phiên là không có sai phạm và gửi phản hồi cho người tố cáo.</summary>
    Task<DismissAuctionReportsResponse> DismissReportsAsync(
        ActorContext actor, Guid auctionId, DismissAuctionReportsRequest? request, CancellationToken cancellationToken = default);
}