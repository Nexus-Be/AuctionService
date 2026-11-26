# Checklist Demo & Kiểm thử Toàn Luồng Hôm Nay (FE + Liên Service)

> **Cập nhật:** 2026-11-26  
> **Mục tiêu:** Kiểm tra toàn bộ các luồng **chưa test ở buổi demo trước** (Gia hạn & Kết thúc phiên, Buy Now, Giá sàn 12h, Second-Chance, Khiếu nại/Report, Chia tiền & Nhận kết quả từ `Wallet`, `Commerce`, `User`, `Notification`).

---

## 0. Kiểm tra trước khi Demo (Pre-flight Check)

1. **Trạng thái kết nối trên Render (`AuctionService`):**
   - Đã nối: `User`, `Wallet`, `Commerce`, `Notification`, `AdminTimeControls__Enabled=true`.
   - **Lưu ý:** Kiểm tra xem đã điền **`Catalog__BaseUrl`** chưa (nếu chưa có Catalog thì dùng các phiên đã tạo sẵn trong DB, hoặc điền `Catalog__BaseUrl` trước khi tạo phiên mới).
   - Gọi `GET https://auctionservice-k6zv.onrender.com/health` → phải trả `200 OK` (`{"status":"Healthy"}`).
2. **Mẹo test nhanh không cần ngồi đợi giờ thật:**
   - Dùng tài khoản **role `admin`** *(lưu ý: chỉ `admin`, không dùng `staff`)* gọi 2 API tua thời gian:
     - Tua phiên đấu giá: `PATCH /api/v1/admin/auctions/{id}/schedule`
     - Tua hạn 12h / 24h sau phiên: `PATCH /api/v1/admin/auctions/{id}/settlement-deadlines`

---

## 1. Kịch bản 1: Anti-Sniping (Gia hạn) & Kết thúc phiên

| Bước | Thao tác trên FE / API | Kết quả cần kiểm chứng |
| :---: | :--- | :--- |
| 1 | Admin bấm **"Còn 4 phút"** (`PATCH .../schedule` với `{"remainingSeconds": 240}`) trên phiên đang `Running`. | Đồng hồ đếm ngược trên FE nhảy về ~4 phút. |
| 2 | Buyer B đặt giá cao hơn Buyer A. | - SignalR cập nhật giá mới ngay lập tức.<br>- Thời gian kết thúc tự **cộng thêm 5 phút** (`AntiSnipingExtensionMinutes`), `ExtensionCount` tăng lên `1`.<br>- Buyer A nhận thông báo bị vượt giá (`auction-outbids`). |
| 3 | Admin bấm **"Kết thúc ngay"** (`PATCH .../schedule` với `{"remainingSeconds": 0}`). | - Sau ~5–10s worker tự chốt phiên sang `Ended`.<br>- Người từ **Hạng 3 trở đi** được tự động hoàn cọc (`Released` + noti `auction-deposits`).<br>- Hạng 1 nhận thông báo thắng phiên (`auction-winners`), `CommerceService` nhận settlement tạo đơn hàng chờ thanh toán 24h. |

---

## 2. Kịch bản 2: Buy Now (Mua ngay) & Tách tiền cọc / tiền hàng

### 2A. Buy Now → Thanh toán thành công
1. Trên phiên `Running` có cài `BuyNowPriceAmount` (khi thỏa điều kiện hiện nút Buy Now), Buyer bấm **Mua ngay** (`POST /api/v1/auctions/{id}/buy-now`).
2. **Kiểm tra:**
   - Ví Buyer bị giữ tách 2 khoản: **Tiền cọc** + **Tiền hàng còn lại** (`BuyNowPrice - Deposit`).
   - Phiên đóng ngay (`Ended`), toàn bộ cọc của các bidder khác được hoàn trả.
   - Đơn hàng xuất hiện bên `CommerceService` với số tiền cần thanh toán phần ship/hoàn tất đơn.
3. Buyer hoàn tất thanh toán đơn → Seller xác nhận đơn (`SellerAccepted`) → phiên chuyển **`Settled`**, tiền hàng + cọc được giải ngân về ví Seller (sau khi trừ phí nền tảng).

### 2B. Buy Now → Quá 24h không thanh toán
1. Buyer bấm Buy Now xong nhưng **không** thanh toán đơn.
2. Admin tua hết hạn 24h thanh toán (`PATCH .../settlement-deadlines` với `{"paymentRemainingSeconds": 0}`).
3. **Kiểm tra:**
   - Hệ thống **CHỈ tịch thu Tiền cọc** (chia **70% Seller / 30% Sàn**).
   - Phần **Tiền hàng còn lại** được **hoàn trả lại vào ví Buyer** (không thu hết tiền hàng).
   - Buy Now thất bại **không** kích hoạt Second-Chance; sản phẩm được mở khóa để Seller đăng lại.

---

## 3. Kịch bản 3: Giá sàn bí mật (Reserve Price) & Duyệt 12 giờ

1. **Kiểm tra bảo mật:**
   - Buyer xem chi tiết phiên: chỉ thấy có Giá sàn hay chưa đạt Giá sàn (`hasReservePrice`, `isReserveMet`), **không thấy số tiền `ReservePriceAmount`**.
   - Seller xem phiên của mình (`GET /api/v1/seller/auctions/{id}/pricing`): thấy đầy đủ `ReservePriceAmount`.
2. **Kết thúc dưới Giá sàn (`HighestBid < ReservePrice`):**
   - Admin tua phiên về `{"remainingSeconds": 0}`.
   - Phiên chuyển `Ended` với trạng thái chờ Seller duyệt (`AwaitingSellerApproval`, hạn **12 giờ**); Seller nhận thông báo nhắc duyệt (`auction-reserve-approval-needed`).
