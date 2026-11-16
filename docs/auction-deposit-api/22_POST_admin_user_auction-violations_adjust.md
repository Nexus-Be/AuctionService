# 22 · POST /api/v1/admin/users/{userId}/auction-violations/adjust

| | |
|---|---|
| Service | **User** · MỚI |
| Caller | FE (Admin) |
| Auth | JWT, role Admin (Staff **không** có quyền) |

## 1. Giải quyết bài toán gì
Admin **điều chỉnh Streak và hạn chế đấu giá** (reset khi khiếu nại đúng, áp hạn chế/đình chỉ thủ công, gỡ hạn chế). Mọi thay đổi bắt buộc có lý do và ghi audit (ai, lúc nào, giá trị cũ/mới).

## 2. Request
```json
{
  "action": "RESET_STREAK",
  "value": null,
  "until": null,
  "reason": "Khiếu nại hợp lệ, lỗi cổng thanh toán"
}
```
| `action` | `value` | `until` |
|---|---|---|
| `RESET_STREAK` | — | — |
| `SET_STREAK` | số ≥ 0 | — |
| `APPLY_RESTRICTION` | — | bắt buộc (tương lai) |
| `LIFT_RESTRICTION` | — | — |
| `SUSPEND` | — | tuỳ chọn (không có = vô thời hạn) |

## 3. Response 200 (happy case)
```json
{
  "userId": "…",
  "before": { "streak": 3, "restriction": "TEMP_RESTRICTED" },
  "after":  { "streak": 0, "restriction": null },
  "auditId": "…"
}
```

## 4. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu `reason`, `value` âm, `until` quá khứ |
| 401/403 | `UNAUTHORIZED` / `FORBIDDEN` | Chỉ Admin |
| 404 | `USER_NOT_FOUND` | |
| 409 | `NOTHING_TO_CHANGE` | Giá trị mới trùng giá trị hiện tại |
| 409 | `CANNOT_RESTRICT_ADMIN` | Áp hạn chế lên tài khoản Admin |

## 5. Ghi chú
Hạn chế này chỉ ảnh hưởng đấu giá. Auction thấy thay đổi sau tối đa một chu kỳ cache của API 19 (30–60s).
