# Admin Testing Time & Deadline APIs — Tổng hợp thay đổi & Phân tích 5W1H

> **Service:** AuctionService (Port `8005`)  
> **Ngày cập nhật:** 2026-11-20  
> **Mục đích tổng quát:** Cung cấp công cụ trên giao diện Admin để điều chỉnh nhanh thời gian phiên đấu giá và các mốc hạn chót sau phiên (duyệt giá sàn 12h, thanh toán 24h, lời mời second-chance), phục vụ kiểm thử E2E/Staging mà không cần đợi thời gian thực hoặc sửa DB thủ công.

---

## 1. Tổng hợp các thay đổi trong mã nguồn

| Thành phần | File | Thay đổi chi tiết |
| :--- | :--- | :--- |
| **Requests** | `Auction.Application/Services/Auction/AuctionRequests.cs` | Thêm `AdminUpdateAuctionScheduleRequest` và `AdminUpdateSettlementDeadlinesRequest`. |
| **Responses** | `Auction.Application/Services/Auction/AuctionResponses.cs` | Thêm `AdminAuctionScheduleUpdatedResponse` và `AdminSettlementDeadlinesUpdatedResponse`. |
| **Service Interface** | `Auction.Application/Services/Auction/IAuctionService.cs` | Khai báo `AdminUpdateScheduleAsync` và `AdminUpdateSettlementDeadlinesAsync`. |
| **Service Logic** | `Auction.Application/Services/Auction/AuctionService.cs` | Hiện thực logic validate trạng thái, tính mốc thời gian mới, tăng `RowVersion` (đối với `AuctionState`), ghi log audit và lưu DB. |
| **Controller** | `Auction.Api/Controllers/Public/AuctionsController.cs` | Thêm 2 endpoint `PATCH`:<br>- `~/api/v1/admin/auctions/{id:guid}/schedule`<br>- `~/api/v1/admin/auctions/{id:guid}/settlement-deadlines` |

---

## 2. Phân tích API 1: Cập nhật lịch / thời gian phiên đấu giá

**Endpoint:** `PATCH /api/v1/admin/auctions/{id}/schedule`

### Phân tích theo 5 tiêu chí (Who, What, When, Where, How)

1. **Who (Ai được phép gọi?)**
   - Cổng môi trường (`RequireAdminTimeControls`): chỉ mở khi `AdminTimeControls:Enabled=true` và host không phải Production; đóng thì **404 `NOT_FOUND`** trước cả bước xác thực.
   - Bắt buộc đăng nhập (`TryGetUserId`) và có role **`admin`** (không còn `staff`) (`HasRole("admin") || HasRole("staff")`).
   - Staff, Buyer, Seller và mọi role khác gọi sẽ bị chặn với HTTP `403 Forbidden` (`ROLE_NOT_ALLOWED`).

2. **What (API này làm gì?)**
   - Cho phép chỉnh sửa `ScheduledStartAt` (khi phiên chưa chạy) và/hoặc `AuctionState.ScheduledEndAt` (khi phiên đang `Draft`, `Scheduled`, hoặc `Running`).
   - Hỗ trợ 2 cách truyền:
     - **Tua nhanh tương đối (`remainingSeconds`):** Ví dụ truyền `300` để phiên còn đúng 5 phút, hoặc `0` để phiên hết giờ ngay lập tức.
     - **Đặt mốc tuyệt đối (`scheduledStartAt`, `scheduledEndAt`):** Truyền timestamp ISO-8601 cụ thể.
   - **Input (`AdminUpdateAuctionScheduleRequest`):**
     ```json
     {
       "remainingSeconds": 300,
       "scheduledStartAt": null,
       "scheduledEndAt": null,
       "reason": "QA test anti-sniping extension"
     }
     ```
   - **Output (`AdminAuctionScheduleUpdatedResponse` — `200 OK`):**
     ```json
     {
       "auctionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
       "status": "Running",
       "scheduledStartAt": "2026-11-20T10:00:00Z",
       "originalEndAt": "2026-11-20T12:00:00Z",
       "scheduledEndAt": "2026-11-20T10:55:00Z",
       "updatedAt": "2026-11-20T10:50:00Z"
     }
     ```

