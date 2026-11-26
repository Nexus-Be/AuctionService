# Góp ý trải nghiệm giao diện Đấu giá sau đợt ngồi test luồng (26/11)

> **Lưu ý nhẹ trước khi đọc:** Đây là ý kiến cá nhân của tui trong lúc ngồi bấm test luồng thôi nha, anh em FE có thể nghe hoặc hong :))) Cái nào hợp lý thì anh em nhặt vào làm cho mượt, cái nào bên FE có chủ đích thiết kế vậy rồi thì cứ bỏ qua nghen.

Hôm nay tui ngồi đóng vai đủ cả Seller, Buyer với Admin để bấm xuyên suốt từ lúc tạo phiên, đặt giá, nâng hạn mức, chốt phiên cho tới lúc tạo đơn hàng. Luồng chính chạy được tới bước lên đơn rồi, nhưng lúc ngồi bấm thực tế thì có mấy chỗ giao diện chưa báo gì hoặc hiển thị hơi khó hiểu cho người dùng. Tui gom lại những điểm thuộc phần giao diện theo từng màn hình dưới đây cho anh em tiện dò.

---

## 1. Luồng người bán (Seller)

### Tạo và chỉnh sửa phiên đấu giá (`/seller/auctions/new` & `/seller/auctions/[id]/edit`)
- **Báo lỗi gom chung ở trên cùng khi nhập sai trường:** Lúc tạo phiên mà lỡ nhập sai giá khởi điểm, bước giá, giá sàn hay thời gian bắt đầu/kết thúc thì form đang hiện chung một dòng báo lỗi ở trên cùng chứ chưa chỉ rõ ngay dưới từng ô input bị sai (ví dụ bước giá phải là bội số của `1.000đ`, hoặc giá sàn phải lớn hơn giá khởi điểm). Anh em gắn lỗi đỏ ngay dưới từng ô input tương ứng thì người bán đỡ phải mò xem mình nhập sai ở ô nào.
- **Không có thông báo xác nhận sau khi bấm Lưu thay đổi phiên:** Ở màn hình chỉnh sửa phiên (`/seller/auctions/[id]/edit`), lúc bấm **Lưu thay đổi** xong hệ thống chuyển thẳng về trang chi tiết phiên mà không hiện toast hay thông báo `"Cập nhật phiên đấu giá thành công"`, nên người bán không biết thay đổi của mình đã lưu ăn chưa.

---

## 2. Luồng người mua (Buyer)

### Màn hình chi tiết phiên & đặt giá (`/auctions/[id]`)
- **Nút Đặt giá bị khóa khi chưa đóng cọc nhưng thiếu câu nhắc ngay tại chỗ:** Khi người mua chưa nạp đủ tiền hoặc chưa khóa cọc mà nhìn vào khung Đặt giá bên phải, nút **Đặt giá** bị khóa cứng (`disabled`) nhưng ngay chỗ nút đó không có dòng chữ nào giải thích tại sao bị khóa (trong khi hộp giữ cọc lại nằm tuốt ở phía trên đầu trang). Anh em hiện thêm một dòng nhắc nhỏ ngay dưới nút Đặt giá kiểu *"Bạn cần giữ cọc ở khung phía trên trước khi đặt giá"* (hoặc nút cuộn/nhảy lên phần Khóa cọc / Nạp ví) thì người mua đỡ tưởng web bị đơ nút.
- **Thông báo đặt giá thành công dễ bị khuất tầm mắt:** Khi đặt giá xong, dòng chữ `"Đã đặt giá thành công"` hiện ở tít trên đầu trang trong khi người mua đang nhìn ở khung đặt giá bên dưới. Có thêm một cái toast nổi ở góc màn hình sẽ giúp người mua nhận biết ngay là lệnh đặt giá đã vào.
- **Câu chữ chỗ gia hạn chống sniping hơi khó hiểu:** Trên giao diện đang hiện dòng `"Đã gia hạn 5 phút · lần 1/10"`. Người dùng phổ thông đọc vào dễ thắc mắc không biết `1/10` là gì. Anh em có thể đổi lại cho gần gũi hơn, ví dụ: *"Phiên vừa được cộng thêm 5 phút do có người đặt giá sát giờ (đã gia hạn 1/10 lần tối đa)"*.
- **Hiển thị rõ người thắng cuộc trên trang chi tiết phiên sau khi hết giờ:** Khi đồng hồ đếm ngược về `00:00` và phiên chuyển sang `Đã kết thúc`, người xem vào trang chi tiết phiên chỉ thấy chữ `"Đã hết giờ"` và giá cuối ở khung bên phải chứ chưa thấy dòng công bố người thắng cuộc ngay trên thẻ giá (bên BE đã trả sẵn trường `leadingBidderDisplayName` chứa tên che của người thắng khi phiên `Ended`/`Settled` rồi, anh em móc ra hiện ngay dưới giá cuối là đẹp luôn).

