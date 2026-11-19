# ĐẶC TẢ CHI TIẾT CÁC API CHO BUY NOW & GIÁ SÀN (RESERVE PRICE)

> **Service:** `AuctionService` (port `8005`)  
> **Mục đích tài liệu:** Giải thích rõ lý do ra đời, bài toán nghiệp vụ giải quyết, Input, Output và toàn bộ Error codes của từng API phục vụ tính năng **Mua ngay (Buy Now)** và **Giá sàn (Reserve Price)**.

---

## 1. Cập nhật các API Tạo / Sửa / Tạo lại / Xem phiên đấu giá

### 1.1. Tạo, Sửa & Tạo lại phiên (`POST /api/v1/auctions`, `PUT /api/v1/auctions/{id}`, `POST /api/v1/auctions/{id}/recreate`)
* **Tại sao cập nhật:** Seller cần thiết lập mức giá tối thiểu bí mật (`reservePriceAmount`) và mức giá chốt nhanh (`buyNowPriceAmount`) ngay khi lên lịch hoặc chỉnh sửa phiên (trước khi phiên bắt đầu).
* **Input bổ sung (JSON Body — đều là optional/nullable):**
  * `reservePriceAmount` (`decimal?`): Giá sàn bí mật (chỉ Seller & Admin biết).
  * `buyNowPriceAmount` (`decimal?`): Giá Mua ngay.
* **Output bổ sung:** Trả kèm `reservePriceAmount`, `buyNowPriceAmount` trong `AuctionCreatedResponse`, `AuctionUpdatedResponse`, `AuctionRecreatedResponse`.
* **Error Codes mới:**
  * `400 VAL_INVALID_PRICE`: Giá âm hoặc nhiều hơn 2 chữ số thập phân.
  * `422 INVALID_PRICE_RELATION`: `reservePriceAmount < startPriceAmount`, hoặc `buyNowPriceAmount <= reservePriceAmount` (nếu có giá sàn) / `<= startPriceAmount` (nếu không có giá sàn).
  * `422 RESERVE_PRICE_RESTRICTED`: Seller đang bị tạm khóa quyền đặt Giá sàn 14 ngày do từ chối bán dưới giá sàn 3 lần liên tiếp.

### 1.2. Xem chi tiết & Danh sách phiên (`GET /api/v1/auctions`, `GET /api/v1/auctions/{id}`)
* **Tại sao cập nhật:** Buyer không được biết con số `reservePriceAmount`, nhưng cần biết phiên đã chạm giá sàn chưa và nút Buy Now còn hiệu lực hay không.
* **Output bổ sung (`AuctionSummaryDto` & `AuctionDetailDto`):**
  * `buyNowPriceAmount` (`decimal?`)
  * `hasReservePrice` (`bool` — chỉ trong `AuctionDetailDto`)
  * `isReserveMet` (`bool` — `true` nếu không có giá sàn hoặc `currentPriceAmount >= reservePriceAmount`)
  * `buyNowAvailable` (`bool` — `true` khi phiên `Running` và chưa chạm giá sàn / chưa có bid nào nếu không có giá sàn)
  * *(Lưu ý bảo mật: Tuyệt đối **không** trả `reservePriceAmount` ở 2 API công khai này).*

---

## 2. API Mua ngay (`POST /api/v1/auctions/{id}/buy-now`)

### 2.1. Tại sao sinh ra & Giải quyết việc gì?
- **Lý do sinh ra:** Cho phép người mua chốt sở hữu sản phẩm ngay lập tức ở mức `BuyNowPrice` khi phiên chưa chạm Giá sàn (hoặc chưa có bid nào nếu không đặt Giá sàn), thay vì phải chờ hết giờ đấu giá.
- **Bài toán giải quyết:** Thay cho luồng "giữ chỗ chờ X phút thanh toán" dễ gây xung đột khi có người bid vượt giá Buy Now hoặc bị spam khóa phiên, API này **khóa đủ 100% tiền hàng trong Ví Nexus ngay tại thời điểm bấm** (tách làm 2 hold: `Tiền cọc` + `Tiền hàng còn lại`), giải tỏa ngay cọc của tất cả bidder khác, và đưa đơn sang Commerce để Buyer trả riêng phí vận chuyển trong 24h. Nếu Buyer bùng không trả phí ship, chỉ tịch thu phần Tiền cọc và hoàn trả phần Tiền hàng còn lại.

