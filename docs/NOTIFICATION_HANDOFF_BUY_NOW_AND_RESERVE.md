# TÀI LIỆU BÀN GIAO TÍCH HỢP CHO `NotificationService`: BUY NOW & GIÁ SÀN

> **Từ:** `AuctionService` (port `8005`)  
> **Gửi tới:** `NotificationService` (và `Gateway`)  
> **Mục đích:** Xác nhận các luồng thông báo đang dùng lại cho **Buy Now & Giá sàn**, đồng thời liệt kê các thông báo mới (nếu team muốn bổ sung thêm để hoàn thiện trải nghiệm Seller/Buyer).

---

## 1. Các luồng thông báo ĐÃ CÓ SẴN — Dùng lại ngay không cần sửa

1. **Khi Buyer chốt đơn bằng Buy Now:**
   - `AuctionService` phát **`POST /api/v1/notifications/auction-winners`** (đã có sẵn) với `winningAmount = BuyNowPrice` và `paymentDeadline = +24h` (hạn thanh toán phí ship).
   - Khi Buyer thanh toán phí ship xong $\rightarrow$ phát **`POST /api/v1/notifications/auction-order-payments`** (đã có sẵn).
   - Khi các bidder khác được hoàn cọc / hoặc Buyer Buy Now bỏ kèo bị tịch thu cọc & hoàn tiền hàng $\rightarrow$ phát **`POST /api/v1/notifications/auction-deposits`** (đã tích hợp).
2. **Khi Seller đồng ý bán dưới giá sàn:**
   - `AuctionService` phát **`POST /api/v1/notifications/auction-winners`** (đã có sẵn) cho Hạng 1 với giá chốt mới và hạn thanh toán `24h`.
3. **Khi Seller từ chối bán dưới giá sàn (hoặc quá hạn 12h):**
   - `AuctionService` phát **`POST /api/v1/notifications/auction-failed`** (đã có sẵn) cho Seller với lý do `ReserveNotMet`.

---

## 2. Các thông báo MỚI nên bổ sung thêm (Tùy chọn nâng cao UX)

Hiện tại `AuctionService` đã lưu toàn bộ trạng thái (`AwaitingSellerApproval`, `SellerApprovalDeadlineAt`, `ConsecutiveDeclineCount`, `ReserveRestrictedUntil`) trong DB và trả qua API cho FE hiển thị. Nếu muốn bắn thêm **Push/Email/In-App Notification** chủ động, `NotificationService` (và Gateway) có thể mở thêm 2 endpoint dưới đây:

### 2.1. Nhắc Seller duyệt bán dưới giá sàn trong 12h (`POST /api/v1/notifications/auction-reserve-approval-needed`)
* **Thời điểm bắn:** Ngay khi phiên hết giờ mà `HighestBid < ReservePrice` (settlement chuyển sang `AwaitingSellerApproval`).
* **Người nhận:** `sellerUserId`.
* **Payload đề xuất:**
```json
{
  "sourceEventId": "guid",
  "auctionId": "guid",
  "sellerUserId": "guid",
  "auctionTitle": "string",
  "highestBidAmount": 14000000,
  "reservePriceAmount": 15000000,
  "currency": "VND",
  "approvalDeadlineAt": "2026-11-20T06:00:00Z"
}
```

### 2.2. Cảnh báo / Thông báo khóa quyền đặt Giá sàn (`POST /api/v1/notifications/auction-reserve-restriction`)
* **Thời điểm bắn:**
  * Lần 2 từ chối liên tiếp: Gửi cảnh báo nhắc nhẹ (*"Nếu từ chối thêm 1 lần nữa, quyền đặt Giá sàn sẽ bị khóa 14 ngày"*).
  * Lần 3 từ chối liên tiếp: Thông báo quyền đặt Giá sàn đã bị tạm khóa đến `restrictedUntil`.
* **Payload đề xuất:**
```json
{
  "sourceEventId": "guid",
  "sellerUserId": "guid",
  "consecutiveDeclineCount": 2,
  "isRestricted": false,
  "restrictedUntil": null,
  "occurredAt": "2026-11-19T15:30:00Z"
}
```
*(Ghi chú: Hiện tại `AuctionService` chưa đẩy 2 route mục 2 này vào `NotificationOutbox` để tránh lỗi `404 NeedsAttention` khi `NotificationService` chưa mở route. Khi nào `NotificationService` mở xong 2 route trên thì báo `AuctionService` nối vào trong 5 phút).*
