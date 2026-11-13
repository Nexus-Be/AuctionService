# 10 · POST /api/v1/internal/auctions/{auctionId}/order-outcome

> ⚠️ **v0.4:** hoàn cọc khi Seller từ chối do Auction gọi Wallet W5, xem `26_UPDATE_v0.4_option-C.md` mục 4.6.

| | |
|---|---|
| Service | Auction · **MỚI** (nếu đã có callback hiện tại thì mở rộng) |
| Caller | Commerce |
| Auth | `X-Internal-Service-Key` **bắt buộc** |

## 1. Giải quyết bài toán gì
Hạn thanh toán 24h do Commerce giữ, và không có event bus, nên Commerce báo kết quả order đấu giá cho Auction bằng callback. Từ đó Auction quyết định: Capture cọc, ghi vi phạm, mời người về nhì, nhả kho, chốt `SETTLED`/`FAILED`.

## 2. Request
```json
{
  "eventId": "ord-evt-001",
  "orderId": "…",
  "orderSource": "WINNER",
  "outcome": "PAYMENT_EXPIRED",
  "buyerId": "…",
  "occurredAt": "2026-11-13T10:00:01Z",
  "paidAmount": null,
  "refundedAmount": null,
  "refundRef": null
}
```
`orderSource`: `WINNER` | `SECOND_CHANCE`.
`outcome`: `PAID` · `PAYMENT_EXPIRED` · `SELLER_REJECTED` · `SELLER_ACCEPT_EXPIRED` · `COMPLETED` · `CANCELLED_BY_ADMIN`.

## 3. Xử lý theo `outcome`
| orderSource | outcome | Auction làm |
|---|---|---|
| WINNER | `PAID` | Phiên `SETTLED`; thông báo Seller. Việc nền: Capture cọc `OrderPayment` (API 15, `orderId`) → deposit `Captured`; báo User **reset Streak** (API 25) |
| WINNER | `PAYMENT_EXPIRED` | Trong **một transaction**: có người về nhì hợp lệ → tạo offer, phiên `SECOND_CHANCE`; không có → `FAILED` + đặt `StockReleaseState=Pending`. Đồng thời đặt việc nền: Capture `Forfeit` (15), ghi vi phạm `WINNER_NON_PAYMENT` (20). Không nhả kho nếu đã có offer |
| SECOND_CHANCE | `PAID` | Offer `Paid`, phiên `SETTLED`; báo User **reset Streak** (API 25) |
| SECOND_CHANCE | `PAYMENT_EXPIRED` | Offer `PaymentExpired`; phiên `FAILED`. Việc nền: ghi vi phạm `SECOND_CHANCE_NON_PAYMENT` (20), nhả kho (23). Không phạt tiền |
| bất kỳ | `SELLER_REJECTED` / `SELLER_ACCEPT_EXPIRED` | Ghi nhận; phiên `FAILED`; nhả kho (23). Commerce hoàn phần Buyer đã trả cho order (luồng hoàn ví hiện có). Auction hoàn **phần cọc đã Capture** về ví Buyer bằng Wallet W5 (việc nền `PendingAction = Refund`). Trừ uy tín Seller do luồng hiện có |
| bất kỳ | `COMPLETED` | Chỉ ghi nhận, không đổi trạng thái |
| bất kỳ | `CANCELLED_BY_ADMIN` | Ghi nhận; xử lý như hủy order (kho/cọc theo quyết định Admin) |

**Cơ chế chống lệch (Overview 4.1):** chuyển trạng thái và đặt các cột việc-nền (`PendingAction`, `ViolationState`, `StreakResetState`, `StockReleaseState`) nằm trong cùng một transaction, **không gọi service ngoài trong transaction**. Job quét gọi từng việc nền độc lập bằng khoá idempotent (`capture:{depositId}`, `violation:{auctionId}:{type}`, `streak-reset:{auctionId}`, `stock-release:{auctionId}`). Lỗi giữa chừng (đã Capture mà chưa ghi Streak) chỉ cần job chạy lại bước còn `Pending`.

**Thay đổi so với spec:** lời mời người về nhì tạo **ngay** trong transaction, không chờ Capture/ghi Streak xong, vì offer không phụ thuộc kết quả hai việc đó.

## 4. Response
**200 OK** (đã ghi nhận kết quả với unique `(orderId, outcome)`, chuyển trạng thái đồng bộ, việc gọi ngoài chạy nền):
```json
{ "received": true, "applied": true, "auctionStatus": "SECOND_CHANCE" }
```
Callback trùng (`orderId + outcome` đã ghi) hoặc lỗi thời: 200 với `applied:false`.

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Body sai, outcome lạ |
| 401 | `INTERNAL_KEY_INVALID` | |
| 404 | `AUCTION_NOT_FOUND` | |
| 409 | `ORDER_MISMATCH` | `orderId` không phải order hiện hành của phiên (vd order Winner cũ báo muộn khi đã sang người về nhì) |
| 5xx | `INTERNAL_ERROR` | Commerce retry cùng `eventId` |

## 6. Ghi chú
- **Kho:** Commerce không tự nhả kho cho order đấu giá khi `PAYMENT_EXPIRED`; Auction quyết định (giữ cho người về nhì hoặc nhả).
- Thứ tự sự kiện không đảm bảo: dùng `occurredAt` + kiểm trạng thái hiện tại, bỏ qua sự kiện lỗi thời.
