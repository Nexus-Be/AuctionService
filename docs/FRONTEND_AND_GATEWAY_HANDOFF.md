# HƯỚNG DẪN TÍCH HỢP & LỜI NHẮC DÀNH CHO FRONTEND / GATEWAY (`AuctionService`)

> **Mục đích file này:** Cung cấp thông tin cấu hình **API Gateway** và danh sách **API mới / thay đổi** của `AuctionService` (port `8005`) để đội Frontend, AI Coding Agent bên FE và người phụ trách Gateway đọc vào là tích hợp đúng ngay, tránh lỗi `404` hoặc lệch nghiệp vụ.

---

## 1. BẮT BUỘC CẤU HÌNH THÊM ROUTE TRÊN API GATEWAY (`localhost:8080`)

Frontend gọi mọi API qua **API Gateway**, không gọi trực tiếp port `8005`. Trước khi test các API mới và SignalR realtime, cần thêm **3 route** sau vào `Gateway/appsettings.json` (và `appsettings.Development.json`) trỏ về `auction-cluster`:

```json
"auction-me-deposits-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/api/v1/me/deposits/{**catch-all}"
  }
},
"auction-me-auctions-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/api/v1/me/auctions/{**catch-all}"
  }
},
"auction-hub-route": {
  "ClusterId": "auction-cluster",
  "Match": {
    "Path": "/hubs/auctions/{**catch-all}"
  }
}
```

* **Lưu ý bảo mật Gateway:** Nếu Gateway đang mở `auction-settlements-route` (`/api/v1/auction-settlements/{**catch-all}`), hãy **gỡ bỏ** vì endpoint settlement đã được chuyển sang `/api/v1/internal/auction-settlements` và bảo vệ bằng `X-Internal-Service-Key`.

---

## 2. DANH SÁCH API MỚI & THAY ĐỔI CHO FRONTEND

### 2.1. API cho Người mua (Buyer)
1. **Tìm kiếm, lọc & sắp xếp danh sách phiên (`GET /api/v1/auctions`):**
   - Hỗ trợ thêm các query params:
     - `keyword` (tìm theo tên sản phẩm)
     - `sellerId`, `productId`
     - `minPrice`, `maxPrice`
     - `endingWithinMinutes` (lọc các phiên `Running` sắp kết thúc trong N phút)
     - `sortBy`: `endingSoon` | `newest` | `priceAsc` | `priceDesc` | `mostBids` *(mặc định giữ nguyên `ScheduledStartAt` giảm dần)*
2. **Danh sách phiên tôi đã tham gia (`GET /api/v1/me/auctions`):**
   - Gom theo từng phiên (**1 dòng/phiên**, không lặp theo từng lượt bid như `GET /api/v1/me/bids`).
   - Query params: `page`, `pageSize`, `participationState` (`winning` | `outbid` | `won` | `lost`), `status`.
   - Trả kèm `myHighestBidAmount`, `isWinning`, `participationState`, `myDepositStatus`, `orderId`.
3. **Danh sách & lịch sử tiền cọc của tôi (`GET /api/v1/me/deposits`):**
   - Query params: `page`, `pageSize`, `status` (`Pending` | `Authorized` | `Released` | `Captured` | `Failed` | `Expired`).

### 2.2. Quy tắc Bước giá khi Seller tạo/sửa phiên (`POST/PUT /api/v1/auctions`)
- `bidStepAmount` bắt buộc phải là **số nguyên dương chia hết cho `1.000` VND** (ví dụ: `10.000`, `50.000`, `100.000`). Không chấp nhận số lẻ (như `1.001` hoặc `1.500,5`).
- Nếu Seller bỏ trống, hệ thống tự tính theo tỷ lệ mặc định và làm tròn theo bội số `1.000`.

### 2.3. API cho Admin & Staff (`/api/v1/admin/...`)
Tất cả các endpoint dưới đây đều cho phép cả role **`admin`** và **`staff`**:
1. `GET /api/v1/admin/auctions`: Danh sách quản lý toàn sàn, hỗ trợ lọc `status`, `sellerId`, `productId`, `hasAttentionDeposit`, `overdueSettlement`, `fromDate`, `toDate`, `page`, `pageSize`.
2. `GET /api/v1/admin/auctions/attention-required`: Danh sách các phiên đang có cọc lỗi (`NeedsAttention`), lỗi đồng bộ hoặc settlement quá hạn cần xử lý.
3. `POST /api/v1/admin/auctions/{auctionId}/deposits/reconcile`: Kích hoạt đối soát & thử lại các khoản cọc đang kẹt của phiên.
4. `POST /api/v1/admin/auctions/{id}/sync/retry`: Kích hoạt chạy lại đồng bộ Catalog / Commerce / User cho phiên bị kẹt.
5. `POST /api/v1/admin/auctions/{auctionId}/bids/{bidId}/invalidate`: Vô hiệu hóa một lượt bid gian lận khi phiên đang `Running` (body tùy chọn `{ "reason": "..." }`) và tự động tính lại giá dẫn đầu.
6. `POST /api/v1/admin/auctions/{id}/force-cancel` & `GET /api/v1/admin/auctions/{id}/saga`: Đã mở quyền cho cả `staff` (trước đây chỉ có `admin`).

---

## 3. CÁC QUY TẮC NGHIỆP VỤ CỐ ĐỊNH (KHÔNG TỰ Ý ĐỔI)

Khi tích hợp FE, lưu ý **3 điểm nghiệp vụ** dưới đây là chủ đích của hệ thống `AuctionService`:
1. **Không có API rút cọc:** Buyer đã đóng cọc tham gia phiên thì số dư bị khóa cho tới khi phiên kết thúc / thua / hủy phiên.
2. **Tiền cọc tính theo điểm uy tín (`5%–30%`):** Không dùng mức cọc cố định 10% cho mọi tài khoản; FE luôn gọi `GET /api/v1/auctions/{auctionId}/deposit-requirement` để lấy số tiền cọc chính xác do server tính trước khi hiển thị nút xác nhận đóng cọc.
3. **Second-Chance bỏ kèo vẫn bị tịch thu cọc:** Người về nhì nếu đồng ý mua lại nhưng quá 24h không thanh toán vẫn bị tịch thu cọc (chia 70% Seller / 30% Sàn) và ghi nhận vi phạm.
4. **Tính năng `Buy Now` & `Giá sàn`:** Đang được đặc tả tại [03_BUY_NOW_BUSINESS_ANALYSIS.md](./03_BUY_NOW_BUSINESS_ANALYSIS.md) và [04_RESERVE_PRICE_BUSINESS_ANALYSIS.md](./04_RESERVE_PRICE_BUSINESS_ANALYSIS.md) (sẽ triển khai sau khi chốt họp nhóm).
