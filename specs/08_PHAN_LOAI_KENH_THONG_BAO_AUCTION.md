# Phân loại các luồng Thông báo (Notification) trong phân hệ Đấu giá

Tài liệu này tổng hợp lại toàn bộ các loại thông báo mà `AuctionService` đang phát ra, chia rõ cái nào gửi qua **Email**, cái nào hiện trên **chuông thông báo của web (In-App Notification)**, và cái nào đẩy **trực tiếp lên màn hình đang mở (SignalR Realtime)** để anh em nắm chung khi test hoặc giải thích luồng nghiệp vụ.

---

## 1. Tại sao hệ thống lại chia ra nhiều kênh khác nhau?

Trong một phiên đấu giá, không phải sự kiện nào cũng nên bắn Email:
- Những sự kiện xảy ra dồn dập theo từng giây (như có người vừa đặt giá mới, bị vượt giá, phiên vừa cộng thêm 5 phút chống sniping) nếu gửi Email sẽ làm nổ hộp thư của người dùng và thư đến nơi thì phiên cũng đã nhảy sang giá khác. Các sự kiện này được ưu tiên hiển thị ngay trên **màn hình đang mở (Realtime)** và **chuông thông báo trên web (In-App)**.
- Ngược lại, những cột mốc quan trọng liên quan đến **tiền bạc, đơn hàng và hạn chót 24 giờ** (mà lúc đó người mua thường đã tắt tab trình duyệt đi làm việc khác, chờ hết giờ đấu giá) thì bắt buộc phải gửi **cả Email lẫn Chuông thông báo trên hệ thống** để họ không bị lỡ hạn thanh toán dẫn tới mất cọc.

---

## 2. Nhóm gửi CẢ HAI KÊNH: Email + Chuông thông báo trên giao diện (In-App)

Trong code của `AuctionService` (`NotificationDispatcher.cs`), có **2 sự kiện** mà worker bắt buộc phải gọi sang `UserService` (`/api/v1/internal/users/contact-info`) để lấy cho bằng được `winnerEmail` và `winnerFullName` trước khi gửi sang `NotificationService`. Đây là 2 sự kiện vừa hiện trên chuông thông báo của web, vừa gửi Email trực tiếp vào hộp thư của người dùng:

| STT | Sự kiện | Route gọi sang Notification | Ai nhận? | Khi nào kích hoạt & Nội dung chính |
| :-: | :--- | :--- | :--- | :--- |
| 1 | **Thắng phiên đấu giá** (`auction-winners`) | `POST /api/v1/notifications/auction-winners` | **Buyer** (Người thắng) | Kích hoạt ngay khi người mua thắng phiên (do hết giờ cao giá nhất, bấm Mua ngay, hoặc được Seller duyệt bán dưới giá sàn). Báo giá trúng, mã kết quả và **hạn chót 24 giờ phải vào thanh toán đơn hàng**. |
| 2 | **Quá hạn 24h không thanh toán** (`auction-payment-timeouts`) | `POST /api/v1/notifications/auction-payment-timeouts` | **Buyer** (Người thắng bỏ kèo) | Kích hoạt khi hết 24 giờ mà người thắng không thanh toán đơn đấu giá. Báo đơn hàng đã bị hủy, phiên không thành công với người đó và khoản tiền cọc bị khấu trừ theo quy định. |

*(Ghi chú kỹ thuật: Trong `appsettings` / `.env` có biến `Notification:DevWinnerEmail`, nếu bật biến này lúc test thì mọi email thắng phiên hoặc quá hạn thanh toán sẽ đổ hết về hộp thư test đó thay vì gửi vào email thật của tài khoản).*

---

## 3. Nhóm chỉ hiển thị trên Chuông thông báo giao diện hệ thống (In-App Notification)

Nhóm này gồm **12 sự kiện**. `AuctionService` chỉ truyền `UserId` của người nhận (`sellerUserId`, `bidderUserId`, `watcherUserIds`...) mà không cần tra cứu địa chỉ Email. Khi người dùng đăng nhập vào web và bấm vào biểu tượng cái chuông ở góc trên bên phải, các thông báo này sẽ xuất hiện trong danh sách:

