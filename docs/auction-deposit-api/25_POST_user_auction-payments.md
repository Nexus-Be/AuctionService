# 25 · POST /api/v1/internal/users/{userId}/auction-payments

| | |
|---|---|
| Service | **User** · MỚI (User sở hữu Streak) |
| Caller | Auction (job quét, sau khi xử lý callback API 10 với `outcome = PAID`) |
| Auth | `X-Internal-Service-Key` bắt buộc |

## 1. Giải quyết bài toán gì
Violation Streak là số lần vi phạm **liên tiếp**. API này báo User rằng Buyer vừa **thanh toán đúng hạn** một phiên thắng, để User **reset Streak về 0** (quy tắc đã chốt). Không có API này Streak chỉ tăng, Buyer thanh toán đúng nhiều phiên sau đó vẫn có thể bị đình chỉ.

## 2. Request
```json
{
  "auctionId": "…",
  "orderId": "…",
  "source": "WINNER",
  "paidAt": "2026-11-13T09:30:00Z"
}
```
`source`: `WINNER` | `SECOND_CHANCE`. Chỉ gọi khi order đấu giá `PAID` **trong hạn**.

## 3. Response
**200 OK** (happy case):
```json
{
  "recorded": true,
  "created": true,
  "previousStreak": 2,
  "newStreak": 0,
  "restrictionLifted": false
}
```
- Streak đang 0: vẫn 200, `previousStreak = 0`, `newStreak = 0` (không lỗi).
- Gọi lại cùng (`userId`, `auctionId`): 200 với `created:false`, trả kết quả cũ.
- `restrictionLifted`: reset Streak **không tự gỡ** hạn chế đang hiệu lực (`TEMP_RESTRICTED`, `SUSPENDED`); hạn chế hết khi qua `until` hoặc Admin gỡ (API 22). *(User chốt lại nếu muốn khác.)*

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường | Lỗi lập trình |
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `USER_NOT_FOUND` | | Dừng, cảnh báo |
| 422 | `INVALID_SOURCE` | `source` lạ | |
| 503 | `USER_SERVICE_UNAVAILABLE` | | **Retry** (job quét, cột `StreakResetState`) |

## 5. Idempotency
Khoá duy nhất (`userId`, `auctionId`). Gọi lại không đổi gì.

## 6. Ghi chú
- Auction chỉ gọi khi order PAID **đúng hạn**; hết hạn rồi mới trả (nếu Commerce cho phép) không reset.
- Lỗi API này **không chặn** việc chốt phiên `SETTLED`; chỉ retry nền.
