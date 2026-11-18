# TÀI LIỆU MÔ TẢ 2 TÍNH NĂNG MỚI CHO FRONTEND: YÊU THÍCH (WATCHLIST) & BÁO CÁO PHIÊN (REPORT)

> **Đối tượng đọc:** Frontend Team & AI Coding Agent bên FE  
> **Service:** `AuctionService` (port `8005`)

---

## 1. Tính năng Yêu thích / Theo dõi phiên đấu giá (Watchlist)

### 1.1. Mục tiêu & Luồng hoạt động
- Người mua (`buyer` hoặc `seller`, trừ chính Seller của phiên đó) có thể bấm **Yêu thích (Watch)** một phiên đấu giá để dễ tìm lại trong danh sách riêng.
- Người mua có **cờ `notifyOnNewBid` (`true`/`false`, mặc định `true`)** để tự bật/tắt việc nhận thông báo khi phiên yêu thích có lượt đặt giá mới.
- **Chống spam thông báo:** Khi có lượt bid mới hợp lệ, hệ thống tự động **loại trừ** người vừa đặt giá, người vừa bị vượt giá (vì đã nhận noti Outbid riêng) và Seller của phiên — chỉ bắn noti `auction-watchlist-bids` cho những người theo dõi thực sự quan tâm và đang bật `notifyOnNewBid = true`.

### 1.2. Danh sách API Watchlist

| Chức năng | Method & Endpoint | Request / Query | Response chính |
| :--- | :--- | :--- | :--- |
| **Yêu thích / Bật tắt noti** | `POST /api/v1/auctions/{id}/watch` | Body (tùy chọn): `{ "notifyOnNewBid": true }` | `{ auctionId, isWatching: true, notifyOnNewBid, updatedAt }` |
| **Bỏ yêu thích** | `DELETE /api/v1/auctions/{id}/watch` | — | `{ auctionId, isWatching: false, notifyOnNewBid: false, updatedAt }` |
| **Danh sách yêu thích của tôi** | `GET /api/v1/me/watchlist` | Query: `page`, `pageSize` | `PagedData<MyWatchlistItemDto>` (kèm `notifyOnNewBid`, `currentPriceAmount`, `primaryImageUrl`, `status`, `scheduledEndAt`) |

### 1.3. Gợi ý UI cho FE
- Trên card phiên đấu giá & trang chi tiết phiên: Thêm nút **Tim / Theo dõi** (gọi `POST .../watch` hoặc `DELETE .../watch`).
- Trong danh sách `/me/watchlist` (hoặc ngay cạnh nút Theo dõi): Thêm **Switch / Toggle "Nhận thông báo khi có giá mới"** gọi lại `POST /api/v1/auctions/{id}/watch` với `{ "notifyOnNewBid": false/true }`.

---

## 2. Tính năng Báo cáo phiên đấu giá bất thường (Report Auction)

### 2.1. Mục tiêu & Luồng hoạt động
- Mặc dù sản phẩm đã được duyệt trước khi mở phiên, trong lúc phiên chạy vẫn có thể phát sinh **thổi giá ảo (shill bidding), hàng giả duyệt sót, cài cắm liên hệ ngoài sàn...**.
- Người mua (`buyer` hoặc `seller`, trừ Seller của phiên) có thể gửi **1 báo cáo** cho phiên đang `Scheduled` hoặc `Running` (bắt buộc chọn lý do + mô tả tối thiểu 10 ký tự).
- **Xử lý thủ công bởi Admin/Staff (KHÔNG tự động hủy phiên):**
  - **Nếu Admin/Staff thấy ĐÚNG có vi phạm:** Admin/Staff bấm **`force-cancel` thủ công** kèm `reason` $\rightarrow$ Phiên bị hủy, hoàn 100% cọc cho mọi bidder, các báo cáo `Pending` của phiên tự động chuyển sang **`ActionTaken`** và hệ thống gửi thông báo **cảm ơn** tới tất cả những người đã tố cáo.
  - **Nếu Admin/Staff thấy KHÔNG có sai phạm:** Admin/Staff bấm **Bỏ qua (`dismiss`)** kèm ghi chú $\rightarrow$ Phiên tiếp tục chạy bình thường, các báo cáo `Pending` chuyển sang **`Dismissed`** và hệ thống gửi thông báo phản hồi (*chưa phát hiện sai phạm*) tới những người đã tố cáo.

### 2.2. Enum cần dùng trên FE
- **`AuctionReportReason`:**
  - `SuspectedShillBidding` *(Nghi vấn thổi giá ảo / quân xanh)*
  - `CounterfeitOrFake` *(Nghi vấn hàng giả / nhái)*
  - `MisleadingInfo` *(Thông tin / mô tả sai sự thật)*
  - `ExternalContact` *(Cài cắm liên hệ giao dịch ngoài sàn)*
  - `Other` *(Khác)*
- **`AuctionReportStatus`:** `Pending`, `ActionTaken`, `Dismissed`.

### 2.3. Danh sách API Report

| Vai trò | Method & Endpoint | Request / Query | Response chính |
| :--- | :--- | :--- | :--- |
| **Buyer** | `POST /api/v1/auctions/{id}/reports` | `{ "reason": "SuspectedShillBidding", "description": "..." }` *(>= 10 ký tự)* | `201 Created`: `{ reportId, auctionId, reason, status: "Pending", createdAt }` |
| **Buyer** | `GET /api/v1/me/reports` | Query: `page`, `pageSize` | `PagedData<MyAuctionReportDto>` (kèm `status` và `adminNote` phản hồi từ Admin) |
| **Admin / Staff** | `GET /api/v1/admin/auction-reports` | Query: `page`, `pageSize`, `status` (`Pending`/`ActionTaken`/`Dismissed`), `auctionId` | `PagedData<AdminAuctionReportDto>` |
| **Admin / Staff** | `POST /api/v1/admin/auctions/{id}/reports/dismiss` | Body (tùy chọn): `{ "note": "..." }` | `{ auctionId, dismissedCount, note, reviewedAt }` |
| **Admin / Staff** | `POST /api/v1/admin/auctions/{id}/force-cancel` | `{ "reason": "..." }` | *(API có sẵn — khi gọi thủ công sẽ tự chuyển report `Pending` sang `ActionTaken` & gửi noti cảm ơn)* |
