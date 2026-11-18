# VIỆC CẦN CÁC SERVICE / GATEWAY / FRONTEND PHỐI HỢP ĐỂ HOÀN THIỆN WATCHLIST & REPORT

> **Mục đích file này:** Liệt kê chính xác những phần việc nằm ngoài `AuctionService` mà **API Gateway**, **NotificationService** và **Frontend** cần bổ sung để 2 tính năng **Yêu thích (Watchlist)** và **Báo cáo phiên (Report)** chạy thông suốt end-to-end.

---

## 1. Việc cần làm ở API Gateway (`localhost:8080`)

### 1.1. Thêm Route Public mới trỏ về `auction-cluster`
Trong `Gateway/appsettings.json` & `appsettings.Development.json`, cần bổ sung 2 route mới:
*(Lưu ý: `/api/v1/auctions/{id}/watch`, `/api/v1/auctions/{id}/reports`, `/api/v1/admin/auction-reports` hoặc `/api/v1/admin/auctions/...` nếu chưa có wildcard thì cần kiểm tra và thêm tương ứng).*

```json
"auction-me-watchlist-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/api/v1/me/watchlist/{**catch-all}"
  }
},
"auction-me-reports-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/api/v1/me/reports/{**catch-all}"
  }
},
"auction-admin-reports-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/api/v1/admin/auction-reports/{**catch-all}"
  }
}
```
*(Đảm bảo `AdminAuthorizationGuardMiddleware` cho phép role `admin` và `staff` đi qua `/api/v1/admin/auction-reports`).*

### 1.2. Mở Route & Policy cho 2 loại Notification mới (trỏ về `notification-cluster`)
`NotificationDispatcher` của `AuctionService` gọi qua Gateway kèm Bearer token (scope `auction.notifications.write`, policy `AuctionNotificationWrite`) tới 2 đường dẫn mới:
- `POST /api/v1/notifications/auction-watchlist-bids`
- `POST /api/v1/notifications/auction-report-resolved`

---

## 2. Việc cần làm ở `NotificationService`

`NotificationService` cần bổ sung 2 endpoint nhận sự kiện từ `AuctionService` (xác thực qua Bearer JWT `AuctionNotificationWrite` và xử lý idempotent theo `sourceEventId`):

### 2.1. `POST /api/v1/notifications/auction-watchlist-bids`
* **Ý nghĩa:** Gửi thông báo (In-App / Push) cho các user đang theo dõi phiên đấu giá khi có giá bid mới.
* **Payload `AuctionService` gửi sang:**
```json
{
  "sourceEventId": "guid",
  "auctionId": "guid",
  "triggerBidId": "guid",
  "auctionTitle": "string",
  "newAmount": 5500000,
  "currency": "VND",
  "watcherUserIds": ["guid-1", "guid-2"],
  "occurredAt": "2026-11-18T10:00:00Z",
  "auctionEndsAt": "2026-11-18T12:00:00Z",
  "traceId": "string?"
}
```

### 2.2. `POST /api/v1/notifications/auction-report-resolved`
* **Ý nghĩa:** Gửi thông báo kết quả xử lý tố cáo tới danh sách người đã báo cáo (`reporterUserIds`).
* **Payload `AuctionService` gửi sang:**
```json
{
  "sourceEventId": "guid",
  "auctionId": "guid",
  "auctionTitle": "string",
  "outcome": "ActionTaken | Dismissed",
  "message": "string",
  "adminReason": "string?",
  "reporterUserIds": ["guid-1", "guid-2"],
  "resolvedAt": "2026-11-18T10:30:00Z",
  "traceId": "string?"
}
```
> *Lưu ý:* Nếu `NotificationService` chưa kịp làm 2 endpoint này, khi bật `Notification:Enabled = true`, các dòng outbox của 2 loại này sẽ nhận 404 từ Gateway/Notification và chuyển sang `NeedsAttention` (không làm lỗi thao tác đặt giá hay báo cáo trên `AuctionService`).

---

## 3. Việc cần làm ở Frontend (FE)

1. **Màn hình Danh sách / Chi tiết phiên:**
   - Thêm nút **Yêu thích** (`POST/DELETE /api/v1/auctions/{id}/watch`) kèm toggle bật/tắt nhận thông báo giá mới (`notifyOnNewBid`).
   - Thêm nút **Báo cáo phiên** (`POST /api/v1/auctions/{id}/reports`) mở popup chọn `Reason` + nhập `Description` (>= 10 ký tự). Ẩn nút này đối với Seller của chính phiên đó hoặc khi phiên đã kết thúc.
2. **Màn hình Cá nhân Buyer:**
   - Tab **Phiên đang theo dõi** (`GET /api/v1/me/watchlist`) có nút switch bật/tắt `notifyOnNewBid`.
   - Tab **Báo cáo của tôi** (`GET /api/v1/me/reports`) hiển thị trạng thái (`Pending` / `ActionTaken` / `Dismissed`) và nội dung phản hồi `adminNote` từ quản trị viên.
3. **Màn hình Quản trị Admin / Staff:**
   - Trang **Quản lý báo cáo phiên** (`GET /api/v1/admin/auction-reports` lọc theo `Pending`):
     - Nếu kiểm tra thấy **có vi phạm** $\rightarrow$ Admin bấm nút **Hủy phiên cưỡng chế** (`POST /api/v1/admin/auctions/{id}/force-cancel` kèm `reason`).
     - Nếu kiểm tra thấy **không vi phạm** $\rightarrow$ Admin bấm nút **Bỏ qua báo cáo** (`POST /api/v1/admin/auctions/{id}/reports/dismiss` kèm `note` tùy chọn).
