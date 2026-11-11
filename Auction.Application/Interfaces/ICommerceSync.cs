namespace Auction.Application.Interfaces;

using System;

/// <summary>
/// Đẩy settlement của phiên sang Commerce để tạo đơn cho người thắng. Hàm chỉ đưa việc vào hàng đợi rồi trả về ngay, không chờ và không ném lỗi;
/// việc gọi Commerce chạy nền và thử lại khi Commerce lỗi tạm thời. Gọi sau khi settlement đã được lưu.
/// Hàng đợi nằm trong bộ nhớ nên việc còn lại sẽ mất nếu service khởi động lại; khi đó Commerce vẫn lấy được settlement qua GET /api/v1/auction-settlements.
/// </summary>
public interface ICommerceSync
{
    /// <summary>Đẩy settlement đang chờ thanh toán của phiên sang Commerce (chốt người thắng hoặc bidder kế tiếp nhận lời mời).</summary>
    void PushSettlement(Guid auctionId);
}
