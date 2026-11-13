# 26 · Cập nhật v0.4: giữ cọc bằng số dư ví Nexus (hướng C)

> File này **thay thế** nội dung Wallet của bộ tài liệu v0.3 (API 14–17 và các chỗ phụ thuộc hold của cổng thanh toán). Contract gửi Wallet nằm ở `WALLET_REPLY_AND_API_REQUEST.md` (W1–W6).

## 1. Đã chốt

| Chủ đề | Quyết định |
|---|---|
| Nguồn cọc | **Số dư ví Nexus**, khóa bằng bút toán nội bộ. Không hold Stripe, không redirect sang cổng khi đóng cọc. |
| Nguyên tắc spec cũ "không lấy cọc từ Nexus Wallet" | **Được thay thế.** Cọc lấy từ số dư ví dưới dạng khóa, không phải thanh toán. |
| Kết quả đóng cọc | Đồng bộ, Wallet trả ngay `Held` hoặc lỗi thiếu số dư. Không callback. |
| Cơ chế bù khi lỗi | Cột trạng thái + job quét ở Auction (Overview 4.1). Không Outbox/Inbox. |
| Hoàn tiền | Về ví Nexus: người thua = mở khóa; Seller từ chối = hoàn phần cọc đã Capture về ví Buyer. |
| VNPay | Không liên quan cọc. Khôi phục nạp tiền sau là việc độc lập. |
| Hold 8 ngày, Stripe extended | Không còn là vấn đề. **Bỏ hướng B và `captureMode`.** |
| Chia tiền tịch thu | Auction tính, Wallet thực thi danh sách chuyển tiền. |

## 2. Mô hình hướng C

```
AvailableBalance = Balance − HeldBalance
```
- Đóng cọc: `HeldBalance` tăng, `Balance` không đổi.
- Release: `HeldBalance` giảm (tiền quay lại khả dụng).
- Capture: `Balance` và `HeldBalance` cùng giảm, tiền chuyển cho bên nhận.
- Mọi thao tác trừ tiền khác của ví chỉ được dùng `AvailableBalance`.

Hold: `Held → Captured | Released`, và `Captured (ORDER_PAYMENT) → Refunded`.

## 3. Thay thế API Wallet (14–17)

| Cũ (hold Stripe) | Mới (khóa số dư) | Thay đổi chính |
|---|---|---|
| 14 `POST /internal/payments/deposit-holds` | **W1** `POST /wallets/holds` | Không redirect, không `returnUrl`/`callbackUrl`/`holdUntil`. Trả ngay `Held`. Lỗi mới `INSUFFICIENT_AVAILABLE_BALANCE` |
| 15 capture | **W2** `.../holds/{id}/capture` | Gửi `transfers` đã tính sẵn; Wallet chỉ kiểm tổng |
| 16 release | **W3** `.../holds/{id}/release` | Mở khóa nội bộ, không qua cổng |
| 17 get hold | **W4** `.../holds/{id}` và `by-reference` | Thêm tra theo `referenceId` để xử lý timeout |
| (chưa có) | **W5** `.../holds/{id}/refund-captured` | Hoàn phần cọc đã Capture khi Seller từ chối |
| (chưa có) | **W6** `GET /wallets/me`, `/wallets/me/holds` | FE hiển thị số dư khóa |

## 4. Thay đổi ở các API của Auction

### 4.1. API 01 `deposit-requirement`
Bỏ `holdValidUntil` (không còn giới hạn của cổng). Không cần thêm số dư vào response; FE lấy số dư khả dụng từ Wallet (W6). Giữ nguyên các phần còn lại.