3. **When (Khi nào sử dụng?)**
   - Khi phiên đang ở trạng thái **`Scheduled`** (hoặc `Draft`): muốn tua `scheduledStartAt` về hiện tại (`now`) để background worker mở phiên sang `Running` ngay.
   - Khi phiên đang **`Running`**:
     - Đặt `remainingSeconds: 300` (5 phút) hoặc `180` (3 phút) để test **anti-sniping** (đặt giá ở những phút cuối xem hệ thống có tự gia hạn `ScheduledEndAt` và tăng `ExtensionCount` hay không).
     - Đặt `remainingSeconds: 0` (hoặc `10`) để test **kết thúc phiên** (chốt người thắng, hoặc chuyển sang `AwaitingSellerApproval` nếu dưới giá sàn, hoặc `Ended` không người mua).
   - **Không dùng được** khi phiên đã `Ended`, `Settled`, `Failed`, `Cancelled` (`409 INVALID_AUCTION_STATUS`).

4. **Where (Tác động ở đâu?)**
   - **Controller:** `Auction.Api/Controllers/Public/AuctionsController.cs` (`AdminUpdateSchedule`).
   - **Service:** `AuctionService.AdminUpdateScheduleAsync`.
   - **Database:** Bảng `Auctions` (`ScheduledStartAt`, `OriginalEndAt`, `UpdatedAt`) và bảng `AuctionStates` (`ScheduledEndAt`, `RowVersion`, `UpdatedAt`).
   - **Tác động dây chuyền:** Các background worker của `AuctionService` (mở phiên `Scheduled -> Running`, chốt phiên `Running -> Ended`) và logic anti-sniping trong `BidService` đọc trực tiếp `ScheduledStartAt` / `AuctionState.ScheduledEndAt`, nên luồng tự động sẽ chạy tiếp theo đúng mốc mới.

5. **How (Cơ chế xử lý & Ràng buộc lỗi)**
   - Nếu không truyền trường thời gian nào → `400 VAL_INVALID_INPUT`.
   - Nếu `remainingSeconds < 0` → `400 VAL_INVALID_INPUT`.
   - Nếu phiên đang `Running` mà truyền `scheduledStartAt` → `409 INVALID_AUCTION_STATUS` (không cho sửa giờ bắt đầu của phiên đang chạy).
   - Nếu `newEndAt <= scheduledStartAt` (trừ trường hợp phiên `Running` truyền `remainingSeconds = 0` để kết thúc ngay) → `400 VAL_INVALID_INPUT`.
   - Nếu phiên là `Draft`/`Scheduled`, `OriginalEndAt` được đồng bộ bằng `newEndAt`; nếu phiên là `Running`, chỉ cập nhật `state.ScheduledEndAt` và tăng `state.RowVersion`.

---

## 3. Phân tích API 2: Cập nhật các mốc hạn chót sau phiên (Settlement Deadlines)

**Endpoint:** `PATCH /api/v1/admin/auctions/{id}/settlement-deadlines`

### Phân tích theo 5 tiêu chí (Who, What, When, Where, How)

1. **Who (Ai được phép gọi?)**
   - Cùng cổng môi trường như trên (đóng thì 404). Bắt buộc đăng nhập và có role **`admin`** (`403 ROLE_NOT_ALLOWED` nếu sai role, kể cả staff).

