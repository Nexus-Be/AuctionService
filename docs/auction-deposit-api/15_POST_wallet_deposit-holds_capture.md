# 15 · POST /api/v1/internal/payments/deposit-holds/{holdId}/capture

> ⚠️ **v0.4: ĐÃ THAY THẾ bởi hướng C (W2).** Contract mới ở `WALLET_REPLY_AND_API_REQUEST.md` và `26_UPDATE_v0.4_option-C.md`. Nội dung bên dưới là hướng hold của cổng Stripe, chỉ để tham khảo.

| | |
|---|---|
| Service | **Wallet/Payment** · MỚI (đề xuất) |
| Caller | Auction (fast path + job quét) |
| Auth | `X-Internal-Service-Key` bắt buộc |
| Headers | `Idempotency-Key` = `capture:{depositId}` |

## 1. Giải quyết bài toán gì
Biến khoản hold thành **tiền thật**, trong hai trường hợp:
- `ORDER_PAYMENT`: Winner đã trả, cọc trở thành một phần của giá thắng (không tính cọc hai lần).
- `FORFEIT`: Winner không trả trong hạn, cọc bị tịch thu và chia cho Seller và Platform.

## 2. Request
```json
{
  "purpose": "FORFEIT",
  "amount": 50000,
  "orderId": null,
  "distribution": [
    { "beneficiaryType": "SELLER",   "beneficiaryId": "…", "amount": 35000 },
    { "beneficiaryType": "PLATFORM", "beneficiaryId": null, "amount": 15000 }
  ]
}
```
- `ORDER_PAYMENT`: bắt buộc `orderId`, `distribution` rỗng (tiền ghi vào escrow của order, Commerce phân phối khi `COMPLETED`).
- `FORFEIT`: bắt buộc `distribution`, tổng = `amount`. Auction tính theo `forfeiture` của policy version trong deposit; Seller = làm tròn xuống, Platform = phần còn lại.
- Capture **toàn bộ**, không Capture một phần.

## 3. Response 200 (happy case)
```json
{
  "holdId": "…", "status": "Captured", "capturedAmount": 50000,
  "capturedAt": "2026-11-13T10:00:05Z",
  "transfers": [ { "beneficiaryType": "SELLER", "amount": 35000, "ledgerRef": "…" } ]
}
```
Gọi lại với cùng key hoặc hold đã `Captured` cùng `purpose`/`amount`: **200 và trả kết quả cũ**.

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu `orderId`/`distribution` | Lỗi lập trình |
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `HOLD_NOT_FOUND` | | Dừng, cảnh báo |
| 409 | `HOLD_ALREADY_CAPTURED_DIFFERENT` | Đã Capture với purpose/amount khác | Dừng, cảnh báo Admin (nguy cơ Capture hai lần) |
| 409 | `HOLD_ALREADY_RELEASED` | Hold đã Release | Dừng, cảnh báo Admin |
| 409 | `HOLD_EXPIRED` | Hold đã hết hạn tại cổng | Không Capture được; cảnh báo Admin (Overview 7.1, 7.2) |
| 422 | `DISTRIBUTION_INVALID` | Tổng ≠ amount | Lỗi lập trình |
| 422 | `AMOUNT_MISMATCH` | amount ≠ số hold | Dừng, cảnh báo |
| 502/503 | `PROVIDER_ERROR` / `PAYMENT_SERVICE_UNAVAILABLE` | | **Retry** cùng key (backoff) |

## 5. Ghi chú
Capture lỗi vĩnh viễn sau khi Commerce đã nhận phần còn lại → thiếu tiền so với giá thắng. Cần cảnh báo Admin và đối soát tay.

## 6. ~~Hướng B~~ (HỦY BỎ ở v0.4, không còn cần vì dùng khóa số dư)

Trạng thái: **chờ Wallet trả lời**, chưa làm ở Giai đoạn 1. Giai đoạn 1 dùng hướng A (mặc định `captureMode = ON_RESOLUTION`) ở trên.

Khi `captureMode = ON_WINNER_DETERMINED`:
1. Lúc phiên kết thúc, với **Winner** gọi Capture `purpose = ESCROW` (giữ tiền thật trong escrow của Wallet, chưa quyết định đích). Người thua vẫn Release như cũ.
2. Hold chỉ cần sống đến lúc phiên kết thúc (≤ 168h), không cần thêm 24h thanh toán.
3. Khi có kết quả order, gọi Wallet chuyển khoản escrow đó: `ORDER_PAYMENT` (Winner trả, tính vào giá thắng), `FORFEIT` (Winner quá hạn, chia Seller/Platform), hoặc `REFUND` (Seller từ chối, hoàn Buyer).
4. Cần Wallet bổ sung một API "resolve escrow" (hoặc cho phép gọi lại endpoint này với `purpose` đích trên hold đã `Captured/ESCROW`). Chỉ cần thêm khi chọn hướng B.

Ảnh hưởng phía Auction: chỉ đổi **thời điểm** gọi Capture trong job kết thúc phiên và thêm bước resolve; state machine deposit không đổi.