### 4.2. API 02 tạo cọc
- **Bỏ** `returnUrl`, `paymentRedirectUrl`, `paymentExpiresAt`.
- Auction gọi **W1** đồng bộ (timeout đề xuất 5 giây), cập nhật deposit thành `Authorized` ngay.
- Response **201** `{ depositId, status: "Authorized", amount, ratePercent, policyVersion }`.
- Lỗi mới: `422 INSUFFICIENT_AVAILABLE_BALANCE` với `details: { availableBalance, required, shortfall }`. FE hiển thị số thiếu kèm nút nạp tiền.
- Lỗi cũ `HOLD_DURATION_NOT_SUPPORTED`, `PAYMENT_PROVIDER_ERROR`: bỏ. Thêm `503 WALLET_UNAVAILABLE`.
- **Timeout hoặc mất response từ Wallet:** deposit giữ `Pending`; job quét gọi `W4 by-reference`: có hold → `Authorized`; không có → gọi lại W1 cùng khóa, hoặc `Failed` sau 10 phút. Buyer gọi lại API 02 sẽ nhận bản ghi `Pending` cũ, không khóa hai lần.
- Thêm bước kiểm tra tuỳ chọn: `409 TOO_MANY_ACTIVE_DEPOSITS` (xem mục 5, dòng 7).

### 4.3. API 03 `deposits/me`
`expiresAt = null`; bỏ nhánh `DEPOSIT_EXPIRED` và `refresh=true`. Chỉ còn trạng thái đọc từ DB của Auction.

### 4.4. API 04 callback `authorization-result`
**Không dùng.** Khóa số dư là đồng bộ nên không có callback. File 04 giữ lại để tham khảo nếu sau này dùng hold của cổng.

### 4.5. API 05 đặt giá
Đơn giản hơn: chỉ kiểm cọc `Authorized` **đọc từ DB của Auction**, không còn kiểm `ExpiresAt` hay gọi Wallet xác minh. Bỏ lỗi `DEPOSIT_EXPIRED`. Giữ `DEPOSIT_REQUIRED` và `BIDDER_RESTRICTED`.

### 4.6. API 10 `order-outcome`
Chỉ đổi phần hoàn tiền. Khi `SELLER_REJECTED` hoặc `SELLER_ACCEPT_EXPIRED`:
- Commerce hoàn **phần Buyer đã trả cho order** bằng luồng hoàn ví hiện có.
- Auction hoàn **phần cọc đã Capture** bằng W5 (việc nền, `PendingAction = Refund`), cả hai khoản độc lập và idempotent riêng.

### 4.7. API 11, 12 chính sách cọc
Bỏ trường `captureMode` (chỉ còn một cách: Capture khi có kết quả thanh toán). Bỏ `holdBufferHours`.

### 4.8. API 24 hủy phiên
Giữ nguyên. Release đi qua W3.

### 4.9. API 15, mục "Hướng B"
**Hủy bỏ.**

### 4.10. Trạng thái deposit
`Expired` không còn dùng trong hướng C (không có hạn của cổng). Giữ giá trị trong enum cho tương thích, không sinh ra.
Cột `PendingAction` thêm giá trị `Refund` (đang chờ hoàn cọc sau khi Seller từ chối).

## 5. Rủi ro của hướng C và cách xử lý đơn giản

