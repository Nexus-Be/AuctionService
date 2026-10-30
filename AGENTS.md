<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **AuctionService** (445 symbols, 565 relationships, 4 execution flows).

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only` from the project root.

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`.
- **MUST analyze graph changes before committing.** Use `detect_changes({scope: "all"})` or `node .gitnexus/run.cjs detect-changes --scope all --repo .`.
- **MUST warn on HIGH/CRITICAL `risk` pre-edit.**
- **MUST treat `risk: UNKNOWN` as unresolved, not low.**
- **NEVER** edit before impact analysis · **NEVER** rename with find-and-replace · **NEVER** commit before detect_changes.

<!-- gitnexus:end -->

---

# QUY CHUẨN VẬN HÀNH DÀNH CHO AI CODING AGENTS (AUCTION SERVICE)

> **Trạng thái triển khai (2026-10-30):** ✅ ĐÃ CHUẨN HÓA — 100% RESTful API (`AuctionsController`, `BidsController`), Feature Co-location, cấm gRPC, bãi bỏ hoàn toàn RabbitMQ, MassTransit và Outbox tables.
> **Nguồn sự thật:** code (`Auction.sln`, `Auction.Api/Controllers/AuctionsController.cs`, `Auction.Api/Controllers/BidsController.cs`).
> **Cập nhật lần cuối:** 2026-10-30.

---

## 🛑 NGUYÊN TẮC 1: BẮT BUỘC LẬP KẾ HOẠCH TRƯỚC KHI HÀNH ĐỘNG (PLAN-FIRST RULE)

Mọi AI Agent khi nhận nhiệm vụ kỹ thuật tại AuctionService **BẮT BUỘC PHẢI LẬP VÀ TRÌNH BÀY PLAN** trước khi thực hiện bất kỳ hành động sửa đổi mã nguồn, thêm/xóa file, refactor, tạo migration database hay thay đổi cấu hình nào.

Một Plan chuẩn bắt buộc phải có:
1. **Mục tiêu (Objective)**: Tóm tắt chính xác điều cần làm.
2. **Context & Files cần đọc**: Liệt kê các file nội bộ của AuctionService theo ma trận đọc file tại Nguyên tắc 3.
3. **Phân tích rủi ro & Phạm vi ảnh hưởng (Impact Analysis)**: Dùng GitNexus `impact`.
4. **Checklist các bước thực hiện (Step-by-Step Implementation)**.
5. **Kế hoạch kiểm chứng (Verification)**: `dotnet build Auction.sln -c Release`.

---

## 🧭 NGUYÊN TẮC 2: QUY TẮC PHẠM VI NỘI BỘ (INWARD-ONLY / SELF-CONTAINED)

AuctionService là một Git Repository độc lập:
* **TUYỆT ĐỐI CHỈ ĐƯỢC trỏ tới các tài nguyên nội bộ của AuctionService**.
* **TUYỆT ĐỐI KHÔNG ĐƯỢC trỏ ngược ra ngoài Root** (`../../../docs/...`) hay sang các Service khác (`../../Catalog/...`).
* Toàn bộ tài liệu, hướng dẫn và cấu hình trong repo này phải 100% tự cung tự cấp (Self-contained).

---

## 📚 NGUYÊN TẮC 3: DANH MỤC FILE NỘI BỘ BẮT BUỘC PHẢI ĐỌC TẠI AUCTION SERVICE

Khi bắt đầu làm việc tại AuctionService, AI Agent **BẮT BUỘC PHẢI ĐỌC** các file nội bộ sau theo thứ tự:
1. `specs/00_AGENT_MISSION_GUARDRAILS.md`: Bounded context phiên đấu giá, đặt giá, chốt phiên; port `8005`; DB `5437` (`auction_db`).
2. `specs/01_CLEAN_ARCHITECTURE_LAYERS.md`: Cấu trúc 4 tầng Clean Architecture, No-Repository pattern over EF Core.
3. `specs/02_API_REST_CONTRACTS.md`: Hợp đồng RESTful API: `AuctionsController`, `BidsController`.
4. `specs/03_EVENT_MESSAGING.md`: Trạng thái bãi bỏ RabbitMQ / MassTransit; giao tiếp REST trực tiếp.
5. `specs/04_DATA_PERSISTENCE.md`: Schema PostgreSQL thật (`Auctions`, `Bids`, `AuctionStates`...).
6. `specs/05_DEV_VERIFICATION_CHECKLIST.md`: Lệnh build kiểm chứng service.

---

## 🔒 NGUYÊN TẮC 4: RÀO CHẮN BẤT BIẾN TẠI AUCTION SERVICE

1. **TUYỆT ĐỐI KHÔNG DÙNG REPOSITORY PATTERN BỌC NGOÀI EF CORE**: Sử dụng trực tiếp `AppDbContext` (hiện thực `IAuctionDbContext`) và `DbSet<T>` trong tầng Application. Cấm tạo `IAuctionRepository`, `IBidRepository`, `UnitOfWork`.
2. **CẤU HÌNH PORT 8005 VÀ DATABASE 5437 LÀ BẤT BIẾN**.
3. **CẤM SỬ DỤNG gRPC VÀ BÃI BỎ MESSAGE BROKER — BẮT BUỘC DÙNG 100% RESTFUL API**:
   - Toàn bộ giao tiếp đồng bộ sử dụng chuẩn **RESTful Web API** (JSON qua HTTP/1.1 hoặc HTTP/2).
   - Tuyệt đối cấm gRPC, Protobuf, RabbitMQ và MassTransit.
4. **FEATURE CO-LOCATION & 1-TO-1 CONTROLLER PAIRING**:
   - Tầng Application: Toàn bộ use cases đặt tại `Auction.Application/Services/{Feature}/` gồm `I{Feature}Service.cs`, `{Feature}Service.cs`, `{Feature}Requests.cs`, `{Feature}Responses.cs`, `{Feature}Dtos.cs`.
   - Tầng Api: Ghép cặp 1-to-1 với `AuctionsController.cs` và `BidsController.cs`, chỉ inject duy nhất feature service tương ứng.

---

## 💬 NGUYÊN TẮC 5: QUY CHUẨN THÔNG BÁO LỖI TOÀN HỆ THỐNG

1. **Ngôn ngữ (100% English)**: Toàn bộ thông báo lỗi viết bằng tiếng Anh. Tuyệt đối không dùng tiếng Việt.
2. **Quy cách viết hoa (Sentence case)**: Viết hoa duy nhất chữ cái đầu câu: `"Auction has already ended or is not active."`
3. **Mã lỗi (SCREAMING_SNAKE_CASE)**: `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `VAL_INVALID_INPUT`, `UNAUTHORIZED`, `FORBIDDEN`.

---

## 🎯 NGUYÊN TẮC 6: ĐIỀU KIỆN HOÀN THÀNH NHIỆM VỤ (DEFINITION OF DONE)

Một task tại AuctionService chỉ hoàn tất khi:
* Đã lập Plan chi tiết trước khi thực hiện.
* Không chứa bất kỳ liên kết trỏ ngược ra ngoài AuctionService.
* `dotnet build Auction.sln -c Release` thành công 100% (0 error).
* Trạng thái Git sạch, không commit file rác (`bin/`, `obj/`, `.vs/`, `.idea/`).
