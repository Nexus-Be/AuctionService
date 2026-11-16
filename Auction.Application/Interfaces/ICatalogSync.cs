namespace Auction.Application.Interfaces;

using System;

/// <summary>
/// Đồng bộ trạng thái sản phẩm sang Catalog sau khi vòng đời phiên thay đổi. Các hàm chỉ đưa việc vào hàng đợi rồi trả về ngay, không chờ
/// và không ném lỗi; việc gọi Catalog chạy nền và thử lại khi Catalog lỗi tạm thời. Gọi sau khi thay đổi của phiên đã được lưu.
/// Hàng đợi nằm trong bộ nhớ nên việc còn lại sẽ mất nếu service khởi động lại.
/// </summary>
public interface ICatalogSync
{
    /// <summary>Mở khoá sản phẩm (Catalog: InAuction về Active) khi phiên bị huỷ hoặc thất bại, để seller tạo lại phiên hoặc quản lý sản phẩm.</summary>
    void UnlockProduct(Guid productId, Guid auctionId);

    /// <summary>Chốt bán sản phẩm (Catalog: Auctioned, vĩnh viễn) khi Seller đã chấp nhận đơn (callback SellerAccepted của Commerce).</summary>
    void MarkProductAuctioned(Guid productId, Guid auctionId);

    /// <summary>Hoàn trả sản phẩm từ trạng thái Auctioned về Active khi đơn hàng bị huỷ hoặc từ chối sau khi đã lỡ chốt bán.</summary>
    void RevertProductAuctioned(Guid productId, Guid auctionId);
}
