# 08 · POST /api/v1/auctions/{id}/second-chance/decline

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer, người về nhì) |
| Auth | JWT, role Buyer |
| Headers | `Idempotency-Key` |
| Gọi ra ngoài | Catalog `23` (fast path + job quét) |

## 1. Giải quyết bài toán gì
Người về nhì từ chối lời mời. Theo thiết kế: phiên **`FAILED` ngay**, **không** mời người tiếp theo, **không** tăng Streak hay trừ uy tín.

## 2. Request
Body rỗng, hoặc `{ "reason": "..." }` (tuỳ chọn, tối đa 500 ký tự).

## 3. Xử lý
1. Kiểm tra giống API 07 (đúng người, `Offered`, còn hạn).
2. Offer → `Declined`, phiên → `FAILED` (`failReason = SECOND_CHANCE_DECLINED`).
3. Cùng transaction đặt `StockReleaseState = Pending`; sau commit gọi nhả kho (API 23), job quét retry nếu lỗi. Thông báo Seller (có thể Relist).
4. Không gọi User `20`.

Hết hạn không trả lời được xử lý bởi **job quét offer quá hạn**: cùng hiệu ứng, offer `Expired`.

## 4. Response 200 (happy case)
```json
{ "offerId": "…", "status": "Declined", "auctionStatus": "FAILED" }
```

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `NOT_SECOND_CHANCE_BIDDER` | |
| 404 | `OFFER_NOT_FOUND` | |
| 409 | `OFFER_EXPIRED` | Đã hết hạn |
| 409 | `OFFER_ALREADY_RESOLVED` | Đã `Accepted`. Nếu đã `Declined` cùng người → 200 idempotent |
| 400 | `IDEMPOTENCY_KEY_REQUIRED` | |

## 6. Ghi chú
Nhả kho lỗi (Catalog down) không làm Decline thất bại: job quét retry, Admin có cảnh báo (`NeedsAttention`) nếu quá 10 lần.
