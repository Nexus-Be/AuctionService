# 24 · Hủy phiên: Seller cancel & Admin force-cancel  (SỬA)

| | Seller cancel | Admin force-cancel |
|---|---|---|
| Path (đối chiếu code) | `POST /api/v1/seller/auctions/{id}/cancel` | `POST /api/v1/admin/auctions/{id}/cancel` |
| Auth | JWT Seller của phiên | JWT Admin |
| Điều kiện | `SCHEDULED`, hoặc `ACTIVE` **chưa có bid** | Mọi phiên chưa kết thúc; **bắt buộc dùng khi đã có bid** |
| `reason` | tuỳ chọn | **bắt buộc** |

Service: Auction · **SỬA** (thêm xử lý cọc). Gọi ra ngoài: Wallet `16`, Catalog `23` (fast path + job quét).

## 1. Giải quyết bài toán gì
Khi phiên bị hủy, **mọi cọc phải được Release**, Buyer không mất cọc. Không trừ uy tín Seller khi Seller hủy lúc chưa có bid hoặc Admin hủy.

## 2. Request
```json
{ "reason": "Sản phẩm vi phạm chính sách" }
```

## 3. Xử lý (thứ tự quan trọng)
1. Trong một transaction: phiên → `CANCELLED` (chặn bid mới và tạo cọc mới) + đặt `PendingAction=Release` cho mỗi deposit và `StockReleaseState=Pending` + ghi audit (phiên, thời điểm, người hủy, lý do).
2. Worker Release từng deposit (API 16, reason `AUCTION_CANCELLED` hoặc `ADMIN_FORCE_CANCEL`): `Authorized` → `Released`; `Pending` → huỷ hold (`Voided`), deposit `Released`.
3. Nhả kho (API 23).
4. Thông báo bidder và Seller.
5. Cọc `Authorized` đến muộn sau khi hủy được xử lý ở API 04 (tự Release).

Release lỗi **không rollback việc hủy**; job quét retry.

## 4. Response 200 (happy case)
```json
{
  "auctionId": "…",
  "status": "CANCELLED",
  "depositsToRelease": 5,
  "stockRelease": "QUEUED"
}
```

## 5. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `REASON_REQUIRED` | Admin thiếu lý do |
| 401/403 | `UNAUTHORIZED` / `FORBIDDEN` | Không đúng quyền, hoặc Seller không sở hữu phiên |
| 404 | `AUCTION_NOT_FOUND` | |
| 409 | `AUCTION_HAS_BIDS` | Seller hủy khi đã có bid hợp lệ (Admin dùng force-cancel) |
| 409 | `AUCTION_ALREADY_STARTED` | Seller hủy phiên đang chạy theo quy tắc không cho |
| 409 | `AUCTION_ALREADY_ENDED` / `AUCTION_ALREADY_CANCELLED` | Đã kết thúc/hủy (đã hủy → 200 idempotent) |

## 6. Ghi chú
Admin force-cancel khi phiên đã có người thắng và order đang chờ thanh toán chưa được quy định; cần chốt (hủy order, hoàn tiền, xử lý cọc Winner).
