# 23 · POST /api/v1/internal/inventory/auction-holds/{auctionId}/release

| | |
|---|---|
| Service | **Catalog/Inventory** · **KIỂM TRA** (nhiều khả năng đã có API nhả kho cho phiên; đối chiếu path và contract thật) |
| Caller | Auction (fast path + job quét) |
| Auth | `X-Internal-Service-Key` bắt buộc |
| Headers | `Idempotency-Key` = `stock-release:{auctionId}` |

## 1. Giải quyết bài toán gì
Nhả kho 1 đơn vị đã giữ cho phiên. Flow 4 nhả kho khi Winner quá hạn, nhưng thiết kế mới **chỉ nhả sau khi người về nhì không còn khả năng mua** (từ chối/hết hạn/không trả) hoặc phiên `FAILED`/`CANCELLED`. Khi người về nhì mua, kho **chuyển cho order của họ**, không nhả.

## 2. Request
```json
{ "reason": "SECOND_CHANCE_DECLINED" }
```
`reason`: `AUCTION_CANCELLED` · `NO_BIDS` · `WINNER_NON_PAYMENT_NO_RUNNER_UP` · `SECOND_CHANCE_DECLINED` · `SECOND_CHANCE_EXPIRED` · `SECOND_CHANCE_NON_PAYMENT` · `SELLER_REJECTED`.

## 3. Response 200 (happy case)
```json
{ "auctionId": "…", "released": true, "quantity": 1, "releasedAt": "…" }
```
Đã nhả trước đó: 200 với `released:false, alreadyReleased:true`.

## 4. Các case lỗi
| HTTP | code | Khi nào | Auction xử lý |
|---|---|---|---|
| 401 | `INTERNAL_KEY_INVALID` | | |
| 404 | `STOCK_HOLD_NOT_FOUND` | Không có kho giữ cho phiên | Coi như đã nhả, ghi log |
| 409 | `STOCK_HOLD_CONSUMED` | Kho đã trừ thẳng cho order PAID | Không nhả; log (có thể do callback sai thứ tự) |
| 503 | `CATALOG_UNAVAILABLE` | | **Retry** (job quét) |

## 5. Ghi chú
Nếu hiện chưa có API nào để **chuyển kho từ phiên sang order người về nhì**, cần bổ sung hoặc dùng lại cơ chế "chuyển giữ cho order đấu giá" của Flow 4 cho order thứ hai.
