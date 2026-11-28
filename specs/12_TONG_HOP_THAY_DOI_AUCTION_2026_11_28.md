# Tổng hợp các thay đổi của `AuctionService` (28/11/2026)

## 1. Xử lý tức thì khi Admin kết thúc hạn thanh toán / Second-Chance (`AdminUpdateSettlementDeadlinesAsync`)
- **Vấn đề trước đây:** Khi Admin đặt `paymentRemainingSeconds = 0` hoặc `winnerOfferRemainingSeconds = 0` để test/demo, BE chỉ cập nhật mốc thời gian trong DB của `AuctionService` mà không kích hoạt xử lý ngay (phải chờ `CommerceService` quét đơn quá hạn bắn callback sang).
- **Đã sửa:**
  - Khi `paymentRemainingSeconds == 0`: `AuctionService` tự động gọi `HandlePaymentStatusAsync` với trạng thái `PaymentTimedOut` ngay lập tức để **thu cọc Winner 1 (Forfeit)**, ghi nhận vi phạm và **tạo lời mời Second-Chance** cho người về nhì.
  - Khi `winnerOfferRemainingSeconds == 0`: `AuctionService` tự động chuyển lời mời sang `Expired`, đóng phiên `Failed`, giải tỏa cọc cho người về nhì, mở khóa sản phẩm ở Catalog và gửi thông báo `auction-failed` cho Seller.

## 2. Luồng Second-Chance dưới giá sàn (`ReservePriceAmount`)
- Khi Winner 1 không thanh toán và người về nhì có mức giá **thấp hơn giá sàn**:
  - Chuyển settlement sang `AwaitingSellerApproval` (hạn 12 giờ), **chưa** gửi thông báo cho người về nhì mà gửi `ReserveApprovalNeeded` hỏi Seller trước.
  - Nếu Seller **Đồng ý** (`ApproveBelowReserveAsync`): gia hạn lời mời 24 giờ và lúc này mới gửi thông báo `ReplacementWinnerOffer` cho người về nhì.
  - Nếu Seller **Từ chối** (`RejectBelowReserveAsync`): đánh dấu lời mời `Expired`, đóng phiên `Failed`, giải tỏa cọc cho người về nhì.
  - Chặn người về nhì tự accept trước khi Seller duyệt (`409 SELLER_APPROVAL_REQUIRED`).

## 3. Bổ sung 3 thông báo còn thiếu cho Buyer (qua `/api/v1/notifications/in-app`)
1. **Thông báo cho Buyer Hạng 1 đang chờ Seller duyệt dưới giá sàn (`NotifyBuyerBelowReservePending`):** Gửi ngay khi phiên kết thúc dưới giá sàn, báo mức giá cao nhất của họ đang chờ Seller xác nhận trong 12 giờ.
2. **Thông báo lý do cho Buyer Hạng 1 khi Seller từ chối bán dưới giá sàn (`NotifyBuyerBelowReserveRejected`):** Gửi khi Seller từ chối, báo rõ lý do phiên `Failed` là do người bán từ chối bán dưới giá sàn và tiền cọc đã được hoàn lại.
3. **Thông báo chia buồn cho những người tham gia đặt giá nhưng không thắng (`NotifyAuctionLosers`):** Gửi khi phiên kết thúc có người thắng (cả kết thúc bình thường và Mua ngay) cho toàn bộ các bidder còn lại, báo phiên đã kết thúc và tiền cọc sẽ được hoàn lại.

## 4. Quy tắc đặt giá đầu tiên (`BidRules.MinNextBid`)
- Lượt đặt giá đầu tiên bắt buộc phải đạt tối thiểu **`startPriceAmount + bidStepAmount`** (Giá khởi điểm + 1 bước giá).
