# Hướng dẫn Frontend (Admin Panel) — Tích hợp API Tua nhanh Thời gian Phiên đấu giá (QA / Staging Test)

> **Service:** AuctionService (Port `8005`)  
> **Đối tượng sử dụng trên UI:** Chỉ hiển thị cho tài khoản có role **`admin`** hoặc **`staff`**.

---

## 1. Khi nào Frontend cần hiển thị & thao tác các API này?

Các API này phục vụ **Admin/QA thao tác trực tiếp trên giao diện chi tiết phiên đấu giá (Admin Auction Detail)** để kiểm thử các luồng phụ thuộc thời gian mà không cần chờ đợi:

| Tình huống cần test | Trạng thái hiện tại | API cần gọi | Payload gợi ý |
| :--- | :--- | :--- | :--- |
| **Mở phiên `Scheduled` ngay lập tức** | `status = Scheduled` | `PATCH /api/v1/admin/auctions/{id}/schedule` | `scheduledStartAt = now`, `scheduledEndAt = now + 30 phút` |
| **Test gia hạn (Anti-sniping) khi còn 5 phút** | `status = Running` | `PATCH /api/v1/admin/auctions/{id}/schedule` | `{ "remainingSeconds": 300 }` |
| **Test kết thúc phiên ngay (chốt người thắng / giá sàn)** | `status = Running` | `PATCH /api/v1/admin/auctions/{id}/schedule` | `{ "remainingSeconds": 0 }` (hoặc `5` giây) |
| **Test hết hạn 12h Seller duyệt bán dưới giá sàn** | `settlementStatus = AwaitingSellerApproval` | `PATCH /api/v1/admin/auctions/{id}/settlement-deadlines` | `{ "sellerApprovalRemainingSeconds": 0 }` |
| **Test hết hạn 24h Buyer thanh toán (thắng thường / Buy Now)** | `settlementStatus = PendingPayment` | `PATCH /api/v1/admin/auctions/{id}/settlement-deadlines` | `{ "paymentRemainingSeconds": 0 }` |
| **Test hết hạn lời mời Second-Chance (người về nhì)** | Có Winner Offer `Pending` | `PATCH /api/v1/admin/auctions/{id}/settlement-deadlines` | `{ "winnerOfferRemainingSeconds": 0 }` |

---

## 2. Chi tiết tích hợp Frontend

### A. Giao diện gợi ý (Block "QA / Time Controls" trong Admin Auction Detail)

Chỉ render block này nếu user hiện tại có role `admin` hoặc `staff`. Hiển thị các nút bấm nhanh theo trạng thái phiên:

1. **Khi `status === "Scheduled"` (hoặc `"Draft"`):**
   - Nút **"Bắt đầu ngay (chạy 30 phút)"**:
     - Gọi `PATCH /api/v1/admin/auctions/{id}/schedule`
     - Body: `{ "scheduledStartAt": new Date().toISOString(), "remainingSeconds": 1800, "reason": "Admin start now" }`
2. **Khi `status === "Running"`:**
   - Nút **"Còn 5 phút (Test gia hạn)"**:
     - Body: `{ "remainingSeconds": 300, "reason": "Test anti-sniping" }`
   - Nút **"Còn 30 giây"**:
     - Body: `{ "remainingSeconds": 30, "reason": "Test near end" }`
   - Nút **"Kết thúc ngay"**:
     - Body: `{ "remainingSeconds": 0, "reason": "Test auction end" }`
   - Hoặc một ô DateTime Picker cho phép nhập tùy ý `scheduledEndAt`.
3. **Khi phiên đã kết thúc (`status === "Ended"`):**
   - Nếu `settlementStatus === "AwaitingSellerApproval"`:
     - Hiện nút **"Hết hạn duyệt giá sàn (12h) ngay"** → gọi `PATCH .../settlement-deadlines` với `{ "sellerApprovalRemainingSeconds": 0 }`.
   - Nếu `settlementStatus === "PendingPayment"`:
     - Hiện nút **"Hết hạn thanh toán (24h) ngay"** → gọi `PATCH .../settlement-deadlines` với `{ "paymentRemainingSeconds": 0 }`.
   - Nếu có Second-Chance Offer đang `Pending`:
     - Hiện nút **"Hết hạn Second-Chance ngay"** → gọi `PATCH .../settlement-deadlines` với `{ "winnerOfferRemainingSeconds": 0 }`.

---

## 3. Cách Frontend xử lý sau khi gọi API thành công (`200 OK`)

1. **Cập nhật UI ngay lập tức từ Response:**
   - `PATCH .../schedule` trả về `scheduledStartAt`, `originalEndAt`, `scheduledEndAt`, `status`. FE cập nhật lại đồng hồ đếm ngược (countdown) theo `scheduledEndAt` mới.
   - `PATCH .../settlement-deadlines` trả về `sellerApprovalDeadlineAt`, `paymentDeadlineAt`, `winnerOfferExpiresAt`. FE cập nhật lại countdown tương ứng.
2. **Lưu ý về độ trễ Background Worker (3–10 giây):**
   - Khi bấm **"Kết thúc ngay"** (`remainingSeconds: 0`) hoặc **"Hết hạn ... ngay"** (`...RemainingSeconds: 0`), API chỉ dời mốc thời gian về `now`, còn **Background Worker** của `AuctionService` sẽ tự động quét và chuyển trạng thái thật sau vài giây (để đảm bảo chạy đúng quy trình khóa/nhả cọc, gửi Notification, đồng bộ Commerce).
   - **Khuyến nghị FE:** Sau khi gọi thành công với giá trị `0` (hoặc `<= 10s`), FE nên hiện toast *"Đã cập nhật mốc thời gian, hệ thống sẽ xử lý trong vài giây"* và **auto-refresh (refetch `GET /api/v1/auctions/{id}` hoặc `saga`) sau 3–5 giây** (hoặc lắng nghe SignalR event nếu màn hình đang subscribe).

---

## 4. Xử lý lỗi cần lưu ý trên FE

| HTTP Status | `errorCode` | Nguyên nhân & Cách FE hiển thị |
| :--- | :--- | :--- |
| `403` | `ROLE_NOT_ALLOWED` | User không phải `admin`/`staff` (ẩn UI này với user thường). |
| `404` | `AUCTION_NOT_FOUND` | Phiên không tồn tại hoặc đã bị xóa. |
| `400` | `VAL_INVALID_INPUT` | Truyền thiếu field, số giây âm, hoặc `scheduledEndAt <= scheduledStartAt`. Hiển thị message từ `detail`/`message` của API. |
| `409` | `INVALID_AUCTION_STATUS` | Phiên đã kết thúc/hủy rồi, hoặc cố sửa `scheduledStartAt` khi phiên đang `Running`. FE cần refetch lại trạng thái mới nhất của phiên. |
| `409` | `INVALID_SETTLEMENT_STATUS` | Trạng thái thanh toán/duyệt giá sàn đã thay đổi trước đó (vd: buyer vừa kịp thanh toán). FE refetch lại trang. |
| `409` | `WINNER_OFFER_NOT_FOUND` | Không còn lời mời Second-Chance nào đang `Pending`. |
