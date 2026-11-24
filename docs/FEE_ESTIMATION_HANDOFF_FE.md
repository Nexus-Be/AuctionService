# Tài liệu bàn giao Frontend — Mục 36 (Phí nền tảng) & Toàn bộ Chính sách Đấu giá (Auction Policies)

> **Từ:** AuctionService  
> **Gửi:** Team Frontend & CommerceService  
> **Mục đích:**  
> 1. Chốt phương án xử lý **Phí nền tảng dự kiến (Mục 36)**.  
> 2. Tổng hợp đầy đủ **Chính sách & Quy tắc nghiệp vụ của luồng Đấu giá** để FE xây dựng mục **"Chính sách đấu giá / Quy định tham gia"** và hiển thị điều kiện bật/tắt UI chính xác cho Seller & Buyer.

---

## Phần 1: Phản hồi Mục 36 — Phí nền tảng dự kiến cho Seller

### 1. Kết luận nhanh
`AuctionService` **không lưu trữ, không cấu hình và không tính toán phí nền tảng** (`PlatformFeePercent`). Toàn bộ cấu hình `% phí nền tảng`, snapshot phí theo từng đơn hàng và logic khấu trừ khi giải ngân đều nằm ở **`CommerceService`**.

Vì vậy, phần **hiển thị phí nền tảng dự kiến (Mục 36)** sẽ do **FE lấy % phí từ `CommerceService`** và tự tính ước lượng trên UI, **không thêm vào `SellerAuctionPricingDto` của `AuctionService`**.

### 2. Lý do kỹ thuật
- **Single Source of Truth:** `CommerceService` giữ cấu hình `PlatformFeePercent` và snapshot cứng tỷ lệ này vào đơn (`SettlementSnapshots`) tại thời điểm tạo đơn.
- **Tách biệt trách nhiệm:** `GET /api/v1/seller/auctions/{id}/pricing` của `AuctionService` chỉ trả về các mức giá thuộc domain đấu giá (`StartPriceAmount`, `ReservePriceAmount`, `BuyNowPriceAmount`).
- **Tránh coupling chéo:** Không để API xem giá phiên của `AuctionService` bị chậm/lỗi dây chuyền nếu `CommerceService` tạm gián đoạn.

### 3. Đề xuất hiển thị trên UI Seller
Trên màn hình **Tạo phiên** và **Chi tiết phiên của Seller**, FE nên hiển thị minh bạch 2 kịch bản tiền về ví Seller:
1. **Khi giao dịch thành công:** Lấy `platformFeePercent` từ `CommerceService` để hiện *"Phí nền tảng dự kiến"* (`Giá × platformFeePercent / 100`) và *"Tiền thực nhận dự kiến"*.
2. **Khi người thắng bùng thanh toán (`Forfeit`):** Lấy `sellerPercent` / `platformPercent` từ `GET /api/v1/auction-configs/deposit-policy` của `AuctionService` để hiện chính sách chia tiền cọc phạt (mặc định **70% cho Seller / 30% cho Sàn**).

---

## Phần 2: Tổng hợp Chính sách Đấu giá (để FE làm trang Chính sách & Tooltip UI)

FE có thể dùng các quy tắc dưới đây để làm trang/modal **"Chính sách & Quy định đấu giá"** và giải thích cho Seller/Buyer ngay trên giao diện:

### 1. Quy định tạo phiên & Bước giá (`AuctionRules`)
- **Thời gian mở phiên:** Thời điểm bắt đầu không được quá **30 ngày** kể từ hiện tại.
- **Thời lượng phiên:** Tối thiểu **60 phút**, tối đa **168 giờ (7 ngày)**.
- **Bước giá (`BidStep`):** Phải là bội số dương của **1.000 VND**. Nếu Seller không nhập, hệ thống tự tính mặc định **10%** giá khởi điểm (làm tròn nghìn).
- **Quan hệ giá:** Bắt buộc `Giá khởi điểm <= Giá sàn (nếu có) < Giá Mua ngay (nếu có)`.
- **Quyền sửa / hủy phiên:** Seller chỉ được sửa hoặc hủy phiên khi phiên **chưa kết thúc** và **chưa có lượt đặt giá nào** (`BidCount == 0`).

---

### 2. Chính sách Đặt cọc dựa trên Điểm uy tín (`DepositRules`)
Người mua **bắt buộc phải khóa tiền cọc trong Ví** trước khi đặt giá (trừ trường hợp bấm *Buy Now* hệ thống tự khóa cọc + tiền hàng cùng lúc).

- **Tỷ lệ cọc mặc định theo điểm uy tín (tính trên `StartPriceAmount`):**
  - Uy tín **80 – 100 điểm:** **5%**
  - Uy tín **60 – 79 điểm:** **10%**
  - Uy tín **40 – 59 điểm:** **15%**
  - Uy tín **0 – 39 điểm** hoặc **tài khoản mới chưa có điểm:** **20%**
- **Phụ thu vi phạm (`StreakSurcharge`):** Từ **2 lần vi phạm** bùng thanh toán trở lên, cộng thêm **+10%** vào tỷ lệ cọc.
- **Trần & Sàn tiền cọc:**
  - Tỷ lệ cọc tối đa không vượt quá **30%** (`MaxRatePercent`).
  - Tiền cọc tối thiểu là **50.000 VND** (`MinDepositAmount`), nhưng không vượt quá Giá khởi điểm.
- **Hoàn cọc tự động:** Người tham gia không thắng phiên (hoặc phiên bị hủy/thất bại) sẽ được hệ thống tự động giải tỏa tiền cọc về ví.

