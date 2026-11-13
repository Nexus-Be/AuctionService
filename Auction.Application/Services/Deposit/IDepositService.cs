namespace Auction.Application.Services.Deposit;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IDepositService
{
    /// <summary>Cho Buyer biết số cọc, tỷ lệ, chính sách tịch thu và cọc hiện có của họ trong phiên. Không có hiệu ứng phụ.</summary>
    Task<DepositRequirementResponse> GetRequirementAsync(ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tạo (hoặc thử lại) cọc bằng cách khóa số dư khả dụng trong ví Nexus đồng bộ (Hướng C).
    /// Cùng Idempotency-Key và cùng nội dung thì trả kết quả lần đầu.
    /// </summary>
    Task<CreateDepositResult> CreateAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, CreateDepositRequest request, CancellationToken cancellationToken = default);

    /// <summary>Nhận callback kết quả Authorization từ Wallet và chuyển trạng thái cọc. Chịu được callback trùng, đảo thứ tự và đến muộn.</summary>
    Task<DepositAuthorizationResultResponse> ApplyAuthorizationResultAsync(
        Guid depositId, DepositAuthorizationResultRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bidder phải có cọc Authorized trong DB của Auction mới được đặt giá.
    /// Ném DEPOSIT_REQUIRED nếu chưa có cọc hoặc cọc không ở trạng thái Authorized.
    /// </summary>
    Task EnsureBidderDepositValidAsync(Guid auctionId, Guid bidderId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Admin hoặc Staff xem đối soát danh sách cọc của một phiên đấu giá.</summary>
    Task<AdminAuctionDepositsResponse> GetAdminAuctionDepositsAsync(
        ActorContext actor, Guid auctionId, string? status, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Buyer xem trạng thái cọc của chính mình trong phiên đấu giá và biết có được đặt giá hay không (API 03).</summary>
    Task<MyDepositResponse> GetMyDepositAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default);

    /// <summary>Giải phóng các cọc đã đánh dấu ReleasePending của một phiên đấu giá (chạy sau khi commit huỷ phiên hoặc kết thúc phiên).</summary>
    Task ReleasePendingDepositsForAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default);
}
