namespace Auction.Domain.Enums;

/// <summary>Trạng thái cọc của một bidder trong một phiên.</summary>
public enum DepositStatus
{
    /// <summary>Đã tạo, đang chờ Buyer thanh toán (Authorization) qua cổng thanh toán.</summary>
    Pending = 0,
    /// <summary>Cổng đã giữ tiền (hold). Bidder được phép đặt giá.</summary>
    Authorized = 1,
    /// <summary>Authorization thất bại.</summary>
    Failed = 2,
    /// <summary>Hold đã hết hạn hoặc bị giải phóng ngoài dự kiến.</summary>
    Expired = 3,
    /// <summary>Hold đã được giải phóng, Buyer không mất cọc.</summary>
    Released = 4,
    /// <summary>Hold đã được thu (Capture).</summary>
    Captured = 5
}
