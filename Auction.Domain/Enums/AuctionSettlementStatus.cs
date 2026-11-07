namespace Auction.Domain.Enums;

/// <summary>Trạng thái xử lý thanh toán của settlement.</summary>
public enum AuctionSettlementStatus
{
    /// <summary>Đang chờ người thắng thanh toán.</summary>
    PendingPayment = 0,
    /// <summary>Đã thanh toán thành công.</summary>
    Paid = 1,
    /// <summary>Thanh toán thất bại.</summary>
    Failed = 2,
    /// <summary>Thanh toán đã hết hạn.</summary>
    Expired = 3,
    /// <summary>Settlement đã bị huỷ.</summary>
    Cancelled = 4
}
