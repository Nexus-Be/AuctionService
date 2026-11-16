# 17 · GET /api/v1/internal/payments/deposit-holds/{holdId}

> ⚠️ **v0.4: ĐÃ THAY THẾ bởi hướng C (W4).** Contract mới ở `WALLET_REPLY_AND_API_REQUEST.md` và `26_UPDATE_v0.4_option-C.md`. Nội dung bên dưới là hướng hold của cổng Stripe, chỉ để tham khảo.

| | |
|---|---|
| Service | **Wallet/Payment** · MỚI (đề xuất) |
| Caller | Auction |
| Auth | `X-Internal-Service-Key` bắt buộc |

## 1. Giải quyết bài toán gì
Auction **chủ động xác minh** trạng thái hold, vì không có event và callback có thể mất hoặc đến muộn. Dùng khi: Buyer gọi `03?refresh=true`, cọc `Pending` quá 15 phút, `Authorized` sắp hết hạn, hoặc cần đối soát trước khi bid.

## 2. Response 200 (happy case)
```json
{
  "holdId": "…",
  "referenceId": "<depositId>",
  "status": "Authorized",
  "mode": "AUTHORIZE_HOLD",
  "amount": 50000,
  "authorizedAt": "2026-11-12T10:03:00Z",
  "providerHoldExpiresAt": "2026-11-21T10:00:00Z",
  "capturedAmount": 0,
  "releasedAt": null
}
```
`status`: `PendingAuthorization` · `Authorized` · `Captured` · `Released` · `Voided` · `Expired` · `Failed`.

## 3. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `HOLD_NOT_FOUND` | | Coi như `Failed` nếu deposit còn `Pending` quá lâu |
| 503 | `PAYMENT_SERVICE_UNAVAILABLE` | | Giữ trạng thái cũ, trả `stale:true` cho FE |

## 4. Ghi chú
Auction đồng bộ trạng thái deposit theo kết quả này bằng cùng logic của API 04 (bảng chuyển trạng thái hợp lệ), để hai đường (callback và polling) cho cùng kết quả.
