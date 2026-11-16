# 18 · POST /api/v1/auction-orders/settlement  (SỬA)

| | |
|---|---|
| Service | **Commerce** · SỬA API hiện có |
| Caller | Auction (fast path + job quét) |
| Auth | `X-Internal-Service-Key` **BẮT BUỘC** (vá lỗi P0: hiện chỉ kiểm khi client gửi header) |
| Headers | `Idempotency-Key` = `settle:{auctionId}:{source}:{buyerId}` |

## 1. Giải quyết bài toán gì
Auction báo Commerce người thắng để **tạo order đấu giá**. Thay đổi: có `depositAmount`, công thức tổng tiền mới, và dùng thêm cho **người về nhì** (`source = SECOND_CHANCE`).
Route này **không** được lộ ra Gateway công khai (route công khai chỉ cho `/auction-orders/{guid}/...`).

## 2. Request
```json
{
  "auctionId": "…",
  "source": "WINNER",
  "sellerId": "…",
  "buyerId": "…",
  "skuId": "…",
  "winningAmount": 1000000,
  "depositAmount": 50000,
  "endedAt": "2026-11-12T10:00:00Z"
}
```
- `source = SECOND_CHANCE`: `winningAmount` = giá của người về nhì, `depositAmount = 0`.
- Hạn thanh toán (mặc định 24h) do **Commerce** tính từ lúc tạo order, không do Auction gửi.

## 3. Công thức
```
RemainingAmount = WinningAmount − DepositAmount
TotalAmount     = RemainingAmount + ShippingFee      // phí ship tính sau khi Buyer nhập địa chỉ
```
Tổng giá trị đơn vẫn là `WinningAmount` (cọc nằm trong giá thắng, không tính hai lần). Seller nhận `WinningAmount` trừ chiết khấu khi order `COMPLETED`.

## 4. Response
**201 Created** (happy case):
```json
{
  "auctionOrderId": "…",
  "status": "PendingPayment",
  "winningAmount": 1000000,
  "depositAmount": 50000,
  "remainingAmount": 950000,
  "shippingFee": null,
  "totalAmount": null,
  "paymentDeadlineAt": "2026-11-13T10:00:00Z",
  "created": true
}
```
**200 OK** với `created:false`: đã tạo trước đó (cùng `auctionId + source + buyerId`).

## 5. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường | Lỗi lập trình |
| 401 | `INTERNAL_KEY_INVALID` | Thiếu/sai key | Dừng, cảnh báo |
| 409 | `SETTLEMENT_CONFLICT` | Đã có settlement cùng khoá nhưng số tiền/Buyer khác | Dừng, cảnh báo Admin |
| 409 | `STOCK_HOLD_MISSING` | Không thấy kho giữ cho phiên | Cảnh báo, kiểm Catalog |
| 422 | `DEPOSIT_EXCEEDS_WINNING_AMOUNT` | `depositAmount > winningAmount` | Lỗi dữ liệu, cảnh báo |
| 422 | `INVALID_AMOUNT` | Số tiền ≤ 0 | |
| 503 | `COMMERCE_UNAVAILABLE` | | **Retry** cùng key |

## 6. Trách nhiệm đi kèm của Commerce
1. Giữ hạn thanh toán 24h; hết hạn → hủy order và gọi **callback API 10** (`PAYMENT_EXPIRED`), **không tự nhả kho** cho order đấu giá.
2. Khi order PAID/Seller từ chối/quá hạn chấp nhận → callback API 10.
3. Khi Seller từ chối hoặc quá hạn: hoàn số Buyer đã trả cho order bằng luồng hoàn ví hiện có; **phần cọc đã Capture do Auction hoàn qua Wallet W5**, không gộp vào luồng này.
4. Response/GET order trả thêm `depositAmount`, `remainingAmount`.
