# 06 · GET /api/v1/auctions/{id}/result  (SỬA)

| | |
|---|---|
| Service | Auction · **SỬA API hiện có** |
| Caller | FE (Buyer, Seller, Admin, Staff) |
| Auth | JWT; dữ liệu trả khác nhau theo vai trò |

## 1. Giải quyết bài toán gì
Một chỗ để FE biết **phiên đang ở giai đoạn nào sau khi kết thúc**: chờ Winner trả, mời người về nhì, đã settle hay failed, kèm thông tin cọc và đơn hàng.

## 2. Request
Không có body.

## 3. Response 200 (happy case, Winner đang chờ thanh toán)
```json
{
  "auctionId": "…",
  "status": "ENDED",
  "phase": "AWAITING_WINNER_PAYMENT",
  "winner": { "bidderId": "…", "displayName": null },
  "winningAmount": 1000000,
  "settlement": {
    "orderId": "…",
    "depositAmount": 50000,
    "remainingAmount": 950000,
    "paymentDeadlineAt": "2026-11-13T10:00:00Z"
  },
  "myDeposit": { "status": "Authorized", "amount": 50000 },
  "secondChance": null
}
```
`phase`: `NO_BIDS_FAILED` · `AWAITING_WINNER_PAYMENT` · `WINNER_PAID_AWAITING_SELLER` · `SECOND_CHANCE_OFFERED` · `AWAITING_RUNNER_UP_PAYMENT` · `SETTLED` · `FAILED` · `CANCELLED`.

Khi `phase = SECOND_CHANCE_OFFERED`:
```json
"secondChance": { "status": "Offered", "offerPrice": 950000, "respondBy": "2026-11-14T10:00:00Z" }
```
Chỉ Seller, Admin, Staff và chính người về nhì thấy `secondChance.bidderId`/`offerPrice`.

## 4. Quy tắc hiển thị theo vai trò
| Trường | Buyer (Winner) | Buyer khác | Seller | Admin/Staff |
|---|---|---|---|---|
| `winner.bidderId` | ✔ | ẩn | ✔ | ✔ |
| `settlement` | ✔ | ẩn | ✔ (không có `myDeposit`) | ✔ |
| `myDeposit` | cọc của mình | cọc của mình | ẩn | ẩn (dùng API 13) |
| `secondChance` | nếu là người về nhì | ẩn | ✔ | ✔ |

`winner.displayName` hiện chưa có (BE chưa trả trong settlement); để `null`.

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 404 | `AUCTION_NOT_FOUND` | |
| 409 | `AUCTION_NOT_ENDED` | Phiên chưa kết thúc (FE dùng API chi tiết phiên) |

## 6. Ghi chú
`settlement` lấy từ Commerce hoặc từ bản ghi settlement Auction đã lưu. Nếu Commerce chưa tạo order (settlement đang retry): `settlement.orderId = null`, `phase` vẫn đúng, kèm `settlementStatus: "PENDING"`.
