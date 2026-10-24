# 03 — SỰ KIỆN BẤT ĐỒNG BỘ QUA RABBITMQ (AUCTION SERVICE EVENT MESSAGING)
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có — **0 publisher / 0 consumer / 0 event class**. Hạ tầng MassTransit **8.3.0** + RabbitMQ + EF Outbox **đã đăng ký** (`Auction.Infrastructure/DependencyInjection.cs:23-48`) nhưng `ConfigureEndpoints` không có consumer nào ⇒ bus rỗng. Migration đã tạo 3 bảng outbox (`InboxState`, `OutboxState`, `OutboxMessage`).
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
AuctionService **được đặc tả** phát hành sự kiện thời gian thực khi có người đặt giá mới hoặc khi phiên đấu giá kết thúc. ⚠️ Trong code hiện tại **không có sự kiện nào** — grep `Event` trong toàn bộ `*.cs` của repo = 0 kết quả; không có file nào trong `Auction.Domain/Events/` (thư mục không tồn tại).
---
## 1. SỰ KIỆN PHÁT HÀNH (EVENTS PUBLISHED)
| Tên Sự Kiện | Thời Điểm Kích Hoạt | Payload Dữ Liệu | Services Quan Tâm | Trạng thái | Bằng chứng |
|:---|:---|:---|:---|:---:|:---|
| `BidPlacedEvent` | Có người đặt giá hợp lệ mới thành công | `AuctionId`, `BidId`, `UserId`, `Amount`, `PlacedAt` | `NotificationService` (báo cho người giữ giá trước bị vượt qua / outbid), `LogService`. | ❌ CHƯA CÓ | không có class event, 0 `IPublishEndpoint.Publish`, 0 use case `PlaceBid` |
| `AuctionEndedEvent` | Phiên đấu giá kết thúc thời gian chạy | `AuctionId`, `WinnerUserId`, `WinningAmount`, `EndedAt` | `CommerceService` (tự động khởi tạo đơn thanh toán cho người thắng), `NotificationService` (báo tin vui thắng cuộc). | ❌ CHƯA CÓ | không có class event, 0 publisher, 0 background job đóng phiên |
| `AuctionCancelledEvent` | Seller hủy phiên đấu giá khi chưa có ai bid | `AuctionId`, `SellerId`, `Reason`, `CancelledAt` | `NotificationService`, `LogService`. | ❌ CHƯA CÓ | không có class event, 0 publisher, 0 use case hủy phiên |
> ⚠️ **Tên sự kiện không thống nhất giữa các tài liệu**: `AGENTS.md` (mục Nguyên tắc 3) ghi `AuctionCreatedEvent`/`BidPlacedEvent`/`AuctionWonEvent`; bản ghi hợp đồng dùng chung ở workspace root ghi `IAuctionEndedEvent`. Code thì **không có cả ba cách đặt tên trên**. Trước khi implement phải chốt một bộ tên duy nhất rồi mới viết class — đây là lý do kỹ thuật khiến việc "thêm event" hiện chưa thể bắt đầu.
---
## 2. SỰ KIỆN TIÊU THỤ (EVENTS CONSUMED)
| Consumer (đặc tả) | Trạng thái | Bằng chứng |
|:---|:---:|:---|
| Không có consumer nào được đặc tả cho AuctionService | ❌ CHƯA CÓ (và không cần) | grep `IConsumer` = 0 kết quả |
---
## 3. HẠ TẦNG MESSAGING ĐÃ DỰNG (nhưng rỗng)
| Thành phần | Trạng thái | Bằng chứng |
|---|---|---|
| `AddMassTransit` + RabbitMQ transport | ✅ CÓ | `Auction.Infrastructure/DependencyInjection.cs:23,33-47` |
| Kebab-case endpoint formatter | ✅ CÓ | `DependencyInjection.cs:25` |
| EF Core Transactional Outbox (Postgres + bus outbox) | ✅ CÓ | `DependencyInjection.cs:27-31` |
| Entity outbox trong model | ✅ CÓ (3 bảng) | `Auction.Infrastructure/Persistence/AppDbContext.cs:35-37` (`AddInboxStateEntity`, `AddOutboxMessageEntity`, `AddOutboxStateEntity`) |
| Consumer đăng ký (`AddConsumer`) | ❌ CHƯA CÓ | `ConfigureEndpoints(context)` không có consumer ⇒ 0 endpoint RabbitMQ được tạo |
| Publisher (`IPublishEndpoint`, `ISendEndpointProvider`) | ❌ CHƯA CÓ | grep = 0 kết quả |
| Cấu hình `RabbitMq:Host/Port/User/Password` | ✅ CÓ | `Auction.Api/appsettings.json:12-17`, đọc ở `DependencyInjection.cs:35-38` (fallback `localhost:5672`, `guest/guest`) |
> Hệ quả cần biết: vì outbox được bật nhưng **không có message nào được publish**, 3 bảng `InboxState`/`OutboxState`/`OutboxMessage` trong migration hiện luôn rỗng và không có tác dụng. Chúng cũng **không** có trong `dbml` của repo — xem `specs/04` mục 3.
---
## 4. SAGA / CHOREOGRAPHY
| Hạng mục | Trạng thái | Bằng chứng |
|---|---|---|
| State machine saga trong code | ❌ CHƯA CÓ | 0 `MassTransitStateMachine`, 0 `SagaStateMachine` |
| Bảng `SagaStates` | ❌ KHÔNG có trong code | bảng này chỉ tồn tại trong `dbml/service/auction-db.dbml:299` ở workspace root; migration của repo không tạo nó |