3. **Test nhánh Đồng ý (`Accept`):**
   - Seller bấm **Đồng ý bán** (`POST .../reserve-approval/accept`) → Tạo đơn chờ Buyer thanh toán trong 24h; chuỗi từ chối của Seller reset về `0`.
4. **Test nhánh Từ chối / Hết hạn 12h (`Decline` / Timeout):**
   - Tạo phiên khác kết thúc dưới giá sàn, rồi Seller bấm **Từ chối** (`POST .../reserve-approval/decline`) hoặc Admin tua hết hạn 12h (`{"sellerApprovalRemainingSeconds": 0}`).
   - **Kiểm tra:** Phiên chuyển `Failed`, hoàn 100% cọc cho người đặt giá cao nhất, `ConsecutiveDeclineCount` tăng lên.
   - Lặp đủ **2 lần** → Seller nhận thông báo cảnh báo; đủ **3 lần liên tiếp** → Seller bị **khóa quyền đặt Giá sàn 14 ngày** (kiểm tra `GET /api/v1/seller/reserve-status` và thử tạo phiên mới có `ReservePrice` phải bị chặn).

---

## 4. Kịch bản 4: Quá hạn thanh toán 24h & Cơ hội thứ 2 (Second-Chance Offer)

*Điều kiện:* Phiên đấu giá thường (đã đạt giá sàn), có ít nhất **2 người đặt giá hợp lệ** (Hạng 1 và Hạng 2).

1. Phiên kết thúc → Hạng 1 không thanh toán → Admin tua hết hạn 24h (`{"paymentRemainingSeconds": 0}`).
2. **Kiểm tra xử lý Hạng 1:**
   - Cọc Hạng 1 bị **tịch thu (`Forfeit`)**, chia **70% vào ví Seller / 30% Sàn**.
   - Hạng 1 bị cộng vi phạm / trừ điểm uy tín bên `UserService` (`auction-deposits` báo `Captured`).
3. **Kiểm tra lời mời Hạng 2 (`Second-Chance`):**
   - Hạng 2 nhận thông báo (`auction-winner-offers`) và thấy lời mời ở màn hình chi tiết phiên (`GET .../second-chance/me`) với **giá bằng đúng mức Hạng 2 đã đặt**.
   - **Nhánh 4A (Chấp nhận):** Hạng 2 bấm **Accept** (`POST .../winner-offers/{offerId}/accept`) → `CommerceService` tạo đơn mới hạn 24h → thanh toán thành công → `Settled`.
   - **Nhánh 4B (Từ chối / Hết hạn 24h):** Hạng 2 bấm **Decline** hoặc Admin tua `{"winnerOfferRemainingSeconds": 0}` → **Hoàn 100% cọc cho Hạng 2 (không phạt)**, phiên chuyển `Failed`, mở khóa sản phẩm.

---

## 5. Kịch bản 5: Tính tiền, Giải ngân & Nhận kết quả liên service

1. **Phí nền tảng & Giải ngân (`Commerce` + `Wallet`):**
   - Khi đơn hàng hoàn tất (`SellerAccepted` / `Completed` và giải ngân V2 sang `Wallet`):
     - Ví Seller nhận đúng số tiền: `Tổng tiền hàng - Phí nền tảng (PlatformFeePercent%)`.
     - Đối chiếu lịch sử biến động số dư bên `WalletService` và bảng phân bổ đơn hàng bên `CommerceService`.
   - Kiểm tra màn hình Seller đã hiển thị minh bạch **Phí nền tảng dự kiến** (lấy từ Commerce) và **Chính sách chia cọc 70/30** (lấy từ Auction).
2. **Thông báo hệ thống (In-App) & Email (`NotificationService`):**
   - Kiểm tra chuông thông báo trên Web và hộp thư Email cho đủ các sự kiện: `outbid`, `winner`, `order-paid`, `second-chance`, `reserve-approval-needed`, `reserve-restriction`, `deposit` (hold/release/capture).

---

## 6. Kịch bản 6: Khiếu nại, Report & Hủy khẩn cấp (`User` + `Catalog` + `Auction`)

1. **Khiếu nại sau khi nhận hàng (`P2-05` — `UserService` + `CommerceService`):**
   - Buyer mở khiếu nại trên đơn hàng đã giao/thanh toán (chọn lý do *Fraud / Counterfeit* kiểm tra bắt buộc đính kèm ảnh bằng chứng).
   - Admin/Staff vào duyệt khiếu nại:
     - Chấp nhận khiếu nại → kiểm tra hoàn tiền về ví Buyer (`WalletService`) và cập nhật trạng thái đơn.
     - Từ chối khiếu nại → giữ nguyên đơn, đóng khiếu nại.
2. **Report phiên đấu giá & Force-Cancel (`AuctionService` + `CatalogService`):**
   - Buyer gửi tố cáo một phiên đang chạy (`POST /api/v1/auctions/{id}/reports`).
   - Admin xem danh sách report (`GET /api/v1/admin/auction-reports`):
     - Nếu có vi phạm → Admin bấm **Force-Cancel** (`POST /api/v1/admin/auctions/{id}/force-cancel`) → phiên hủy ngay, **hoàn 100% cọc** cho toàn bộ bidder, người tố cáo nhận thông báo `auction-report-resolved`, Seller không bị trừ oan nếu không cố ý hoặc bị ban sản phẩm bên `Catalog` nếu vi phạm nặng.
     - Nếu không có vi phạm → Admin bấm **Dismiss** (`POST /api/v1/admin/auctions/{id}/reports/dismiss`).
