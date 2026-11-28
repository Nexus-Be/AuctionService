# Lưu ý cho FE: Trường hợp người về nhì có giá đặt thấp hơn giá sàn

Khi người thắng đầu tiên không thanh toán (bỏ cọc) và hệ thống chuyển sang xét người về nhì (Second-Chance), có một trường hợp phát sinh: **giá đặt của người về nhì thấp hơn giá sàn bí mật (`ReservePrice`) mà người bán đã đặt.**

Bên BE đã bổ sung xử lý cho trường hợp này theo đúng luồng duyệt giá sàn, dưới đây là những điểm FE cần nắm để hiển thị khớp với BE:

## 1. Hỏi người bán trước, người bán đồng ý mới báo người về nhì
- Khi Hạng 1 hết hạn thanh toán, nếu giá của người về nhì **đạt giá sàn**: hệ thống gửi thông báo mời người về nhì ngay như bình thường.
- Nếu giá của người về nhì **thấp hơn giá sàn**:
  - Người về nhì **chưa** nhận được thông báo mời mua.
  - Người bán sẽ nhận được thông báo yêu cầu duyệt bán dưới giá sàn trong vòng **12 giờ** (`AuctionReserveApprovalNeeded`).

## 2. Giao diện phía Người bán (`/seller/auctions/[id]/result`)
- Ở trang kết quả phiên của người bán, khi `settlement.status` là `AwaitingSellerApproval` (hoặc `showsSellerApproval === true`), người bán sẽ thấy 2 nút **Đồng ý bán dưới giá sàn** và **Từ chối bán** như luồng dưới giá sàn bình thường.
- **Nếu người bán Đồng ý:** BE gia hạn lời mời 24 giờ và lúc này mới gửi thông báo (`AuctionWinnerOffer`) cho người về nhì vào nhận kèo.
- **Nếu người bán Từ chối:** Phiên chuyển sang `Failed`, hoàn cọc cho người về nhì và tính 1 lần từ chối vào chuỗi hạn chế giá sàn của người bán.

## 3. Giao diện phía Người về nhì (`/buyer/auctions`)
- Trong lúc người bán chưa duyệt (`AwaitingSellerApproval`), nếu người về nhì có tự gọi API nhận kèo thì BE sẽ chặn và trả về lỗi `409` (`SELLER_APPROVAL_REQUIRED`).
- Sau khi người bán đồng ý và người về nhì nhận được thông báo, người về nhì vào bấm Đồng ý / Từ chối bình thường như trước.
