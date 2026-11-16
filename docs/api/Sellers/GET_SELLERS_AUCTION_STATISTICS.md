# AUCTION SERVICE - GET /api/v1/sellers/{sellerId}/auction-statistics

> **Feature / Use Case:** Seller auction statistics for profile display
> **Trạng thái:** ⏳ DRAFT (Pre-Code Plan)
> **Ngày tạo:** 2026-11-06
> **Tác giả:** AI Agent

---

## 1. Thông tin Định tuyến & RBAC (Routing & Authorization)
- **HTTP Method:** `GET`
- **Route Endpoint:** `/api/v1/sellers/{sellerId}/auction-statistics`
- **Quyền hạn (RBAC Policy):** `AllowAnonymous`
- **Quy tắc Sở hữu (Resource Ownership):** Không áp dụng. Đây là số liệu tổng hợp phục vụ seller profile.
- **Controller:** `Auction.Api/Controllers/Public/AuctionsController.cs`
- **Application Contract:** `Auction.Application/Services/Auction/IAuctionService.cs`

---

## 2. Đầu vào & Quy chuẩn Validation (Input & Validation Schema)
### 2.1 Route Parameters
| Tham số | Vị trí | Kiểu C# | Bắt buộc | Ràng buộc / Giới hạn | Ghi chú |
| :--- | :--- | :--- | :---: | :--- | :--- |
| `sellerId` | Route | `Guid` | Có | Không được là `Guid.Empty` | ID của seller cần hiển thị thống kê |

Không có request body hoặc query parameter.

### 2.2 Validation
- `sellerId` được ràng buộc bởi route constraint `guid`.
- `Guid.Empty` trả về `400 Bad Request` với `VAL_INVALID_INPUT`.
- Seller không có auction vẫn trả về `200 OK` với các count bằng `0` và `successRate = null`.

---

## 3. Đầu ra & Hợp đồng DTO thuần (Response Contract - Pure DTO)
- **Vị trí file:** `Auction.Application/Services/Auction/AuctionDtos.cs`
- **Tên C# Record:** `SellerAuctionStatisticsDto`
- **Mã HTTP thành công:** `200 OK`

### Quy tắc nghiệp vụ
- `successfulAuctions` là số auction có `AuctionStatus.Settled`.
- `failedAuctions` là số auction có `AuctionStatus.Failed`.
- `completedAuctions = successfulAuctions + failedAuctions`.
- `successRate = successfulAuctions / completedAuctions * 100`, làm tròn 2 chữ số thập phân.
- Khi `completedAuctions == 0`, `successRate` là `null`.
- `Draft`, `Scheduled`, `Running`, `Ended` và `Cancelled` không được tính vào `completedAuctions`.
- Auction bị soft-delete không được tính.

### JSON Response Body mẫu
```json
{
  "sellerId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "successfulAuctions": 28,
  "failedAuctions": 9,
  "completedAuctions": 37,
  "successRate": 75.68
}
```

---

## 4. Ma trận Mã lỗi RFC 9110 (Error Matrix)
| HTTP Status | Error Code | Message (Sentence case) | Điều kiện kích hoạt |
| :---: | :--- | :--- | :--- |
| `400 Bad Request` | `VAL_INVALID_INPUT` | `Seller ID is required.` | `sellerId` là `Guid.Empty` |
| `500 Internal Error` | `INTERNAL_SERVER_ERROR` | `An unexpected error occurred while processing the request.` | Lỗi ngoài dự kiến |

---

## 5. Kế hoạch Thực thi & Checklist Kiểm chứng (Pre-Code Plan & Checklist)
### 5.1 Các bước thực thi nghiệp vụ
1. Validate `sellerId` tại controller boundary.
2. Truy vấn `Auctions` bằng `AsNoTracking()`, lọc `SellerId` và `IsDeleted`.
3. Group/count theo `AuctionState.Status` trong SQL, chỉ lấy `Settled` và `Failed`.
4. Tính `completedAuctions` và `successRate` trong Application service.
5. Trả về `SellerAuctionStatisticsDto` thuần.

### 5.2 Persistence
- Không thêm entity hoặc field mới.
- Không thêm index hoặc migration; chức năng chỉ đọc dữ liệu hiện có.

### 5.3 Checklist hoàn thành
- [ ] **Pre-Code:** Đã đối soát entity, enum và service hiện có.
- [x] **Pre-Code:** Tài liệu API nội bộ đã được viết tại `docs/api/Sellers/GET_SELLERS_AUCTION_STATISTICS.md`.
- [ ] **Application:** Interface, service và response DTO đã hoàn thiện.
- [ ] **Api:** Thin controller đã ghép cặp và trả DTO thuần.
- [ ] **Persistence:** Index đã được đánh giá; migration chỉ tạo nếu cần.
- [ ] **Tests:** Repository hiện không có test project.
- [ ] **Verify:** `dotnet build Auction.sln -c Release` đạt 0 lỗi.
