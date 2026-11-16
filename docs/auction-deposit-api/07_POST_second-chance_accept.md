# 07 · POST /api/v1/auctions/{id}/second-chance/accept

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer, người về nhì) |
| Auth | JWT, role Buyer |
| Headers | `Idempotency-Key` (bắt buộc) |
| Gọi ra ngoài | Commerce `18` (fast path + job quét, xem Overview 4.1) |

## 1. Giải quyết bài toán gì
Người về nhì **chấp nhận mua theo giá bid của chính mình** sau khi Winner không thanh toán. Chỉ có **một** lời mời; không mời người thứ 3, thứ 4.

## 2. Request
```json
{ "acceptedPrice": 950000 }
```
`acceptedPrice` bắt buộc, để Buyer xác nhận đúng giá.

## 3. Xử lý
1. Khoá bản ghi offer. Kiểm tra: đúng người được mời, `Offered`, chưa quá `ExpiresAt`, phiên đang `SECOND_CHANCE`.
2. `acceptedPrice == offerPrice`.
3. Đặt offer `Accepted`, phiên → chờ thanh toán.
4. Cùng transaction đặt `Offer.SettlementState = Pending`; sau commit gọi ngay Commerce `18` (`source = SECOND_CHANCE`, `depositAmount = 0`, `settlementKey = settle:{auctionId}:SECOND_CHANCE:{bidderId}`). Gọi lỗi thì job quét gọi lại, thành công → `Done`.
5. Commerce tạo order, hạn thanh toán 24h như Winner.

Người về nhì **trả toàn bộ giá**; cọc cũ đã Release ở lúc phiên kết thúc nên không trừ.

## 4. Response 200 (happy case)
```json
{
  "offerId": "…",
  "status": "Accepted",
  "settlementStatus": "CREATED",
  "orderId": "…",
  "amountToPay": 950000,
  "paymentDeadlineAt": "2026-11-15T10:00:00Z"
}
```
Nếu Commerce chưa phản hồi kịp: `settlementStatus: "PENDING"`, `orderId: null`. FE hỏi lại API 06/09.

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `IDEMPOTENCY_KEY_REQUIRED` / `VALIDATION_ERROR` | |
| 401 | `UNAUTHORIZED` | |
| 403 | `NOT_SECOND_CHANCE_BIDDER` | Không phải người được mời |
| 404 | `OFFER_NOT_FOUND` | Phiên không có lời mời |
| 409 | `OFFER_EXPIRED` | Quá hạn trả lời (offer đã `Expired`, phiên `FAILED`) |
| 409 | `OFFER_ALREADY_RESOLVED` | Đã chấp nhận/từ chối. Nếu đã `Accepted` cùng người → trả 200 idempotent |
| 422 | `PRICE_MISMATCH` | `acceptedPrice` khác giá mời |
| 403 | `BIDDER_RESTRICTED` | *(tuỳ chốt)* nếu bị đình chỉ sau khi bid |

## 6. Idempotency & đồng thời
Accept và Decline/hết hạn có thể đến cùng lúc: dùng khoá hàng + kiểm tra trạng thái, bên đến sau nhận 409. Settlement gọi Commerce idempotent theo `settlementKey`.