### 2.2. Input
* **Path:** `id` (`Guid`, mã phiên đấu giá)
* **Headers:**
  * `Idempotency-Key` *(Bắt buộc — chống bấm đúp / gửi lại khi mạng lag)*
  * `If-Match` *(Khuyến nghị — truyền `RowVersion` hiện tại để chặn nếu có người vừa bid chạm giá sàn trước 1 mili-giây)*
* **Auth:** Role `buyer` hoặc `seller` (trừ Seller chủ của chính phiên này).

### 2.3. Output (`200 OK` — `BuyNowResponse`)
```json
{
  "auctionId": "guid",
  "winnerId": "guid",
  "buyNowPriceAmount": 15000000,
  "depositAmountHeld": 1000000,
  "remainingGoodsAmountHeld": 14000000,
  "status": "Ended",
  "endedAt": "2026-11-19T15:30:00Z",
  "paymentDeadlineAt": "2026-11-20T15:30:00Z",
  "rowVersion": 5
}
```

### 2.4. Errors
* `400 VAL_INVALID_IDENTIFIER` / `VALIDATION_ERROR`: Thiếu `Idempotency-Key` hoặc `auctionId` rỗng.
* `403 SELLER_CANNOT_BID_OWN_AUCTION` / `ROLE_NOT_ALLOWED`: Seller tự mua phiên của mình hoặc tài khoản không hợp lệ.
* `404 AUCTION_NOT_FOUND`: Phiên không tồn tại.
* `409 CONCURRENT_UPDATE`: `If-Match` (`RowVersion`) không khớp vì phiên vừa có bid mới hoặc vừa đóng.
* `409 AUCTION_NOT_RUNNING`: Phiên chưa bắt đầu hoặc đã kết thúc.
* `409 BUY_NOW_NOT_CONFIGURED`: Phiên không cài giá Buy Now.
* `409 BUY_NOW_NOT_AVAILABLE`: Nút Buy Now đã đóng vì giá bid đã chạm/vượt Giá sàn (hoặc đã có bid đầu tiên đối với phiên không giá sàn).
* `409 DEPOSIT_RELEASE_PENDING`: Khoản cọc cũ của user đang chờ giải tỏa.
* `422 INSUFFICIENT_AVAILABLE_BALANCE` / `WALLET_FROZEN` / `USER_RESTRICTED`: Số dư khả dụng trong Ví không đủ cho phần còn lại, ví bị khóa, hoặc user bị ban/hạn chế quyền đấu giá.
* `422 IDEMPOTENCY_KEY_REUSED`: Dùng lại `Idempotency-Key` cho một request khác.
* `503 WALLET_UNAVAILABLE`: Không kết nối được WalletService.

---

## 3. API Seller Đồng ý bán dưới giá sàn (`POST /api/v1/auctions/{id}/reserve-approval/accept`)

### 3.1. Tại sao sinh ra & Giải quyết việc gì?
- **Lý do sinh ra:** Khi phiên hết giờ (`00:00`) mà giá cao nhất chưa đạt `ReservePrice`, phiên không hủy vội mà chuyển `AuctionSettlement.Status` sang `AwaitingSellerApproval` trong **12 giờ** để Seller tự quyết định có chấp nhận mức giá thị trường đó hay không.
- **Bài toán giải quyết:** Tăng tỷ lệ chốt đơn thực tế cho Seller và sàn mà không cần dùng tài khoản ảo hay hủy sớm trước giờ. Khi Seller bấm đồng ý, hệ thống mở đơn hàng cho Hạng 1 thanh toán trong 24h và **reset chuỗi từ chối (`ConsecutiveDeclineCount`) của Seller về `0`**.

