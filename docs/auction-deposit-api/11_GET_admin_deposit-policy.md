# 11 · GET /api/v1/admin/auction-configs/deposit-policy

| | |
|---|---|
| Service | Auction · **MỚI** (nằm trong `auction-configs` có sẵn) |
| Caller | FE (Admin) |
| Auth | JWT, role Admin (Staff không xem cấu hình) |
| Query | `version` (tuỳ chọn, mặc định bản hiện hành) |

## 1. Giải quyết bài toán gì
Admin xem **chính sách cọc hiện hành** và các phiên bản cũ. API 01 và 02 đọc cùng nguồn này để tính cọc.

## 2. Response 200 (happy case)
```json
{
  "version": 3,
  "effectiveFrom": "2026-11-01T00:00:00Z",
  "updatedBy": "…",
  "reason": "Điều chỉnh tier uy tín",
  "tiers": [
    { "minReputation": 80, "maxReputation": 100, "ratePercent": 5 },
    { "minReputation": 60, "maxReputation": 79,  "ratePercent": 10 },
    { "minReputation": 40, "maxReputation": 59,  "ratePercent": 15 },
    { "minReputation": 0,  "maxReputation": 39,  "ratePercent": 20 }
  ],
  "newUserRatePercent": 20,
  "streakSurcharges": [ { "streak": 2, "additionalPercent": 10 } ],
  "maxRatePercent": 30,
  "minDepositAmount": 50000,
  "forfeiture": { "sellerPercent": 70, "platformPercent": 30 }
}
```
Kèm `history`: danh sách `{version, effectiveFrom, updatedBy, reason}` khi gọi không có `version`.

## 3. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `FORBIDDEN` | Không phải Admin |
| 404 | `POLICY_VERSION_NOT_FOUND` | `version` không tồn tại |

## 4. Ghi chú
Bảng tỷ lệ trong spec chỉ là minh hoạ, chính sách cuối do Admin cấu hình. Ngưỡng hạn chế theo Streak (vd Streak 3 → tạm hạn chế) nằm ở **User service**, không ở đây.

