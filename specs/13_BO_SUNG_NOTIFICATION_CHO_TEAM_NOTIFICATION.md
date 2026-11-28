# Bổ sung Thông báo Đấu giá (Gửi team NotificationService)

## 1. Lưu ý quan trọng: Sửa lỗi `HTTP 401 Unauthorized` trên Render
- Hiện tại trong bảng `NotificationOutbox` của `AuctionService` trên Neon DB, toàn bộ các thông báo từ chiều tối 27/11 đến nay (bao gồm `auction-watchlist-bids`, `auction-report-resolved`, `auction-seller-results`, `auction-reserve-approval-needed`, `auction-cancelled`, `auction-order-payments`, `auction-winner-offers`, `in-app`) **đều đã được `AuctionService` tạo đầy đủ** nhưng khi bắn sang `NotificationService` đều bị trả về **`HTTP 401 Unauthorized`**.
- Nhờ team `NotificationService` kiểm tra lại `SigningKey` / cấu hình JWT Service Auth trên Render để đồng bộ với `AuctionService` (`AUCTION_NOTIFICATION_SIGNING_KEY`). Khi khắc phục xong, toàn bộ các thông báo trên sẽ hoạt động bình thường.

## 2. 3 thông báo mới vừa bổ sung từ `AuctionService`
Cả 3 thông báo mới đều gọi qua endpoint có sẵn **`POST /api/v1/notifications/in-app`** (giống như thông báo tạo phiên chờ duyệt và duyệt phiên):

| STT | Sự kiện | Người nhận | `notificationType` | Nội dung chính |
| :-: | :--- | :--- | :--- | :--- |
| 1 | Phiên kết thúc dưới giá sàn | Buyer Hạng 1 | `AuctionWon` | Báo giá của bạn đang thấp hơn giá sàn và đang chờ người bán xác nhận trong 12 giờ. |
| 2 | Seller từ chối bán dưới giá sàn | Buyer Hạng 1 | `AuctionDeposit` | Báo rõ lý do phiên thất bại là người bán từ chối bán dưới giá sàn và tiền cọc đã hoàn lại. |
| 3 | Phiên kết thúc có người thắng | Các Buyer không thắng | `AuctionDeposit` | Thông báo chia buồn phiên đã kết thúc, bạn chưa thắng và tiền cọc sẽ được hoàn lại. |
