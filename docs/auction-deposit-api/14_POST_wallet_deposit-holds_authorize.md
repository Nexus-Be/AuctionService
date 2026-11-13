# 14 · POST /api/v1/internal/payments/deposit-holds

> ⚠️ **v0.4: ĐÃ THAY THẾ bởi hướng C (W1).** Contract mới ở `WALLET_REPLY_AND_API_REQUEST.md` và `26_UPDATE_v0.4_option-C.md`. Nội dung bên dưới là hướng hold của cổng Stripe, chỉ để tham khảo.

| | |
|---|---|
| Service | **Wallet/Payment** · MỚI (đề xuất; chủ tích hợp Gateway) |
| Caller | Auction (khi xử lý API 02) |
| Auth | `X-Internal-Service-Key` bắt buộc |
| Headers | `Idempotency-Key` |

## 1. Giải quyết bài toán gì
Auction cần Buyer **đóng cọc qua Payment Gateway bằng Authorization/Hold**. Auction không nói chuyện với cổng thanh toán mà nhờ Wallet/Payment tạo hold và trả đường dẫn để Buyer thanh toán. Kết quả báo về qua callback API 04.

## 2. Request
```json
{
  "referenceType": "AUCTION_DEPOSIT",
  "referenceId": "<depositId>",
  "payerUserId": "…",
  "amount": 50000,
  "currency": "VND",
  "holdUntil": "2026-11-21T10:00:00Z",
  "returnUrl": "https://app.nexus.vn/auctions/{id}/deposit-return",
  "callbackUrl": "https://gateway.nexus.vn/api/v1/internal/deposits/{depositId}/authorization-result"
}
```
`holdUntil` là thời điểm tối thiểu hold phải còn hiệu lực (kết thúc phiên + hạn thanh toán + buffer).

## 3. Response 201 (happy case)
```json
{
  "holdId": "…",
  "status": "PendingAuthorization",
  "mode": "AUTHORIZE_HOLD",
  "redirectUrl": "https://gateway…",
  "redirectExpiresAt": "2026-11-12T10:15:00Z",
  "providerHoldExpiresAt": null
}
```
`mode`: `AUTHORIZE_HOLD` (thông thường) hoặc `CHARGE_AND_REFUND` (cổng không hỗ trợ hold đủ lâu: thu thật rồi hoàn cho người không thắng).

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường, amount ≤ 0 | Lỗi cấu hình, log + 500 cho FE |
| 401 | `INTERNAL_KEY_INVALID` | | |
| 409 | `IDEMPOTENCY_KEY_REUSED` | Cùng key, body khác | Không retry |
| 422 | `HOLD_DURATION_NOT_SUPPORTED` | `holdUntil` vượt giới hạn của cổng | Trả FE `HOLD_DURATION_NOT_SUPPORTED`; nếu Wallet hỗ trợ thì đổi `mode` |
| 422 | `PAYER_NOT_ELIGIBLE` | Tài khoản không được thanh toán | Trả FE lỗi |
| 502 | `PROVIDER_ERROR` | Cổng lỗi | Deposit giữ `Pending`/`Failed`, Buyer thử lại |
| 503 | `PAYMENT_SERVICE_UNAVAILABLE` | | Retry |

## 5. Idempotency
Khoá `deposit:{depositId}:{attempt}`: gọi lại trả **cùng hold** và cùng `redirectUrl` nếu còn hạn, không tạo hold thứ hai.