### A. Dành cho Người mua (Buyer)
1. **Bị người khác vượt giá (`auction-outbids`):**
   - Báo cho người đang giữ giá cao nhất biết vừa có người trả mức giá cao hơn (`newAmount`) kèm thời gian kết thúc phiên để vào trả giá lại nếu muốn.
2. **Phiên đang theo dõi có lượt đặt giá mới (`auction-watchlist-bids`):**
   - Gửi cho danh sách những người đã bấm nút *"Theo dõi phiên"* (và có bật nhận thông báo) khi phiên đó có mức giá mới.
3. **Nhận lời mời mua lại cho Người về nhì (`auction-winner-offers`):**
   - Khi người thắng Hạng 1 bỏ cọc / không thanh toán, hệ thống gửi thông báo cho người xếp Hạng 2 biết họ có 24 giờ để vào mục *Lời mời đấu giá* chọn Đồng ý mua (với mức giá của chính họ) hoặc Từ chối.
4. **Biến động trạng thái tiền cọc (`auction-deposits`):**
   - Báo cho người mua mỗi khi khoản cọc của họ đổi trạng thái:
     - `Authorized`: Đã khóa cọc thành công, đủ điều kiện đặt giá.
     - `Released`: Đã mở khóa hoàn cọc về ví (kèm lý do rõ ràng: người khác thắng, phiên bị hủy, người bán không bán dưới giá sàn...).
     - `Captured`: Tiền cọc đã được khấu trừ vào đơn hàng (nếu thanh toán) hoặc bị tịch thu (nếu bỏ kèo không thanh toán).
     - `Refunded`: Hoàn trả lại tiền cọc đã thu (khi người bán từ chối hoặc không xác nhận đơn hàng sau khi người mua đã trả tiền).
5. **Kết quả xử lý báo cáo vi phạm (`auction-report-resolved`):**
   - Gửi cho những người mua đã bấm *"Báo cáo phiên"* khi Admin/Staff xử lý xong (cảm ơn vì đã báo cáo khi Admin hủy cưỡng chế phiên gian lận, hoặc thông báo bác bỏ báo cáo kèm ghi chú của Admin).

### B. Dành cho Người bán (Seller)
6. **Kết quả phiên đấu giá khi có người thắng (`auction-seller-results`):**
   - Báo cho người bán ngay khi phiên kết thúc và đã xác định được người thắng cuộc, kèm mức giá chốt và hạn thanh toán của người mua.
7. **Người mua đã thanh toán đơn hàng đấu giá (`auction-order-payments`):**
   - Báo cho người bán (và xác nhận cho người mua) khi đơn hàng đấu giá đã được trả tiền xong (`Paid`), nhắc người bán vào tiếp nhận đơn và giao hàng trước hạn `sellerAcceptDeadline`.
8. **Yêu cầu duyệt bán dưới giá sàn trong 12 giờ (`auction-reserve-approval-needed`):**
   - Khi phiên hết giờ mà giá cao nhất vẫn thấp hơn mức Giá sàn bí mật (`ReservePrice`), hệ thống báo cho người bán biết mức giá cao nhất hiện tại và nhắc họ vào bấm *Đồng ý bán* hoặc *Từ chối* trong vòng 12 giờ.
9. **Cảnh báo / Khóa quyền đặt Giá sàn (`auction-reserve-restriction`):**
   - Gửi cho người bán khi họ từ chối (hoặc để quá hạn 12 giờ không duyệt) bán dưới giá sàn từ **lần thứ 2 liên tiếp** (gửi cảnh báo) và **lần thứ 3 liên tiếp** (báo khóa quyền đặt giá sàn trong 14 ngày).
10. **Người thắng quá hạn không thanh toán đơn (`auction-seller-payment-timeouts`):**
    - Báo cho người bán biết người thắng đã không thanh toán trong 24 giờ nên đơn hàng của người đó đã hủy (để chuyển sang mời người về nhì hoặc đóng phiên).
