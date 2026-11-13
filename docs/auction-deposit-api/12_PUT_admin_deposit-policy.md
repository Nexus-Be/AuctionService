# 12 · PUT /api/v1/admin/auction-configs/deposit-policy

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Admin) |
| Auth | JWT, role Admin |
| Headers | `If-Match: <version hiện hành>` |

## 1. Giải quyết bài toán gì
Admin cập nhật chính sách cọc. Mỗi lần lưu tạo **phiên bản mới** (versioned); cọc đã tạo giữ nguyên số tiền theo version cũ (đã snapshot ở API 02). Mọi thay đổi có **lý do + audit** (ai, lúc nào, giá trị cũ/mới).

## 2. Request
Cùng cấu trúc response API 11, thêm `reason`:
```json
{
  "reason": "Tăng cọc cho người mới",
  "tiers": [ … ],
  "newUserRatePercent": 20,
  "streakSurcharges": [ { "streak": 2, "additionalPercent": 10 } ],
  "maxRatePercent": 30,
  "minDepositAmount": 50000,
  "forfeiture": { "sellerPercent": 70, "platformPercent": 30 }
}
```

## 3. Validate
- `tiers` phủ kín 0–100, không chồng lấn, không hở.
- Mọi `ratePercent` ∈ [0, `maxRatePercent`] và `maxRatePercent` ≤ 100.
- `sellerPercent + platformPercent = 100`.
- `minDepositAmount ≥ 0`.
- `reason` bắt buộc (≥ 5 ký tự).

## 4. Response 200 (happy case)
```json
{ "version": 4, "effectiveFrom": "2026-11-12T11:00:00Z" }
```

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường, `reason` ngắn |
| 400 | `POLICY_TIERS_INVALID` | Tier hở/chồng/ngoài khoảng |
| 400 | `FORFEITURE_SPLIT_NOT_100` | Tổng chia ≠ 100 |
| 401/403 | `UNAUTHORIZED` / `FORBIDDEN` | Chỉ Admin |
| 412 | `VERSION_CONFLICT` | `If-Match` không khớp (Admin khác vừa sửa) |
| 428 | `IF_MATCH_REQUIRED` | |

## 6. Ghi chú
Chỉ áp dụng cho cọc tạo **sau** `effectiveFrom`. Phần tịch thu (`forfeiture`) lấy theo version của deposit lúc tạo để Buyer đã thấy đúng chính sách khi xác nhận.