*(Lưu ý cho FE: Các mốc % trên là mặc định của `DepositPolicy`; Admin có thể cấu hình lại nên FE nên đọc động từ API `GET /api/v1/auction-configs/deposit-policy`.)*

---

### 3. Chính sách Mua ngay — Buy Now (`AuctionRules.IsBuyNowAvailable`)
- **Khi nào nút Buy Now hiển thị & cho phép bấm?**
  - Phiên đang ở trạng thái **`Running`** và có cài `BuyNowPriceAmount`.
  - **Nếu phiên KHÔNG có Giá sàn (`ReservePrice == null`):** Chỉ hiển thị/bấm được khi **chưa có ai đặt giá (`BidCount == 0`)**. Ngay khi có lượt bid đầu tiên, nút Buy Now tự động ẩn/khóa.
  - **Nếu phiên CÓ Giá sàn (`ReservePrice != null`):** Vẫn hiển thị/bấm được chừng nào **giá dẫn đầu hiện tại vẫn thấp hơn Giá sàn** (`CurrentPrice == null || CurrentPrice < ReservePrice`). Ngay khi có bid chạm hoặc vượt Giá sàn, nút Buy Now tự động tắt.
- **Cơ chế khóa tiền khi bấm Buy Now (Split Hold):**
  - Phiên đóng ngay lập tức (`Ended`), giải tỏa cọc cho các bidder khác và tạo đơn hàng.
  - Ví người mua bị khóa tách 2 khoản: **Tiền cọc** + **Tiền hàng còn lại** (`BuyNowPrice - DepositAmount`).
- **Nếu người mua Buy Now không hoàn tất thanh toán (quá 24h):**
  - Hệ thống **CHỈ tịch thu phần Tiền cọc** (chia 70/30), còn phần **Tiền hàng còn lại sẽ được hoàn trả về Ví** cho người mua.

---

### 4. Chính sách Giá sàn bí mật & Quyền duyệt 12 giờ (`Reserve Price`)
- **Bảo mật:** `ReservePriceAmount` là **bí mật hoàn toàn với Buyer** (API công khai chỉ trả về `hasReservePrice` và `isReserveMet`, tuyệt đối không lộ số tiền). Chỉ Seller chủ phiên (qua `/api/v1/seller/auctions/{id}/pricing`) và Admin/Staff mới xem được.
- **Nếu hết giờ mà giá cao nhất chưa đạt Giá sàn:**
  - Hệ thống không hủy ngay mà chuyển sang trạng thái **`AwaitingSellerApproval`**, cho Seller **12 giờ** để quyết định:
    - **Đồng ý bán (`Accept`):** Chuyển sang chờ Buyer thanh toán trong **24 giờ**, đồng thời **reset chuỗi từ chối của Seller về 0**.
    - **Từ chối bán (`Decline`) hoặc hết 12 giờ không phản hồi:** Phiên chuyển sang `Failed`, hoàn cọc cho người đặt giá cao nhất, và **cộng 1 lần vào chuỗi từ chối liên tiếp của Seller**:
      - Từ chối lần **2**: Hệ thống gửi thông báo **cảnh báo**.
      - Từ chối liên tiếp **3 lần**: Seller bị **tạm khóa quyền đặt Giá sàn trong 14 ngày** (hết 14 ngày tự động mở lại và reset về 0).

---

### 5. Chính sách Gia hạn chống bắn tỉa (`Anti-Sniping`)
- Nếu có lượt đặt giá hợp lệ mới xuất hiện trong khoảng **`AntiSnipingExtensionMinutes` phút cuối** trước giờ kết thúc (`ScheduledEndAt - now <= AntiSnipingExtensionMinutes`) và số lần gia hạn chưa đạt trần `MaxExtensionCount`:
  - Thời gian kết thúc phiên tự động được **cộng thêm đúng `AntiSnipingExtensionMinutes` phút** (kèm thông báo realtime qua SignalR).

---

### 6. Chính sách Thanh toán (24h), Tịch thu cọc & Cơ hội thứ 2 (`Second-Chance Offer`)
- **Thời hạn thanh toán:** Người thắng phiên (thắng thường, Buy Now, hoặc được Seller duyệt dưới giá sàn) có **24 giờ** để hoàn tất thanh toán đơn hàng.
- **Nếu quá 24 giờ không thanh toán:**
  1. **Tịch thu cọc (`Forfeit`):** Toàn bộ tiền cọc của người thắng bị tịch thu và chia theo tỷ lệ cấu hình (mặc định **70% cho Seller, 30% cho Sàn**).
  2. **Ghi nhận vi phạm:** Báo về `UserService` để trừ điểm uy tín và tăng streak vi phạm (có thể làm tăng % cọc ở các phiên sau hoặc tạm đình chỉ đấu giá nếu vi phạm nhiều).
  3. **Mời người về nhì (`Second-Chance Offer`):**
     - Chỉ áp dụng cho **phiên đấu giá thường đã đạt giá sàn** (không áp dụng cho Buy Now hoặc phiên bán dưới giá sàn).
     - Hệ thống gửi lời mời mua lại đúng bằng mức giá người về nhì đã đặt, thời hạn phản hồi là **24 giờ**.
     - **Người về nhì Chấp nhận:** Có **24 giờ** để thanh toán đơn hàng mới (nếu tiếp tục không thanh toán → tịch thu cọc người về nhì và đóng phiên `Failed`).
     - **Người về nhì Từ chối hoặc quá 24 giờ không phản hồi:** Không phạt người về nhì, hệ thống **hoàn lại cọc** cho người về nhì và đóng phiên `Failed` (mở khóa sản phẩm để Seller tạo lại phiên).