### Luồng nạp ví khi đang chuẩn bị đấu giá (`/wallet`)
- **Nạp tiền qua Stripe xong quay về ví chưa tự làm mới số dư và trạng thái giao dịch ngay:** Lúc nạp thành công từ cổng Stripe quay lại trang Ví, danh sách giao dịch bên dưới đôi khi vẫn còn nút tiếp tục thanh toán (`/resume`) cho giao dịch đã `Completed`, bấm vào bị báo lỗi `409 Conflict` và số dư ví trên cùng chưa tự cộng lên ngay mà phải F5. Anh em cho gọi lại API làm mới số dư ví + ẩn nút tiếp tục thanh toán đối với các giao dịch đã hoàn tất là êm.

---

## 3. Luồng quản trị viên (Admin / Staff)

### Hộp điều khiển thời gian test (`TimeControlsPanel`)
- **Bấm "Mở ngay" hoặc "Kết thúc ngay" xong phải chờ hoặc tự F5:** Khi phiên đang ở trạng thái `Sắp diễn ra` (`Scheduled`), Admin bấm nút **"Mở ngay"** thì API cập nhật giờ bắt đầu về hiện tại, nhưng worker bên dưới cần khoảng 1–5 giây để quét và chuyển trạng thái phiên sang `Đang diễn ra` (`Running`). Vì giao diện chỉ gọi `reload()` đúng 1 lần ngay lúc bấm nên trạng thái trên màn hình vẫn đứng ở `Sắp diễn ra`, phải tự bấm F5 lại mới thấy đổi. Anh em cho tự động poll/reload lại thêm 1–2 nhịp sau khoảng 3–5 giây kể từ lúc bấm nút trên `TimeControlsPanel` là khỏi phải F5 tay.
- **Nhãn nút đặt thời gian còn lại dễ gây hiểu nhầm là cộng thêm giờ:** Chỗ ô nhập giây trên `TimeControlsPanel` đang ghi nhãn là `"Gia hạn còn (giây)"`, làm người test dễ hiểu nhầm là *"Cộng thêm X giây vào thời gian hiện tại"* (ví dụ phiên đang còn 10 phút, gõ `30` vì tưởng cộng thêm 30 giây thì phiên tụt xuống còn đúng 30 giây). Anh em đổi nhãn chữ đó thành `"Đặt tổng thời gian còn lại (giây)"` hoặc tách thêm một nút `"+30s vào giờ kết thúc"` (lấy `scheduledEndAt` hiện tại cộng thêm 30 giây) thì lúc demo sẽ không bị bấm nhầm.

### Giao diện chung khi đăng nhập bằng tài khoản Admin
- **Admin bấm về Trang chủ vẫn thấy thanh menu mua hàng và nút "Đăng ký bán hàng":** Khi đang đăng nhập bằng tài khoản role `Admin`, nếu bấm logo quay về trang chủ thì thanh điều hướng vẫn hiện nguyên các mục của Buyer như `Giỏ hàng`, `Đơn mua`, `Cọc của tôi`, `Đăng ký bán hàng`. Anh em xem ẩn mục `Đăng ký bán hàng` đối với tài khoản `Admin`/`Seller` và thêm nút quay nhanh về `Bảng điều khiển Admin` trên thanh đầu trang cho đỡ rối nha.

---
*Tổng kết lại là giao diện đấu giá bên FE làm khá đủ đồ chơi rồi, kể cả bộ `TimeControlsPanel` chỉnh giờ test cực kỳ tiện. Anh em xem mấy góp ý nhỏ về thông báo toast, chỉ rõ lỗi dưới từng ô input với nhãn nút ở trên, thấy cái nào hợp lý thì tinh chỉnh thêm cho trải nghiệm người dùng mượt hơn nha!*