| # | Rủi ro | Cách xử lý đơn giản | Ai làm |
|---|---|---|---|
| 1 | Luồng trừ tiền khác (thanh toán bằng ví, rút) tiêu mất phần đã khóa | Mọi thao tác trừ dùng **một câu UPDATE có điều kiện** kiểm `Balance − HeldBalance ≥ số tiền`, kèm CHECK `0 ≤ HeldBalance ≤ Balance` ở DB | Wallet |
| 2 | Buyer đóng cọc nhiều phiên cùng lúc, khóa vượt số dư | Cùng câu UPDATE có điều kiện (atomic) và unique `(referenceType, referenceId)`. Không cần khóa riêng | Wallet |
| 3 | Buyer không đủ tiền để đóng cọc | Trả `INSUFFICIENT_AVAILABLE_BALANCE` kèm `shortfall`; FE hiện số thiếu và nút nạp tiền. Không làm gì thêm | Auction + FE |
| 4 | Gọi Wallet timeout, không biết đã khóa chưa | Deposit giữ `Pending`; job quét tra `W4 by-reference` rồi mới gọi lại. Không khóa hai lần nhờ unique | Auction |
| 5 | Capture, Release, hoàn bị lỗi giữa chừng | Job quét retry (Overview 4.1), các API Wallet idempotent | Auction + Wallet |
| 6 | Bug làm tiền Buyer bị khóa vĩnh viễn | Wallet cho Admin xem hold `Held` quá lâu (ví dụ quá 45 ngày) để xử lý tay; **không tự mở khóa** vì sẽ cho Buyer bid không có cọc. Auction có `NeedsAttention` ở API 13 | Wallet + Admin |
| 7 | Buyer có tiền cọc nhưng không đủ trả khi thắng nhiều phiên | Giữ như spec: Winner quá hạn mất cọc, tăng Streak. Tuỳ chọn đơn giản: `maxActiveDeposits` (mặc định không giới hạn, Admin cấu hình) → `409 TOO_MANY_ACTIVE_DEPOSITS`. **Không làm ở Giai đoạn 1** | Auction (sau) |
| 8 | Ví bị Admin đóng băng | W1 từ chối `WALLET_FROZEN`; W2, W3, W5 vẫn chạy để hệ thống tịch thu hoặc mở khóa được | Wallet |
| 9 | Seller từ chối sau khi Capture | W5 hoàn phần cọc về ví Buyer, Commerce hoàn phần còn lại, hai khoản độc lập | Auction + Commerce |
| 10 | Buyer rút tiền khi đang khóa | Lệnh rút chỉ lấy từ `AvailableBalance` (nằm trong dòng 1) | Wallet |
| 11 | Tịch thu cọc có tranh chấp pháp lý | Buyer thấy chính sách tịch thu và xác nhận ở bước 02 (`acceptedPolicyVersion`); chính sách snapshot theo version trong deposit. **Pháp lý do PO/bộ phận pháp lý xác nhận** | PO |
| 12 | Production chưa có đường nạp tiền (VNPay đã gỡ, Stripe mới test mode) | Thiết kế cọc không phụ thuộc cổng nạp. Dev/test dùng nạp thử; production chờ Wallet khôi phục nạp tiền | Wallet |

## 6. Ảnh hưởng FE

1. **Màn đóng cọc:** bỏ chuyển hướng sang cổng. Hiển thị số dư khả dụng, số cọc cần, số thiếu (nếu có) và nút nạp tiền.
2. **Màn ví:** hiển thị tổng số dư, đang khóa, khả dụng, và danh sách tiền đang khóa theo phiên (W6).
3. **Đặt giá:** bỏ xử lý `DEPOSIT_EXPIRED`.
4. **Rút tiền:** số tối đa bằng số dư khả dụng.
5. **Lưu ý cho FE:** Wallet cho biết VNPay initiation, return và IPN đã bị gỡ, nên form nạp VNPay đã nối ở báo cáo FE Phase 2 sẽ không hoạt động. Nên kiểm tra lại với Wallet.

## 7. Việc còn mở

1. **Pháp lý/PO:** xác nhận điều khoản tịch thu cọc và việc nới nguyên tắc "không lấy cọc từ ví".
2. **Wallet:** cam kết thời gian và trả lời 6 câu ở cuối `WALLET_REPLY_AND_API_REQUEST.md`.
3. **Tỷ lệ chia tịch thu** Seller và Platform (Admin cấu hình, Auction tính).
4. Các điểm mở cũ ở Overview 7.3 vẫn còn: xác nhận cọc người về nhì, thời hạn trả lời lời mời, Admin force-cancel khi đã có Winner, cache hồ sơ User, đối chiếu tên enum/path.

## 8. Phân giai đoạn cập nhật

Interface client đổi tên thành `IWalletHoldClient` với 5 hàm: `Hold`, `Release`, `Capture`, `Get` (cả by-reference), `RefundCaptured`. Bản fake giữ số dư trong bộ nhớ, đủ để test.

| Giai đoạn | Việc của Auction |
|---|---|
| **1** | Bảng `AuctionDeposits`, API **01, 02, 05**, đọc chính sách (11), fake client. **Bỏ API 04.** Cần W1, W3, W4 thật khi Wallet sẵn sàng |
| **2** | API **06, 10, 24**, job kết thúc phiên, job quét, W2, W5, nối Commerce (18) và Catalog (23) |
| **3** | API **07, 08**, nối User thật (19, 20, 25), `maxActiveDeposits` nếu cần |
