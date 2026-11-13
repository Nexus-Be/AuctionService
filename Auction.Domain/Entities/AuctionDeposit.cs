using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

/// <summary>
/// Cọc của một bidder trong một phiên. Mỗi cặp (AuctionId, BidderId) chỉ có một bản ghi; thử lại sau khi thất bại
/// hoặc hết hạn dùng lại bản ghi này và tăng AttemptCount. Tiền do Payment Gateway giữ (hold), không vào ví hệ thống.
/// </summary>
public class AuctionDeposit : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required Guid BidderId { get; set; }

    /// <summary>Số cọc, cố định theo PolicyVersion tại thời điểm tạo. Đổi policy sau đó không ảnh hưởng cọc đã tạo.</summary>
    public required decimal Amount { get; set; }
    public required decimal RatePercent { get; set; }
    public required int PolicyVersion { get; set; }
    public required DepositStatus Status { get; set; } = DepositStatus.Pending;
    public int AttemptCount { get; set; } = 1;

    /// <summary>Mã hold do Wallet/Payment trả về. Callback chỉ được áp dụng khi holdId khớp giá trị này.</summary>
    public string? GatewayReference { get; set; }
    public string? GatewayMode { get; set; }
    public string? PaymentRedirectUrl { get; set; }
    public DateTimeOffset? PaymentExpiresAt { get; set; }

    public DateTimeOffset? AuthorizedAt { get; set; }

    /// <summary>Thời điểm hold hết hiệu lực theo cổng. Null nghĩa là cổng không báo hạn.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? FailureCode { get; set; }

    /// <summary>Đang cần giải phóng hold ở Wallet nhưng chưa thành công (lỗi mạng, Wallet bảo trì). Cần job thử lại.</summary>
    public bool ReleasePending { get; set; }
    public string? ReleaseReason { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Tăng mỗi lần cập nhật để hai callback song song không ghi đè nhau.</summary>
    public long RowVersion { get; set; }

    public Auction? Auction { get; set; }
}
