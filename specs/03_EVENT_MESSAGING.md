# 03 — QUY CHUẨN GIAO TIẾP VÀ BÃI BỎ MESSAGE BROKER (AUCTION SERVICE)

> **Trạng thái triển khai (2026-10-30):** 🛑 ĐÃ BÃI BỎ HOÀN TOÀN RABBITMQ & MASSTRANSIT — Toàn bộ cơ chế Consumer, Publisher, Outbox tables đã được loại bỏ khỏi AuctionService.
> **Nguồn sự thật:** code (`Auction.Api/Program.cs`).
> **Cập nhật lần cuối:** 2026-10-30.

---

## 1. NGUYÊN TẮC BÃI BỎ MESSAGE BROKER

1. **Gỡ bỏ hoàn toàn MassTransit**:
   - Toàn bộ package `MassTransit` và `MassTransit.EntityFrameworkCore` đã được loại bỏ.
   - Không khai báo bất kỳ event consumer hoặc publisher nào.

2. **Giao tiếp liên dịch vụ qua REST API**:
   - Các thông báo biến động đấu giá hoặc thông báo người thắng cuộc được thực hiện qua các cuộc gọi REST API đồng bộ có xử lý ngoại lệ an toàn.
