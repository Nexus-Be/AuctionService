# 04 · POST /api/v1/internal/deposits/{depositId}/authorization-result

> ⚠️ **v0.4: KHÔNG DÙNG với hướng C.** Khóa số dư là đồng bộ nên không có callback. Giữ file để tham khảo nếu sau này dùng hold của cổng thanh toán.

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | Wallet/Payment service (callback từ cổng thanh toán) |
| Auth | `X-Internal-Service-Key` **bắt buộc** |

## 1. Giải quyết bài toán gì
Không có event bus, nên Wallet báo kết quả Authorization bằng HTTP callback. API này chuyển cọc `Pending → Authorized/Failed`, hoặc `Authorized → Expired` khi hold bị giải phóng ngoài dự kiến.

## 2. Request
```json
{
  "eventId": "evt-123",
  "holdId": "…",
  "result": "Authorized",
  "amount": 50000,
  "authorizedAt": "2026-11-12T10:03:00Z",
  "expiresAt": "2026-11-21T10:00:00Z",
  "failureCode": null,
  "providerRef": "…"
}
```
`result`: `Authorized` | `Failed` | `Expired` | `Voided`.

## 3. Xử lý
1. Kiểm tra key. Chống trùng: `eventId` trùng `LastGatewayEventId` của deposit, hoặc chuyển trạng thái không hợp lệ (bảng bên dưới) → trả 200 `applied:false`, không đổi gì. Không cần bảng Inbox.
2. Kiểm `holdId` khớp `GatewayReference` của deposit.
3. `Authorized`: kiểm `amount == deposit.Amount`. Lệch → đặt `Failed` và gọi Release `16` (reason `AMOUNT_MISMATCH`).
4. **Đến muộn:** nếu cọc `Authorized` nhưng phiên đã kết thúc/hủy/failed → đặt `Released` và gọi Release `16` (reason `LATE_AUTHORIZATION`). Không cho Buyer bid.
5. Bảng chuyển trạng thái hợp lệ:

| Từ | `result` | Sang |
|---|---|---|
| `Pending` | `Authorized` | `Authorized` (lưu `AuthorizedAt`, `ExpiresAt`) |
| `Pending` | `Failed` | `Failed` |
| `Authorized` | `Expired`/`Voided` | `Expired` |
| trạng thái khác | bất kỳ | giữ nguyên, `applied:false`, ghi log |

## 4. Response 200 (happy case)
```json
{ "depositId": "…", "status": "Authorized", "applied": true }
```
Callback trùng/cũ: vẫn 200 với `applied:false` để Wallet **không retry vô hạn**.

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Body thiếu/sai |
| 401 | `INTERNAL_KEY_INVALID` | Thiếu hoặc sai key |
| 404 | `DEPOSIT_NOT_FOUND` | Sai `depositId` |
| 422 | `HOLD_ID_MISMATCH` | `holdId` không khớp |
| 5xx | `INTERNAL_ERROR` | Wallet sẽ retry cùng `eventId` |

## 6. Ghi chú
- Phải chịu được callback **trùng, đảo thứ tự và đến muộn**.
- Nếu Wallet không bao giờ gọi (mất callback), `03?refresh=true` và job quét cọc `Pending` quá 15 phút sẽ gọi API 17 để đồng bộ.