### 3.2. Input
* **Path:** `id` (`Guid`, mã phiên đấu giá)
* **Auth:** Role `seller` (bắt buộc là chủ sở hữu phiên).

### 3.3. Output (`200 OK` — `ReserveApprovalResponse`)
```json
{
  "auctionId": "guid",
  "auctionStatus": "Ended",
  "settlementStatus": "PendingPayment",
  "acceptedPriceAmount": 14000000,
  "paymentDeadlineAt": "2026-11-20T15:30:00Z",
  "consecutiveDeclineCount": 0,
  "reserveRestrictedUntil": null,
  "respondedAt": "2026-11-19T15:30:00Z"
}
```

### 3.4. Errors
* `403 FORBIDDEN`: Không phải Seller sở hữu phiên.
* `404 AUCTION_NOT_FOUND`: Phiên không tồn tại.
* `409 NOT_AWAITING_SELLER_APPROVAL`: Phiên không nằm ở trạng thái chờ duyệt bán dưới giá sàn.
* `409 SELLER_APPROVAL_EXPIRED`: Đã quá hạn **12 giờ** quyết định.

---

## 4. API Seller Từ chối bán dưới giá sàn (`POST /api/v1/auctions/{id}/reserve-approval/decline`)

### 4.1. Tại sao sinh ra & Giải quyết việc gì?
- **Lý do sinh ra:** Cho phép Seller chủ động từ chối bán nếu giá đấu thấp hơn kỳ vọng tối thiểu, giúp hoàn trả ngay tiền cọc cho Hạng 1 và mở khóa sản phẩm để Seller đăng lại (không phải đợi hết 12 giờ).
- **Bài toán giải quyết:** Ghi nhận số lần từ chối liên tiếp (`ConsecutiveDeclineCount`) để chống Seller lạm dụng đặt Giá sàn quá cao rồi hủy liên tục (đủ **3 lần liên tiếp** sẽ tự động khóa quyền đặt `ReservePrice` trong **14 ngày**).

### 4.2. Input
* **Path:** `id` (`Guid`, mã phiên đấu giá)
* **Body (Tùy chọn):** `{ "reason": "Giá vẫn còn quá thấp so với giá nhập" }`
* **Auth:** Role `seller` (chủ sở hữu phiên).

### 4.3. Output (`200 OK` — `ReserveApprovalResponse`)
```json
{
  "auctionId": "guid",
  "auctionStatus": "Failed",
  "settlementStatus": "Cancelled",
  "acceptedPriceAmount": null,
  "paymentDeadlineAt": null,
  "consecutiveDeclineCount": 2,
  "reserveRestrictedUntil": null,
  "respondedAt": "2026-11-19T15:30:00Z"
}
```

### 4.4. Errors
* `403 FORBIDDEN`: Không phải Seller sở hữu phiên.
* `404 AUCTION_NOT_FOUND`: Phiên không tồn tại.
* `409 NOT_AWAITING_SELLER_APPROVAL`: Phiên không nằm ở trạng thái chờ duyệt bán dưới giá sàn.

---

## 5. API Xem trạng thái giới hạn Giá sàn của Seller (`GET /api/v1/seller/reserve-status`)

### 5.1. Tại sao sinh ra & Giải quyết việc gì?
- **Lý do sinh ra:** Trước khi Seller mở form tạo phiên mới, FE cần biết Seller đã từ chối bán dưới giá sàn bao nhiêu lần liên tiếp và có đang bị khóa quyền đặt `ReservePrice` (14 ngày) hay không để khóa/ẩn ô nhập Giá sàn và hiện cảnh báo nhắc nhở ngay trên UI.

### 5.2. Input & Output (`200 OK` — `SellerReserveStatusResponse`)
* **Auth:** Role `seller` (lấy `SellerId` từ token).
```json
{
  "sellerId": "guid",
  "consecutiveDeclineCount": 2,
  "isReserveRestricted": false,
  "reserveRestrictedUntil": null
}
```

### 5.3. Errors
* `401 UNAUTHORIZED` / `403 FORBIDDEN`: Chưa đăng nhập hoặc không phải tài khoản `seller`.
