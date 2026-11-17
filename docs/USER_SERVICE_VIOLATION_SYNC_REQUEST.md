# YÊU CẦU ĐIỀU CHỈNH API 20 BÊN USERSERVICE (SECOND-CHANCE NON-PAYMENT)

> **Bên gửi:** Team AuctionService (`:8005`)  
> **Bên nhận:** Team UserService (`:8001`)  
> **Endpoint liên quan:** `POST /api/v1/internal/users/{userId}/auction-violations` (API 20)

---

## 1. Bối cảnh thay đổi nghiệp vụ

Theo quy tắc giữ cọc mới (giữ cọc Top 2 — cả Người thắng #1 và Người về nhì khi kết thúc phiên):
- Nếu Người về nhì **từ chối (`Decline`)** hoặc **để lời mời hết hạn 24h**: Không phạt, cọc được hoàn lại 100%.
- Nếu Người về nhì **đã bấm Chấp nhận (`Accept`)** tạo đơn hàng nhưng sau đó **không thanh toán đúng hạn (`PaymentTimedOut`)**:
  - **Cọc của Người về nhì bị tịch thu (`Forfeit`)** và chia **70% cho Seller / 30% cho Sàn**, giống hệt Người thắng #1.
  - Hệ thống vẫn ghi nhận vi phạm `SecondChanceNonPayment` sang `UserService` (tăng `ViolationStreak`, trừ điểm uy tín và áp chế tài như hiện tại).

---

## 2. Điểm đang lệch ở `UserService` hiện tại

Hiện tại trong API 20 (`POST /api/v1/internal/users/{userId}/auction-violations`), `UserService` đang có rule chặn:
- Nếu `violationType == "SecondChanceNonPayment"` mà `depositForfeitedAmount > 0` thì trả lỗi:
  - **HTTP `400 Bad Request`** — `VAL_INVALID_INPUT`: `"Deposit forfeited amount must be 0 for second-chance non-payment."`

Do ràng buộc này, `AuctionService` hiện chưa thể truyền số tiền cọc thực tế bị tịch thu của Người về nhì sang `UserService` để lưu vào lịch sử vi phạm (`AuctionViolations` — API 21).

---

## 3. Yêu cầu phía `UserService` cần sửa

1. **Bỏ rule bắt buộc `depositForfeitedAmount == 0` đối với `SecondChanceNonPayment`**:
   - Chỉ cần validate chung cho cả `WinnerNonPayment` và `SecondChanceNonPayment`: `depositForfeitedAmount >= 0` (nếu `< 0` mới trả `400 VAL_INVALID_INPUT`: `"Deposit forfeited amount must be non-negative."`).
2. **Giữ nguyên các phần còn lại**:
   - Giữ nguyên logic tăng `ViolationStreak`, trừ điểm uy tín, áp chế tài (`Warning`, `DepositRateIncreased`, `TempRestricted`, `Suspended`), và khóa idempotency `(UserId, AuctionId, ViolationType)`.
   - Lưu đúng giá trị `depositForfeitedAmount` nhận được vào bản ghi vi phạm để Admin/Staff đối soát chính xác trên API 21 (`GET /api/v1/admin/users/{userId}/auction-violations`).

---

## 4. Payload mẫu sau khi cập nhật

```json
POST /api/v1/internal/users/{userId}/auction-violations
{
  "violationType": "SecondChanceNonPayment",
  "auctionId": "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d",
  "orderId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "occurredAt": "2026-11-17T10:00:00Z",
  "depositForfeitedAmount": 1000000
}
```

> **Ghi chú phối hợp:** Sau khi team `UserService` cập nhật xong rule này và deploy/merge, báo lại cho team `AuctionService` để bên Auction mở truyền `depositForfeitedAmount` thực tế cho `SecondChanceNonPayment` (thay cho giá trị tạm `0` hiện tại).
