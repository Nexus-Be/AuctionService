# AUCTION SERVICE SPEC KIT — CHỈ DẪN KỸ THUẬT CHO AI AGENT & DEVELOPER

Tài liệu này là **Bộ Chỉ Dẫn & Đặc Tả Kỹ Thuật (Spec Kit)** cho repository **AuctionService** thuộc hệ sinh thái Microservices **Nexus**. Giúp lập trình viên và các AI Coding Agent (Cursor, Claude, Copilot, DeepSeek...) nắm bắt bối cảnh nghiệp vụ phòng đấu giá trực tuyến, lượt đặt giá (bids), quy tắc bước giá và **loại bỏ hoàn toàn nguy cơ ảo giác (zero hallucination)**.

---

## 1. MỤC LỤC BỘ ĐẶC TẢ AUCTION SERVICE (SPECS/)

| File Đặc Tả | Trọng Tâm Nội Dung |
|:---|:---|
| [00_AGENT_MISSION_GUARDRAILS.md](00_AGENT_MISSION_GUARDRAILS.md) | Bounded Context, Port `8005`, DB `auction_db` (`5436`), các điều cấm kỵ cốt lõi. |
| [01_CLEAN_ARCHITECTURE_LAYERS.md](01_CLEAN_ARCHITECTURE_LAYERS.md) | Cấu trúc 4 tầng Clean Architecture (`.Api`, `.Application`, `.Domain`, `.Infrastructure`), luồng phụ thuộc. |
| [02_API_GRPC_CONTRACTS.md](02_API_GRPC_CONTRACTS.md) | Hợp đồng gRPC `AuctionApi` trong `shop.proto`, tạo phiên đấu giá, đặt giá bid, danh sách bids. |
| [03_EVENT_MESSAGING.md](03_EVENT_MESSAGING.md) | Sự kiện bất đồng bộ RabbitMQ + MassTransit 8.4.1 (`BidPlacedEvent`, `AuctionEndedEvent`). |
| [04_DATA_PERSISTENCE.md](04_DATA_PERSISTENCE.md) | Mô hình thực thể `Auction`, `Bid`, DbContext EF Core 8 (No Repository Pattern). |
| [05_DEV_VERIFICATION_CHECKLIST.md](05_DEV_VERIFICATION_CHECKLIST.md) | Lệnh biên dịch, kiểm thử tự động, checklist kiểm tra trước khi commit. |

---

## 2. THÔNG SỐ VẬN HÀNH CHÍNH

- **Ngôn ngữ & Runtime**: .NET 8 (C# 12).
- **Cổng gRPC Server**: Port `8005` (Không có cổng HTTP REST riêng, mọi REST call đều qua ApiGateway Port `8000`).
- **Cơ Sở Dữ Liệu**: PostgreSQL (Port `5436`, Database: `auction_db`).
- **Message Broker**: RabbitMQ (Port `5672`, Virtual Host: `/`).
- **Quy Tắc Đặt Giá (Bidding Rules)**: Giá bid sau bắt buộc $\ge$ `CurrentPrice + StepPrice`.
