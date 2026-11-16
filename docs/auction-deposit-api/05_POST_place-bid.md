# 05 · POST /api/v1/auctions/{id}/bids  (SỬA)

> ⚠️ **v0.4:** có điều chỉnh theo hướng C, xem `26_UPDATE_v0.4_option-C.md` mục 4 (bỏ kiểm `ExpiresAt`/`DEPOSIT_EXPIRED`/`refresh`).

| | |
|---|---|
| Service | Auction · **SỬA API hiện có** (đối chiếu path thật) |
| Caller | FE (Buyer) |
| Auth | JWT, role Buyer |
| Headers | `If-Match: <rowVersion>`, `Idempotency-Key` (giữ nguyên contract hiện tại) |
| Gọi ra ngoài | User `19` (cache), Wallet `17` (khi cọc sắp hết hạn) |

## 1. Giải quyết bài toán gì
Giữ nghiệp vụ đặt giá hiện tại, **bổ sung điều kiện cọc** và đổi cách chặn: không chặn theo Reputation nữa, chỉ chặn khi **thiếu cọc hợp lệ** hoặc **bị đình chỉ/hạn chế quyền đấu giá**.

## 2. Request (không đổi)
```json
{ "amount": 1050000 }
```

## 3. Thứ tự kiểm tra (đề xuất)
1. Auth, role Buyer, không phải Seller của phiên.
2. Phiên đang `ACTIVE` và chưa hết giờ.
3. **[MỚI] Cọc:** có deposit của Buyer, `Authorized` và `ExpiresAt > now + safetyMargin`.
   - Nếu `ExpiresAt` đã qua hoặc sắp qua: gọi Wallet `17` một lần để xác minh. Còn hiệu lực → cập nhật `ExpiresAt`; không → đặt `Expired`, trả lỗi.
4. **[MỚI] Hạn chế:** hồ sơ User (cache 30–60s): không bị đình chỉ/hạn chế đấu giá.
5. `If-Match`, bước giá, giá tối thiểu (logic cũ).
6. Ghi bid, tăng `rowVersion`.

Bỏ kiểm tra "uy tín Buyer ≥ ngưỡng" của Flow 4. Seller vẫn giữ Reputation Gate khi tạo phiên.

## 4. Response 200/201 (happy case, không đổi)
```json
{ "bidId": "…", "amount": 1050000, "isLeading": true, "rowVersion": "…" }
```

## 5. Các case lỗi
| HTTP | code | Khi nào | FE xử lý |
|---|---|---|---|
| 403 | **`DEPOSIT_REQUIRED`** | Chưa có cọc, hoặc cọc `Pending`/`Failed`/`Released`/`Captured` | Chuyển sang đóng cọc |
| 403 | **`DEPOSIT_EXPIRED`** | Cọc hết hạn hoặc Gateway đã giải phóng | Đóng cọc lại |
| 403 | **`BIDDER_RESTRICTED`** | Bị hạn chế/đình chỉ. `details.restrictedUntil` | Hiển thị lý do |
| 403 | `SELLER_CANNOT_BID_OWN_AUCTION`, `ROLE_NOT_ALLOWED` | | Ẩn nút |
| 409 | `AUCTION_NOT_ACTIVE` | Chưa bắt đầu/đã kết thúc/hủy | Tải lại phiên |
| 412 | `ROWVERSION_MISMATCH` | Có bid khác đến trước | Tải lại giá, cho bid lại |
| 428 | `IF_MATCH_REQUIRED` | Thiếu `If-Match` | |
| 422 | `BID_BELOW_MINIMUM` | Thấp hơn giá + bước giá | Hiển thị mức tối thiểu |
| 400 | `IDEMPOTENCY_KEY_REQUIRED` | | |
| 503 | `USER_SERVICE_UNAVAILABLE` | Không có cache và User lỗi | Cho thử lại (fail-closed) |

## 6. Ghi chú
- Replay cùng `Idempotency-Key` trả đúng response cũ.
- Không yêu cầu đóng thêm cọc khi nâng giá.
- Không gọi User đồng bộ mỗi bid nếu có cache; chấp nhận trễ ngắn khi Buyer mới bị hạn chế.
