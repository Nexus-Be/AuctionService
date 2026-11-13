# 01 · GET /api/v1/auctions/{id}/deposit-requirement

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Buyer) |
| Auth | JWT, role Buyer |
| Gọi ra ngoài | User `19` (hồ sơ Reputation/Streak) |

## 1. Giải quyết bài toán gì
Trước khi Buyer đóng cọc, cho biết **số tiền cọc, tỷ lệ, chính sách tịch thu** và tình trạng cọc hiện có. FE dùng để dựng màn xác nhận (spec 3.2 bước 2–4). Số cọc do BE tính, FE không tự tính.

## 2. Request
- Path: `id` (GUID phiên).
- Không có body.

## 3. Tính cọc
```
rate   = tierRate(reputation | newUser) + streakSurcharge(streak)   // từ policy (API 11)
amount = max( ceil(StartPrice × rate / 100), minDepositAmount )
amount = min(amount, StartPrice)                                      // không vượt giá khởi điểm
```
Số cọc **cố định theo `policyVersion` tại thời điểm tạo cọc** (API 02), đổi policy sau đó không ảnh hưởng cọc đã tạo.

## 4. Response 200 (happy case)
```json
{
  "auctionId": "…",
  "currency": "VND",
  "startPrice": 1000000,
  "depositAmount": 50000,
  "ratePercent": 5,
  "minDepositApplied": false,
  "policyVersion": 3,
  "buyerStanding": { "reputationScore": 90, "isNewUser": false, "violationStreak": 0 },
  "forfeiturePolicy": { "sellerPercent": 70, "platformPercent": 30 },
  "holdValidUntil": "2026-11-20T10:00:00Z",
  "canJoin": true,
  "existingDeposit": null
}
```
Khi đã có cọc: `existingDeposit: { "depositId": "…", "status": "Authorized" }`.

## 5. Các case lỗi
| HTTP | code | Khi nào | Xử lý ở caller |
|---|---|---|---|
| 401 | `UNAUTHORIZED` | Thiếu/hết hạn JWT | Đăng nhập lại |
| 403 | `ROLE_NOT_ALLOWED` | Admin/Staff gọi (không được bid) | Ẩn nút |
| 403 | `SELLER_CANNOT_BID_OWN_AUCTION` | Seller của phiên | Ẩn nút |
| 403 | `BIDDER_RESTRICTED` | Buyer bị hạn chế/đình chỉ quyền đấu giá. `details.restrictedUntil`, `reason` | Hiển thị lý do + thời hạn |
| 403 | `ACCOUNT_BANNED` | Tài khoản bị ban | Thông báo |
| 404 | `AUCTION_NOT_FOUND` | Sai id | Về danh sách |
| 409 | `AUCTION_NOT_OPEN_FOR_DEPOSIT` | Phiên đã kết thúc/hủy/failed. Chỉ cho đóng cọc khi `SCHEDULED` hoặc `ACTIVE` | Ẩn khối đóng cọc |
| 503 | `USER_SERVICE_UNAVAILABLE` | Không lấy được hồ sơ User | **Fail-closed**: không trả số cọc đoán; FE cho thử lại |

## 6. Ghi chú
- GET không có hiệu ứng phụ, có thể cache ngắn ở FE (<30s).
- `holdValidUntil` là ước lượng: `endTime + paymentWindow + buffer`, dùng làm tham số hold cho API 14.
