# 03 · GET /api/v1/auctions/{id}/deposits/me

> ⚠️ **v0.4:** có điều chỉnh theo hướng C, xem `26_UPDATE_v0.4_option-C.md` mục 4 (bỏ kiểm `ExpiresAt`/`DEPOSIT_EXPIRED`/`refresh`).

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer) |
| Auth | JWT, role Buyer |
| Query | `refresh=true` (tuỳ chọn) |
| Gọi ra ngoài | Wallet `17` khi `refresh=true` |

## 1. Giải quyết bài toán gì
Cho Buyer và FE biết **cọc của mình trong phiên đang ở trạng thái nào** và có bid được không. FE chỉ mở nút bid khi `canBid = true`. Dùng sau khi Buyer quay lại từ Gateway (không coi việc quay lại là thành công).

## 2. Request
Không có body. `refresh=true`: nếu cọc đang `Pending` hoặc sắp hết hạn, BE hỏi lại Wallet (API 17) rồi cập nhật trạng thái trước khi trả. Giới hạn 1 lần/5 giây/Buyer.

## 3. Response 200 (happy case)
```json
{
  "depositId": "…",
  "status": "Authorized",
  "amount": 50000,
  "ratePercent": 5,
  "authorizedAt": "2026-11-12T10:03:00Z",
  "expiresAt": "2026-11-21T10:00:00Z",
  "releasedAt": null,
  "capturedAt": null,
  "capturePurpose": null,
  "canBid": true,
  "blockReason": null
}
```
Chưa tạo cọc: `{ "status": "NotCreated", "canBid": false, "blockReason": "DEPOSIT_REQUIRED" }` (trả 200, không phải 404, vì đây là trạng thái bình thường).

| `status` | `canBid` | `blockReason` |
|---|---|---|
| `NotCreated` | false | `DEPOSIT_REQUIRED` |
| `Pending` | false | `DEPOSIT_PENDING` |
| `Authorized` còn hạn | true | null |
| `Authorized` hết hạn | false | `DEPOSIT_EXPIRED` |
| `Failed` | false | `DEPOSIT_FAILED` |
| `Expired` | false | `DEPOSIT_EXPIRED` |
| `Released`/`Captured` | false | `DEPOSIT_CLOSED` |
Ngoài ra `canBid=false`, `blockReason=BIDDER_RESTRICTED` nếu Buyer đang bị hạn chế.

## 4. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `ROLE_NOT_ALLOWED` | Không phải Buyer |
| 404 | `AUCTION_NOT_FOUND` | |
| 429 | `REFRESH_TOO_FREQUENT` | `refresh=true` gọi quá dày |
| 503 | `PAYMENT_SERVICE_UNAVAILABLE` | Chỉ khi `refresh=true`; nếu không refresh được vẫn trả dữ liệu đã lưu kèm `"stale": true` |

## 5. Ghi chú
Chỉ trả cọc của **chính người gọi**; không nhận `bidderId` từ client.
