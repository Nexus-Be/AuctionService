# 21 · GET /api/v1/admin/users/{userId}/auction-violations

| | |
|---|---|
| Service | **User** · MỚI |
| Caller | FE (Admin, Staff) |
| Auth | JWT, role Admin hoặc Staff (chỉ xem) |
| Query | `page`, `pageSize` |

## 1. Giải quyết bài toán gì
Admin/Staff xem **Streak hiện tại, hạn chế hiện hành và lịch sử vi phạm đấu giá** của Buyer khi xử lý khiếu nại hoặc xem xét đình chỉ (Staff đã có quyền "xem uy tín và lịch sử phạt").

## 2. Response 200 (happy case)
```json
{
  "userId": "…",
  "currentStreak": 2,
  "auctionRestriction": { "isRestricted": false, "type": null, "until": null },
  "items": [
    {
      "violationId": "…", "violationType": "WINNER_NON_PAYMENT",
      "auctionId": "…", "orderId": "…",
      "streakAfter": 2, "reputationDelta": -10,
      "depositForfeitedAmount": 50000,
      "sanction": "DEPOSIT_RATE_INCREASED",
      "createdAt": "2026-11-13T10:00:01Z", "source": "SYSTEM"
    }
  ],
  "page": 1, "pageSize": 20, "totalItems": 2
}
```
`source`: `SYSTEM` (từ API 20) hoặc `ADMIN` (từ API 22).

## 3. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `FORBIDDEN` | Không phải Admin/Staff |
| 404 | `USER_NOT_FOUND` | |
| 400 | `VALIDATION_ERROR` | Phân trang sai |