11. **Phiên đấu giá thất bại (`auction-failed`):**
    - Báo cho người bán khi phiên khép lại ở trạng thái `Failed` (hết giờ không có ai đặt giá, người bán từ chối bán dưới giá sàn, người mua không thanh toán và không có người về nhì nhận lời mời, hoặc đơn hàng bị hủy sau thanh toán).
12. **Phiên đấu giá bị hủy (`auction-cancelled`):**
    - Báo cho người bán khi phiên bị hủy trước khi kết thúc: bao gồm lúc Seller tự hủy, lúc **Staff/Admin từ chối duyệt phiên (`Reject`)**, hoặc lúc **Admin cưỡng chế hủy phiên (`Force Cancel`)** kèm theo lý do cụ thể.

---

## 4. Nhóm cập nhật trực tiếp trên màn hình đang mở (SignalR Realtime - Không lưu vào chuông hay gửi Mail)

Ngoài 14 loại thông báo lưu qua `NotificationService` ở trên, khi người dùng đang mở trực tiếp trang phòng đấu giá (`/auctions/[id]`), `AuctionHub` (SignalR) sẽ đẩy 3 sự kiện tức thời sau đây để giao diện tự nhảy số mà không làm rác hộp thư hay chuông thông báo:

1. **`BidPlaced`:** Nhảy giá hiện tại, tăng số lượt đặt giá, cập nhật bảng xếp hạng người dẫn đầu và **cập nhật luôn đồng hồ đếm ngược nếu phiên vừa được gia hạn thêm 5 phút chống sniping** *(đó là lý do khi phiên gia hạn thêm 5 phút thì hệ thống chỉ nhảy đồng hồ trực tiếp trên màn hình chứ không gửi Email)*.
2. **`AuctionStatusChanged`:** Tự động đổi nhãn trạng thái phiên trên màn hình (ví dụ khi phiên vừa được duyệt sang `Scheduled`, vừa bắt đầu `Running`, vừa kết thúc `Ended` do có người bấm Mua ngay, hoặc chuyển sang `Settled` / `Failed`).
3. **`AuctionCancelled`:** Khóa ngay nút đặt giá trên màn hình của tất cả những người đang xem nếu phiên bị hủy giữa chừng.

---

## Bảng tóm tắt nhanh để tra cứu khi chấm bài / Demo

| Sự kiện | Giao diện trực tiếp (SignalR) | Chuông thông báo Web (In-App) | Gửi thư (Email) |
| :--- | :---: | :---: | :---: |
| Có lượt đặt giá mới & Gia hạn 5p chống sniping | ✅ Có | ✅ Có *(cho người bị vượt giá & người theo dõi)* | ❌ Không |
| Giữ cọc / Hoàn cọc / Khấu trừ cọc | ❌ Không | ✅ Có | ❌ Không |
| **Người mua thắng phiên đấu giá** | ✅ Có *(đổi trạng thái)* | ✅ **Có** | ✅ **Có** |
| **Người thắng quá hạn 24h không thanh toán** | ✅ Có *(đổi trạng thái)* | ✅ **Có** *(cả Buyer & Seller)* | ✅ **Có** *(cho Buyer)* |
| Mời người về nhì mua lại (Second-Chance) | ❌ Không | ✅ Có | ❌ Không |
| Chờ Seller duyệt giá dưới giá sàn (12h) | ✅ Có | ✅ Có | ❌ Không |
| Cảnh báo / Khóa đặt giá sàn 14 ngày (lần 2, 3) | ❌ Không | ✅ Có | ❌ Không |
| Kết quả phiên cho Seller & Đơn đã thanh toán | ✅ Có | ✅ Có | ❌ Không |
| Phiên thất bại / Bị hủy / Bị từ chối duyệt | ✅ Có | ✅ Có | ❌ Không |
| Kết quả xử lý báo cáo vi phạm phiên | ❌ Không | ✅ Có | ❌ Không |
