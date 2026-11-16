# 16 · POST /api/v1/internal/payments/deposit-holds/{holdId}/release

> ⚠️ **v0.4: ĐÃ THAY THẾ bởi hướng C (W3).** Contract mới ở `WALLET_REPLY_AND_API_REQUEST.md` và `26_UPDATE_v0.4_option-C.md`. Nội dung bên dưới là hướng hold của cổng Stripe, chỉ để tham khảo.

| | |
|---|---|
| Service | **Wallet/Payment** · MỚI (đề xuất) |
| Caller | Auction (fast path + job quét) |
| Auth | `X-Internal-Service-Key` bắt buộc |
| Headers | `Idempotency-Key` = `release:{depositId}` |

## 1. Giải quyết bài toán gì
Giải phóng khoản hold, **Buyer không mất cọc**. Dùng khi: bidder không thắng (phiên kết thúc), Seller/Admin hủy phiên, phiên không có bid hợp lệ hoặc `FAILED`, cọc đến muộn, số tiền lệch.

## 2. Request
```json
{ "reason": "AUCTION_ENDED_NOT_WINNER" }
```
`reason`: `AUCTION_ENDED_NOT_WINNER` · `AUCTION_CANCELLED` · `AUCTION_FAILED` · `AMOUNT_MISMATCH` · `LATE_AUTHORIZATION` · `ADMIN_FORCE_CANCEL`.

## 3. Response 200 (happy case)
```json
{ "holdId": "…", "status": "Released", "releasedAt": "2026-11-13T10:00:03Z" }
```
- Hold còn `PendingAuthorization` (Buyer chưa thanh toán xong) → **huỷ hold**, trả `status: "Voided"`.
- Hold đã `Released`/`Voided` → 200, trả kết quả cũ (idempotent).

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `HOLD_NOT_FOUND` | | Coi như đã giải phóng, ghi log |
| 409 | `HOLD_ALREADY_CAPTURED` | Hold đã Capture | **Không** Release; cảnh báo Admin |
| 409 | `HOLD_EXPIRED` | Hold đã tự hết hạn | Coi là đã giải phóng, deposit `Expired` |
| 502/503 | `PROVIDER_ERROR` / `PAYMENT_SERVICE_UNAVAILABLE` | | **Retry** cùng key; `pendingAction=RELEASE_RETRYING` (API 13) |

## 5. Ghi chú
- Release hàng loạt khi phiên kết thúc/hủy: Auction đặt `PendingAction=Release` cho từng deposit, job quét gọi từng cái, một deposit lỗi không chặn các deposit khác.
- Release thất bại kéo dài → Buyer bị khoá tiền lâu; cần cảnh báo.
