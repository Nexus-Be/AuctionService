---
name: service-api-doc
description: Pre-code API documentation standard for AuctionService (port 8005). Mandates drafting a service-level technical API doc before coding, referencing existing domain entities, enums, DTOs, and error patterns for system-wide consistency. Governs pure DTO contracts, validation attributes, and RFC 9110 error matrices inside AuctionService.
---

# AuctionService API Pre-Code Documentation Skill

> **Scope:** AuctionService repository only (`src/Services/Auction/`). 100% Inward-Only. Never reference the root workspace or sibling services.

This skill governs the **mandatory pre-code documentation lifecycle** within AuctionService. Before any C# code is written or modified for a REST endpoint, the developer or AI agent must create a technical API specification inside `docs/api/`.

---

## 1. Ranh giới Tài liệu AuctionService

* **Vị trí lưu trữ:** `docs/api/{Feature}/{HTTP_METHOD}_{ENDPOINT_SLUG}.md` (ví dụ `docs/api/Auctions/POST_AUCTIONS_BIDS.md`).
* **Thời điểm lập:** **BẮT BUỘC TRƯỚC KHI VIẾT CODE (Pre-Code)**.
* **Định dạng dữ liệu trả về:** **Pure DTO** hoặc `PagedData<T>` (tuyệt đối không bọc qua `ApiResponse<T>`).
* **Rào chắn Inward-Only:** Chỉ trỏ tới tài nguyên nội bộ của AuctionService (`Auction.Domain`, `Auction.Application`, `Auction.Infrastructure`, `Auction.Api`). Không tham chiếu tới `docs/api/` ở Root.

---

## 2. Quy trình 3 Bước Tra cứu Source of Truth Trước Khi Lập Doc

### Bước 1: Tra cứu Thực thể Domain (`Auction.Domain/Entities/`)
- Mở `Auction.Domain/Entities/` (ví dụ `Auction.cs`, `Bid.cs`, `AuctionParticipant.cs`).
- Ràng buộc: Giá đặt `HasPrecision(18, 2)`, bước giá `StepPrice`, thời gian bắt đầu và kết thúc (`DateTimeOffset`).

### Bước 2: Tra cứu Bảng mã Enum (`Auction.Domain/Enums/`)
- Mở `Auction.Domain/Enums/` (ví dụ `AuctionStatus.cs`, `BidStatus.cs`).
- Ghi nhận số nguyên DB (`0`, `1`, `2`...) và chuỗi PascalCase API (`Scheduled`, `Running`, `Ended`, `Cancelled`).
- Điền bảng ánh xạ vào Mục 4.1 của doc.

### Bước 3: Tra cứu Mẫu DTO & Xử lý Lỗi Có Sẵn (`Auction.Application/`)
- Rà soát các tính năng có sẵn tại `Auction.Application/Services/` (ví dụ `Auctions/`, `Bids/`).
- Kế thừa quy ước đặt tên DTO: `{Action}Request`, `{Action}Response`.
- Kế thừa mã lỗi đã có (`AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `VAL_INVALID_INPUT`).

---

## 3. Các Rào Chắn Bất Biến tại AuctionService

1. **Pure DTO:** Controller tại AuctionService trả về DTO thuần, cấm thêm `ResultFilter` hay bọc `ApiResponse<T>`.
2. **Plural Route:** `/api/v1/auctions`, `/api/v1/bids`.
3. **Mã lỗi & Message:** 100% tiếng Anh, Sentence case cho `message`, SCREAMING_SNAKE_CASE cho `errorCode` (cấm tiền tố `ERR_`).
4. **Quyền sở hữu & Bidder Identity:** Thao tác đặt giá (`Bid`) bắt buộc lấy `bidderId` từ `HttpContext.GetUserId()`. Tuyệt đối cấm đặt giá thay người khác.
5. **Phân trang tại DB:** Toàn bộ danh sách phân trang tính toán tại DB qua `Skip`/`Take` và `CountAsync`, trả về `PagedData<T>`.

---

## 4. Quy trình Thực hiện (Workflow)

1. Sao chép `docs/api/SERVICE_API_DOC_TEMPLATE.md` vào `docs/api/{Feature}/{HTTP_METHOD}_{ENDPOINT_SLUG}.md`.
2. Điền đầy đủ 5 phần của template dựa trên kết quả tra cứu Source of Truth.
3. Hoàn thiện mục 5.1 (Pre-Code Plan) mô tả các bước xử lý kỹ thuật và lock concurrency nếu cần.
4. Triển khai code C# tuân thủ 100% hợp đồng đã đặc tả trong doc.
5. Chạy `dotnet test Auction.sln -c Release` (hoặc build `Auction.sln`) đạt 100% pass, tích checklist và chuyển trạng thái doc sang `✅ VERIFIED`.
