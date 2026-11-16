# 20 · POST /api/v1/internal/users/{userId}/auction-violations

| | |
|---|---|
| Service | **User** · MỚI (User sở hữu Streak) |
| Caller | Auction (job quét, sau khi xử lý callback API 10) |
| Auth | `X-Internal-Service-Key` bắt buộc |

## 1. Giải quyết bài toán gì
Ghi một **vi phạm đấu giá** của Buyer: tăng Streak, lưu lịch sử, trừ uy tín, kiểm tra áp hình phạt. Auction chỉ báo sự kiện; **User quyết định** mức trừ uy tín và hình phạt theo cấu hình của User.

## 2. Request
```json
{
  "violationType": "WINNER_NON_PAYMENT",
  "auctionId": "…",
  "orderId": "…",
  "occurredAt": "2026-11-13T10:00:01Z",
  "depositForfeitedAmount": 50000
}
```
`violationType`: `WINNER_NON_PAYMENT` (Capture cọc + Streak + trừ uy tín) · `SECOND_CHANCE_NON_PAYMENT` (Streak + trừ uy tín, không phạt tiền).
**Không** gọi API này khi người về nhì từ chối/hết hạn trả lời.

## 3. Response
**201 Created** (happy case):
```json
{
  "violationId": "…",
  "created": true,
  "previousStreak": 1,
  "newStreak": 2,
  "reputationBefore": 90,
  "reputationAfter": 80,
  "sanction": { "type": "DEPOSIT_RATE_INCREASED", "until": null }
}
```
Streak → hình phạt (tham khảo): 1 cảnh báo · 2 tăng cọc · 3 `TEMP_RESTRICTED` (có `until`) · 4+ `ADMIN_REVIEW`.
**200 OK** `created:false`: đã ghi (cùng khoá), trả lại kết quả cũ.

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường | Lỗi lập trình |
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `USER_NOT_FOUND` | | Dừng, cảnh báo |
| 422 | `INVALID_VIOLATION_TYPE` | Loại lạ | |
| 503 | `USER_SERVICE_UNAVAILABLE` | | **Retry** (job quét) |

## 5. Idempotency
Khoá duy nhất (`userId`, `auctionId`, `violationType`): retry không tăng Streak lần hai. Đây là điểm quan trọng nhất của API, vì job quét sẽ retry.

## 6. Ghi chú
Reset Streak do **API 25** xử lý (khi Buyer thanh toán đúng hạn).
