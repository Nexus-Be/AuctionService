# TÀI LIỆU BÀN GIAO TÍCH HỢP CHO `CommerceService`: BUY NOW & GIÁ SÀN

> **Từ:** `AuctionService` (port `8005`)  
> **Gửi tới:** `CommerceService` (port `8003`)  
> **Mục đích:** Thông báo các thay đổi trong luồng đẩy settlement tạo đơn (`POST /api/v1/auction-orders/settlement`) khi bổ sung tính năng **Buy Now** và **Giá sàn (Reserve Price)**.

---

## 1. Luồng Buy Now (`Source = "BUY_NOW"`)

### 1.1. Điều gì thay đổi phía `AuctionService`?
- Khi Buyer bấm **Mua ngay (`Buy Now`)**, `AuctionService` đã **khóa (Hold) sẵn 100% tiền hàng (`BuyNowPrice`) trong Ví Nexus** của Buyer (tách làm 2 hold: `Tiền cọc` + `Tiền hàng còn lại`).
- Sau đó `AuctionService` đẩy settlement sang `CommerceService` qua `POST /api/v1/auction-orders/settlement` với payload:
  * `source`: **`"BUY_NOW"`** *(giá trị mới, bên cạnh `"WINNER"` và `"SECOND_CHANCE"`)*
  * `winningAmount`: `BuyNowPrice` (tổng tiền hàng)
  * `depositAmount`: **`BuyNowPrice`** *(bằng đúng tổng tiền hàng đã được giữ sẵn trong Ví!)*
  * `paymentDeadlineAt`: Hạn `24 giờ` để Buyer thanh toán phần phí vận chuyển.

### 1.2. Việc `CommerceService` cần làm
1. **Chấp nhận `source = "BUY_NOW"`** tại `POST /api/v1/auction-orders/settlement` (không từ chối giá trị `source` mới này).
2. **Tính số tiền Buyer cần thanh toán thêm lúc checkout (`PUT .../shipping` & `POST .../pay`):**
   - Vì `depositAmount == winningAmount` (`BuyNowPrice`), số tiền hàng còn thiếu = **`0 đ`**.
   - Buyer **chỉ phải trả thêm `Phí vận chuyển (ShippingFee)`** (tính theo địa chỉ nhận hàng) trong vòng **24 giờ**.
   - *(Lưu ý: Nếu phần logic tính của Commerce đang là `RemainingGoods = WinningAmount - DepositAmount` thì tự động ra `0 đ`, rất khớp! Chỉ cần đảm bảo Commerce **không** tự đánh dấu đơn là `Paid` khi `ShippingFee > 0`, vẫn để `PendingPayment` chờ Buyer chọn địa chỉ & trả phí ship trong 24h).*
3. **Callback kết quả thanh toán (`POST /api/v1/internal/auctions/{id}/settlement/payment-status`):**
   - Giữ nguyên contract hiện tại:
     - Buyer trả phí ship xong $\rightarrow$ gửi `Status = Paid` (Auction sẽ Capture cả Tiền cọc và Tiền hàng còn lại).
     - Quá 24h Buyer không trả phí ship $\rightarrow$ gửi `Status = Expired` / `PaymentTimedOut` (Auction tự động tịch thu đúng phần Tiền cọc và **mở khóa hoàn trả phần Tiền hàng còn lại** về Ví cho Buyer).

---

## 2. Luồng Giá sàn (`Reserve Price`)
- **Không cần sửa gì lớn ở Commerce cho luồng Giá sàn:**
  - Khi phiên hết giờ mà giá cao nhất **chưa đạt Giá sàn**, `AuctionService` giữ settlement ở trạng thái `AwaitingSellerApproval` (chờ Seller duyệt 12h) và **CHƯA** đẩy sang `CommerceService`.
  - Chỉ khi Seller bấm **Đồng ý bán dưới giá sàn**, `AuctionService` mới chuyển settlement sang `PendingPayment` (hạn 24h) và đẩy sang `CommerceService` với `source = "WINNER"` như một đơn đấu giá bình thường.
  - Nếu Hạng 1 bỏ không thanh toán đơn này $\rightarrow$ Commerce gửi `Expired`/`PaymentTimedOut` như bình thường (Auction sẽ tự đóng phiên `Failed` và **không** tạo `SECOND_CHANCE`).
