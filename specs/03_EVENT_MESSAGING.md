# 03 — SỰ KIỆN BẤT ĐỒNG BỘ QUA RABBITMQ (AUCTION SERVICE EVENT MESSAGING)

AuctionService phát hành các sự kiện thời gian thực khi có người đặt giá mới hoặc khi phiên đấu giá kết thúc.

---

## 1. SỰ KIỆN PHÁT HÀNH (EVENTS PUBLISHED)

| Tên Sự Kiện | Thời Điểm Kích Hoạt | Payload Dữ Liệu | Services Quan Tâm |
|:---|:---|:---|:---|
| `BidPlacedEvent` | Có người đặt giá hợp lệ mới thành công | `AuctionId`, `BidId`, `UserId`, `Amount`, `PlacedAt` | `NotificationService` (Báo cho người giữ giá trước bị vượt qua / outbid), `LogService`. |
| `AuctionEndedEvent` | Phiên đấu giá kết thúc thời gian chạy | `AuctionId`, `WinnerUserId`, `WinningAmount`, `EndedAt` | `CommerceService` (Tự động khởi tạo đơn thanh toán cho người thắng), `NotificationService` (Báo tin vui thắng cuộc). |
| `AuctionCancelledEvent`| Seller hủy phiên đấu giá khi chưa có ai bid | `AuctionId`, `SellerId`, `Reason`, `CancelledAt` | `NotificationService`, `LogService`. |
