# 09 · GET /api/v1/auctions/{id}/second-chance/me

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer) |
| Auth | JWT, role Buyer |

## 1. Giải quyết bài toán gì
Người về nhì xem **lời mời của mình**: giá, hạn trả lời, trạng thái, và đơn hàng nếu đã chấp nhận. FE dùng để dựng màn Chấp nhận/Từ chối (API 07/08) và theo dõi sau đó.

## 2. Request
Không có body.

## 3. Response 200 (happy case)
```json
{
  "offerId": "…",
  "status": "Offered",
  "offerPrice": 950000,
  "currency": "VND",
  "offeredAt": "2026-11-13T10:05:00Z",
  "respondBy": "2026-11-14T10:05:00Z",
  "orderId": null,
  "paymentDeadlineAt": null,
  "notice": "Bạn sẽ thanh toán toàn bộ 950.000 VND. Cọc trước đó đã được hoàn, không trừ vào số tiền này."
}
```
`status`: `Offered` · `Accepted` · `Declined` · `Expired` · `Paid` · `PaymentExpired`.

## 4. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `ROLE_NOT_ALLOWED` | Không phải Buyer |
| 404 | `NO_OFFER` | Người gọi không có lời mời trong phiên này (FE ẩn khối) |
| 404 | `AUCTION_NOT_FOUND` | |

## 5. Ghi chú
- Chỉ trả offer của người gọi, không tiết lộ giá của người khác.
- Thời hạn trả lời lời mời (`respondBy`) là cấu hình mới (đề xuất mặc định 24h), cần team chốt.
