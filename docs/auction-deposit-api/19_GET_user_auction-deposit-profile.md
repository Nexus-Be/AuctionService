# 19 · GET /api/v1/internal/users/{userId}/auction-deposit-profile

| | |
|---|---|
| Service | **User** · MỚI |
| Caller | Auction (API 01, 02, 05; có cache) |
| Auth | `X-Internal-Service-Key` bắt buộc |

## 1. Giải quyết bài toán gì
Thay cho việc User chặn Buyer theo Reputation: User chỉ **cung cấp dữ kiện** (Reputation, Streak, hạn chế) để Auction tự tính mức cọc theo policy và quyết định chặn. Reputation và Streak **độc lập**, không thay thế nhau.

## 2. Response 200 (happy case)
```json
{
  "userId": "…",
  "accountStatus": "ACTIVE",
  "roles": ["Buyer"],
  "reputationScore": 90,
  "isNewUser": false,
  "violationStreak": 0,
  "auctionRestriction": {
    "isRestricted": false,
    "type": null,
    "until": null,
    "reason": null
  },
  "asOf": "2026-11-12T10:00:00Z"
}
```
- `isNewUser = true`: chưa có lịch sử; `reputationScore` có thể `null`.
- `auctionRestriction.type`: `TEMP_RESTRICTED` (Streak 3) · `SUSPENDED` (Admin đình chỉ). Hạn chế này **chỉ ảnh hưởng đấu giá**, không chặn mua thường.
- `accountStatus`: `ACTIVE` · `BANNED` · `DELETED`.

## 3. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `USER_NOT_FOUND` | | Từ chối thao tác |
| 503 | `USER_SERVICE_UNAVAILABLE` | | **Fail-closed** (không cọc/bid); dùng cache nếu còn hạn |

## 4. Ghi chú
- Cache phía Auction 30–60 giây theo `userId`; làm mới khi tạo cọc (API 02).
- Bảng tỷ lệ cọc nằm ở Auction (API 11), User **không** trả tỷ lệ.
