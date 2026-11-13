# 02 · POST /api/v1/auctions/{id}/deposits

> ⚠️ **v0.4 (hướng C):** không còn redirect sang cổng thanh toán. Cọc được khóa đồng bộ từ số dư ví qua Wallet W1, response trả thẳng `Authorized`. Xem `26_UPDATE_v0.4_option-C.md` mục 4.2. Nội dung bên dưới là bản hold của cổng, chỉ để tham khảo.

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer) |
| Auth | JWT, role Buyer |
| Headers | `Idempotency-Key` (bắt buộc) |
| Gọi ra ngoài | User `19`, Wallet/Payment `14` |

## 1. Giải quyết bài toán gì
Tạo yêu cầu đóng cọc và trả về đường dẫn để Buyer **tự thanh toán qua Payment Gateway** (Authorization/Hold). Không lấy từ Nexus Wallet, không chuyển vào ví hệ thống. Cọc chỉ thành `Authorized` khi nhận callback `04`.

## 2. Request
```json
{
  "acceptedPolicyVersion": 3,
  "acceptedDepositAmount": 50000,
  "returnUrl": "https://app.nexus.vn/auctions/{id}/deposit-return"
}
```
| Trường | Bắt buộc | Ghi chú |
|---|---|---|
| `acceptedPolicyVersion` | ✔ | Version policy Buyer đã thấy ở API 01 |
| `acceptedDepositAmount` | ✔ | Số cọc Buyer đã xác nhận; BE so với số tính lại |
| `returnUrl` | ✔ | Phải thuộc danh sách domain cho phép |

## 3. Xử lý
1. Kiểm tra giống API 01 (role, phiên mở, hạn chế, hồ sơ User).
2. Tính lại cọc; nếu khác `acceptedDepositAmount` → 409 `DEPOSIT_TERMS_CHANGED`.
3. Khoá duy nhất (`AuctionId`, `BidderId`):
   - Chưa có → tạo `Pending`, `AttemptCount=1`.
   - Đang `Pending` còn hạn → trả lại bản ghi cũ (không gọi Wallet lần 2).
   - `Authorized` → trả 200 bản ghi cũ.
   - `Failed`/`Expired` → dùng lại bản ghi, `AttemptCount+1`, về `Pending`.
4. Gọi Wallet `14` với `referenceId = depositId`, `idempotencyKey = deposit:{depositId}:{attempt}`.
5. Lưu `GatewayReference = holdId`, trả `paymentRedirectUrl`.

## 4. Response
**201 Created** (tạo mới / thử lại):
```json
{
  "depositId": "…",
  "status": "Pending",
  "amount": 50000,
  "ratePercent": 5,
  "policyVersion": 3,
  "paymentRedirectUrl": "https://gateway…",
  "paymentExpiresAt": "2026-11-12T10:15:00Z"
}
```
**200 OK**: cọc đã `Authorized` (idempotent), không có `paymentRedirectUrl`.

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `IDEMPOTENCY_KEY_REQUIRED` / `VALIDATION_ERROR` | Thiếu header hoặc body sai |
| 403 | `ROLE_NOT_ALLOWED`, `SELLER_CANNOT_BID_OWN_AUCTION`, `BIDDER_RESTRICTED`, `ACCOUNT_BANNED` | Như API 01 |
| 404 | `AUCTION_NOT_FOUND` | |
| 409 | `AUCTION_NOT_OPEN_FOR_DEPOSIT` | Phiên đã kết thúc/hủy |
| 409 | `DEPOSIT_TERMS_CHANGED` | Policy/mức cọc đổi. `details` trả điều khoản mới, FE hiển thị lại để Buyer xác nhận |
| 409 | `IDEMPOTENCY_KEY_REUSED` | Cùng key nhưng body khác |
| 422 | `RETURN_URL_NOT_ALLOWED` | Domain không hợp lệ |
| 422 | `HOLD_DURATION_NOT_SUPPORTED` | Wallet báo cổng không giữ đủ thời gian (xem Overview mục 7.1) |
| 502 | `PAYMENT_PROVIDER_ERROR` | Cổng thanh toán lỗi. Bản ghi giữ `Pending`/`Failed`, Buyer thử lại |
| 503 | `PAYMENT_SERVICE_UNAVAILABLE` / `USER_SERVICE_UNAVAILABLE` | Retry |

## 6. Idempotency & đồng thời
- Cùng `Idempotency-Key` → cùng response. Hai request song song của cùng Buyer: ràng buộc unique chặn bản ghi thứ hai, request sau nhận bản ghi đã có.
- Nếu gọi Wallet lỗi giữa chừng, bản ghi `Pending` vẫn tồn tại; lần gọi sau dùng lại cùng `idempotencyKey` của Wallet nên không tạo hold trùng.
