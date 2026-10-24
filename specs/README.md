# AUCTION SERVICE SPEC KIT — CHỈ DẪN KỸ THUẬT CHO AI AGENT & DEVELOPER
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có bề mặt dịch vụ — **1/5 mục spec đã có hạ tầng nền** (04 dữ liệu ⚠️ một phần, 03 messaging ⚠️ hạ tầng rỗng), **0 rpc gRPC**, **0 publisher/0 consumer**, **0 test**. Bộ spec này là **đặc tả mục tiêu**, phải đọc kèm banner từng file.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
Tài liệu này là **Bộ Chỉ Dẫn & Đặc Tả Kỹ Thuật (Spec Kit)** cho repository **AuctionService** thuộc hệ sinh thái Microservices **Nexus**. Giúp lập trình viên và các AI Coding Agent (Cursor, Claude, Copilot, DeepSeek...) nắm bắt bối cảnh nghiệp vụ phòng đấu giá trực tuyến, lượt đặt giá (bids), quy tắc bước giá và **loại bỏ hoàn toàn nguy cơ ảo giác (zero hallucination)**.
---
## 1. MỤC LỤC BỘ ĐẶC TẢ AUCTION SERVICE (SPECS/)
| File Đặc Tả | Trọng Tâm Nội Dung | Trạng thái thực tế |
|:---|:---|:---:|
| [00_AGENT_MISSION_GUARDRAILS.md](00_AGENT_MISSION_GUARDRAILS.md) | Bounded Context, port `8005` (Docker) / `5230`-`7202` (local), DB `auction_db` (`5437`), các điều cấm kỵ cốt lõi. | ❌ Chưa có bề mặt dịch vụ |
| [01_CLEAN_ARCHITECTURE_LAYERS.md](01_CLEAN_ARCHITECTURE_LAYERS.md) | Cấu trúc 4 tầng Clean Architecture (`.Api`, `.Application`, `.Domain`, `.Infrastructure`), luồng phụ thuộc. | ⚠️ Một phần (0 domain method, 0 use case) |
| [02_API_GRPC_CONTRACTS.md](02_API_GRPC_CONTRACTS.md) | Hợp đồng gRPC `AuctionApi` (5 rpc: `ListAuctions`, `GetAuction`, `CreateAuction`, `PlaceBid`, `ListBids`). | ❌ 0/5 rpc, 0 file `.proto` |
| [03_EVENT_MESSAGING.md](03_EVENT_MESSAGING.md) | Sự kiện bất đồng bộ RabbitMQ + MassTransit **8.3.0** (`BidPlacedEvent`, `AuctionEndedEvent`, `AuctionCancelledEvent`). | ❌ 0 publisher, 0 consumer |
| [04_DATA_PERSISTENCE.md](04_DATA_PERSISTENCE.md) | 13 thực thể + `AuctionState`, DbContext EF Core 8 (No Repository Pattern), 16 bảng trong migration. | ⚠️ Một phần (0 `SaveChangesAsync`) |
| [05_DEV_VERIFICATION_CHECKLIST.md](05_DEV_VERIFICATION_CHECKLIST.md) | Lệnh biên dịch, kiểm thử tự động, checklist kiểm tra trước khi commit. | ❌ Không có `dotnet test` (0 test project) |
---
## 2. THÔNG SỐ VẬN HÀNH CHÍNH
- **Ngôn ngữ & Runtime**: .NET 8 (C# 12) — mọi project dùng `net8.0`.
- **Cổng gRPC Server**: ⚠️ **Đặc tả** là port `8005`; **thực tế** container expose `8005` (`Dockerfile:3-4`, `docker-compose.yml:37`) nhưng **không có gRPC server nào**; chạy local bằng `launchSettings.json` lại là `5230` (http) / `7202` (https). Endpoint duy nhất: `GET /health`.
- **Cơ Sở Dữ Liệu**: PostgreSQL (port `5437`, database `auction_db`) — ✅ có thật, migration đã tạo 16 bảng.
- **Message Broker**: RabbitMQ (port `5672`, virtual host `/`) — ⚠️ đã đăng ký MassTransit 8.3.0 + outbox nhưng **không có consumer/message nào**.
- **Quy Tắc Đặt Giá (Bidding Rules)**: Giá bid sau bắt buộc $\ge$ `CurrentPrice + StepPrice` — ❌ **đặc tả mục tiêu, chưa triển khai**: không có use case `PlaceBid`, không có validation, 0 `throw new` toàn repo.
- **Số liệu code thật (2026-10-24)**: 47 file `.cs` / 1.182 dòng (không tính `Migrations/`), 0 test project.