2. **What (API này làm gì?)**
   - Tua nhanh hoặc dời 3 loại hạn chót sau khi phiên đã kết thúc:
     1. **Hạn 12h Seller duyệt bán dưới giá sàn:** `AuctionSettlement.SellerApprovalDeadlineAt` (qua `sellerApprovalRemainingSeconds` hoặc `sellerApprovalDeadlineAt`).
     2. **Hạn 24h Buyer thanh toán đơn thắng / Buy Now:** `AuctionSettlement.PaymentDeadlineAt` (qua `paymentRemainingSeconds` hoặc `paymentDeadlineAt`).
     3. **Hạn phản hồi lời mời Second-Chance:** `AuctionWinnerOffer.ExpiresAt` của lời mời đang `Pending` (qua `winnerOfferRemainingSeconds` hoặc `winnerOfferExpiresAt`).
   - **Input (`AdminUpdateSettlementDeadlinesRequest`):**
     ```json
     {
       "sellerApprovalRemainingSeconds": 10,
       "paymentRemainingSeconds": null,
       "winnerOfferRemainingSeconds": null,
       "sellerApprovalDeadlineAt": null,
       "paymentDeadlineAt": null,
       "winnerOfferExpiresAt": null,
       "reason": "Test 12h reserve approval expiry"
     }
     ```
   - **Output (`AdminSettlementDeadlinesUpdatedResponse` — `200 OK`):**
     ```json
     {
       "auctionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
       "settlementStatus": "AwaitingSellerApproval",
       "sellerApprovalDeadlineAt": "2026-11-20T10:50:10Z",
       "paymentDeadlineAt": null,
       "pendingWinnerOfferId": null,
       "winnerOfferExpiresAt": null,
       "updatedAt": "2026-11-20T10:50:00Z"
     }
     ```

3. **When (Khi nào sử dụng?)**
   - **Test hết hạn 12h duyệt giá sàn:** Khi phiên kết thúc ở trạng thái `AwaitingSellerApproval`, set `sellerApprovalRemainingSeconds: 0` (hoặc `10`) để worker tự động xử lý hết hạn duyệt (hoàn cọc buyer, hủy phiên không đạt giá sàn, kiểm tra streak nếu có).
   - **Test hết hạn 24h thanh toán:** Khi phiên ở trạng thái `PendingPayment` (thắng thường hoặc Buy Now), set `paymentRemainingSeconds: 0` để worker tự tịch thu cọc (và hoàn tiền hàng còn lại nếu Buy Now), ghi nhận vi phạm người mua, và kích hoạt lời mời Second-Chance (nếu đủ điều kiện).
   - **Test hết hạn Second-Chance:** Khi đang có `AuctionWinnerOffer` ở trạng thái `Pending`, set `winnerOfferRemainingSeconds: 0` để worker đánh dấu offer hết hạn.

4. **Where (Tác động ở đâu?)**
   - **Controller:** `Auction.Api/Controllers/Public/AuctionsController.cs` (`AdminUpdateSettlementDeadlines`).
   - **Service:** `AuctionService.AdminUpdateSettlementDeadlinesAsync`.
   - **Database:** Bảng `AuctionSettlements` (`SellerApprovalDeadlineAt`, `PaymentDeadlineAt`, `UpdatedAt`) và bảng `AuctionWinnerOffers` (`ExpiresAt`, `UpdatedAt`).

5. **How (Cơ chế xử lý & Ràng buộc lỗi)**
   - Nếu không truyền field nào → `400 VAL_INVALID_INPUT`.
   - Nếu bất kỳ `*RemainingSeconds < 0` → `400 VAL_INVALID_INPUT`.
   - Nếu chỉnh `sellerApproval*` mà `settlement.Status != AwaitingSellerApproval` → `409 INVALID_SETTLEMENT_STATUS`.
   - Nếu chỉnh `payment*` mà `settlement.Status != PendingPayment` → `409 INVALID_SETTLEMENT_STATUS`.
   - Nếu chỉnh `winnerOffer*` mà không có offer nào đang `Pending` → `409 WINNER_OFFER_NOT_FOUND`.
   - Khi thành công, background worker sẽ tự nhận diện các bản ghi quá hạn ở chu kỳ quét kế tiếp (vài giây) và thực thi toàn bộ luồng nghiệp vụ thật.
