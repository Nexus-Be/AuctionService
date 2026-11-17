# Phản hồi Hợp đồng Tích hợp Wallet – Auction (Đã chốt nghiệp vụ)

> **Bên gửi:** Team AuctionService (`:8005`)  
> **Bên nhận:** Team WalletService (`:8008`)  
> **Mục đích:** Xác nhận hợp đồng tích hợp W1–W5, quy trình đối soát và các điểm cần Wallet đồng bộ cùng Auction & Commerce.

Chào team Wallet,

Phía Auction đã hoàn thiện toàn bộ nghiệp vụ và cơ chế đối soát cho luồng đặt cọc. Để hai bên đồng bộ thông suốt với nhau (và khớp với Commerce), team Auction xác nhận hợp đồng và đề nghị Wallet đồng bộ theo các điểm dưới đây:

---

## 1. Xác nhận 6 câu hỏi hợp đồng

1. **Định danh & Lưu trữ**:
   - `AuctionId`, `BidderId`, `OrderId` (do Commerce cấp) đều là `UUID`.
   - Hold reference dùng `referenceType = "AUCTION_DEPOSIT"` và `referenceId = AuctionDeposit.Id` (duy nhất cho mỗi cặp `AuctionId` – `BidderId`).
   - Auction lưu `holdId` trả về vào cột `AuctionDeposits.GatewayReference`.

2. **Sự kiện kích hoạt các thao tác Wallet**:
   - **Reserve (W1)**: Buyer đóng cọc tham gia phiên.
   - **Release (W3)**: Hủy phiên (Seller/Admin), phiên `Failed` không có bid, kết thúc phiên (mở khóa từ hạng 3 trở xuống, giữ Top 1 & Top 2), phiên `Settled` (mở khóa cọc người về nhì), người về nhì từ chối/hết hạn offer, hoặc đơn hàng bị `Cancelled`.
   - **Capture `ORDER_PAYMENT` (W2)**: Khi Commerce báo đơn đã thanh toán (`Paid`). Auction chuyển 100% cọc sang `SYSTEM_ESCROW` kèm `orderId`.
   - **Capture `FORFEIT` (W2)**: Khi **Người thắng #1** hoặc **Người về nhì (đã bấm Accept)** bỏ kèo / quá hạn thanh toán (`PaymentTimedOut` / `Expired` / `Failed`).
   - **Refund Captured (W5)**: Khi Seller từ chối (`SellerRejected`) hoặc quá hạn duyệt (`SellerExpired`) sau khi Buyer đã `Paid`.

3. **Payload & Chia tỷ lệ `FORFEIT` (70/30)**:
   - Auction sở hữu cấu hình `DepositPolicy` (theo `PolicyVersion` tại thời điểm Buyer đóng cọc, mặc định **70% Seller / 30% Sàn**).
   - Auction tính sẵn số nguyên VND: `sellerShare = floor(Amount * 70 / 100)` (`toType = "SELLER_WALLET"`, `toOwnerId = sellerId`) và `platformShare = Amount - sellerShare` (`toType = "PLATFORM"`, `toOwnerId = null`). Nếu thiếu `sellerId`, toàn bộ chuyển về `PLATFORM`.
   - *Ví dụ cọc `1.000.000` VND bị tịch thu*: `SELLER_WALLET` nhận `700.000` VND, `PLATFORM` nhận `300.000` VND.

4. **Phối hợp với Commerce & Chống xử lý sớm**:
   - Commerce báo kết quả thanh toán qua callback đồng bộ (có Outbox retry bên Commerce).
   - Auction giữ nguồn sự thật `PaymentDeadlineAt`: nếu Commerce báo quá hạn sớm hơn hạn lưu ở Auction (dung sai 2 phút), Auction từ chối `409 PAYMENT_DEADLINE_NOT_REACHED`.

5. **Xử lý lỗi & Ca đặc biệt**:
   - Mọi lệnh gọi W1/W2/W3/W5 đều gửi kèm `Idempotency-Key` (UUID v4 hash cố định theo `deposit:{id}:{attempt}`, `release:{id}:{attempt}`, `capture:{id}`, `refund:{id}`) nên retry không bao giờ bị trừ/hoàn/mở khóa 2 lần.
   - Nếu ví Buyer bị đóng băng (`403 WALLET_FROZEN`): W1 từ chối tạo cọc; W2/W3/W5 vẫn thực thi được hoặc retry nền.

6. **Chống hoàn tiền 2 lần khi Seller từ chối**:
   - Commerce đã trừ sẵn `depositAmount` khi tạo đơn (`RemainingAmount = WinningAmount - DepositAmount`).
   - Khi Seller từ chối/quá hạn: Commerce **chỉ hoàn phần `RemainingAmount + Ship`**, còn Auction gọi **W5 hoàn riêng `DepositAmount`** -> không trùng lặp nguồn tiền.

---

## 2. Cơ chế Đối soát (Reconciliation) phía Auction

- **Lưu trạng thái trước khi gọi**: Auction lưu `PendingAction` (`Release` / `Capture` / `Refund`), `PendingPurpose` và `GatewayReference` trong cùng DB transaction trước khi chạy fast-path. Khi retry sẽ dựng lại đúng payload và dùng lại đúng `Idempotency-Key`.
- **Tần suất quét & Backoff**: Worker quét **1 phút/lần**; khi lỗi mạng/5xx sẽ tăng `ActionFailureCount` và giãn cách thử lại **1 -> 2 -> 5 -> 10 -> 30 phút**.
- **Cảnh báo vận hành (Escalation)**: Quá **10 lần lỗi** sẽ bật cờ `NeedsAttention = true` kèm `LastError` để Admin kiểm tra và xử lý trên API đối soát (`GET /api/v1/admin/auctions/{id}/deposits`).
- **Xử lý timeout & cọc kẹt**:
  - Cọc ở trạng thái `Pending` quá 30 giây: tự động tra cứu `W4 by-reference` để đồng bộ (quá 10 phút không có hold sẽ đánh `Failed`).
  - Cọc vẫn `Authorized` trên phiên đã đóng (`Settled` / `Failed` / `Cancelled`) quá **5 phút**: tự động đối soát `Release` / `Capture` / `Forfeit`.

---

## 3. Đề nghị bên Wallet đồng bộ cùng Auction & Commerce

1. **Bật `ORDER_PAYMENT` capture ở tất cả các môi trường (kể cả Staging/Production)**: Vì Commerce và Auction đã nối xong luồng khấu trừ cọc và giải ngân 95% `WinningAmount` qua `SYSTEM_ESCROW`, cần đảm bảo Wallet không tắt `ORDER_PAYMENT` ngoài Development.
2. **Hỗ trợ `FORFEIT` cho cả Người về nhì bỏ kèo**: Nghiệp vụ của Auction quy định Người về nhì đã bấm `Accept` nhưng quá hạn không thanh toán cũng bị tịch thu cọc 70/30 giống Người thắng #1 (cách gọi W2 hoàn toàn giống nhau, Wallet không cần đổi API).
3. **Thống nhất bỏ qua bước lưu raw JSON payload / ledger ref bên Auction**: Cách lưu state + tái tạo payload theo `Idempotency-Key` hiện tại của Auction đã đảm bảo idempotent 100%, không cần thay đổi thêm bảng hay endpoint mới.

Nếu team Wallet đồng ý các điểm trên, chúng ta có thể tiến hành chạy kiểm thử tích hợp E2E trên môi trường PostgreSQL + mạng thật ngay.
