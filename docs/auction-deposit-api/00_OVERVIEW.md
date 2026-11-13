# Auction Deposit – Tổng quan API & luồng nghiệp vụ

> **v0.4: đã chốt hướng C (giữ cọc bằng số dư ví Nexus). Đọc `26_UPDATE_v0.4_option-C.md` trước; các file 02, 03, 04, 05, 10, 14–17 có banner ghi rõ phần đã đổi.**
>
> Phiên bản gốc: `v0.3-draft` (đã cập nhật theo các quyết định ở mục 7.1; phần Wallet vẫn chờ trả lời) · Dựa trên: `AUCTION_DEPOSIT_SPEC v0.1`, `Usecase Flow 4 & 12`, báo cáo FE Phase 2, và câu trả lời của team (mục 1).
> Mọi path/enum/mã lỗi dưới đây là **đề xuất**, cần đối chiếu với code hiện có trước khi chốt.

## 1. Quyết định từ câu trả lời của team

| # | Câu hỏi | Quyết định dùng để thiết kế |
|---|---|---|
| 1 | Hold của Gateway giữ được bao lâu | Hạn thanh toán 24h **do Commerce/Wallet quản lý, Auction không xử lý**. Auction chỉ gọi Wallet/Payment để Authorize/Capture/Release. Giới hạn thời gian Hold của nhà cung cấp cổng vẫn là rủi ro, xem mục 7. |
| 2 | Ai sở hữu tích hợp Gateway | *(Bạn ghi "không hiểu", giải thích ở mục 2.)* **Giả định: Wallet/Payment service sở hữu.** Auction chỉ gọi client nội bộ. |
| 3 | Ai lưu Violation Streak | **User service** sở hữu. |
| 4 | Người về nhì nhận rồi không trả | **Tăng Streak + trừ uy tín**, không phạt tiền. |
| 5 | Hold của người về nhì | Người về nhì **có hạn thanh toán như người thắng (24h)**. Giả định: cọc của họ đã Release lúc phiên kết thúc, họ trả **toàn bộ** giá. |
| 6 | Cách hoàn cọc khi Seller từ chối | *(Chưa trả lời)* Commerce/Wallet hoàn, Auction chỉ nhận callback. |
| 7 | Chia cọc bị tịch thu | *(Chưa trả lời)* Admin cấu hình % Seller/Platform, chuyển qua Wallet. |
| 8 | Cọc tối thiểu | *(Chưa trả lời)* Thêm `minDepositAmount` vào policy. |
| 9 | Close / Admin Review | *(Chưa trả lời)* Chỉ làm `Failed` + Relist. |

## 2. Giải thích câu 2 (ai sở hữu tích hợp Gateway)

"Gateway" ở đây là **cổng thanh toán bên ngoài** (VNPay...), không phải API Gateway của hệ thống. Ai đó phải là service duy nhất nói chuyện với cổng đó (tạo Hold, Capture, Release, nhận webhook, giữ khóa bí mật). Nếu Auction, Commerce và Wallet đều tích hợp riêng sẽ trùng code, trùng secret và khó đối soát. Đề xuất: **Wallet/Payment service là chủ**, Auction và Commerce chỉ gọi API nội bộ của Wallet. Tài liệu này theo hướng đó; nếu team chọn service khác thì chỉ đổi base URL của nhóm API 14–17.

## 3. Danh sách API (mỗi API một file)

| File | API | Service chủ | Loại |
|---|---|---|---|
| 01 | `GET /auctions/{id}/deposit-requirement` | Auction | MỚI |
| 02 | `POST /auctions/{id}/deposits` | Auction | MỚI |
| 03 | `GET /auctions/{id}/deposits/me` | Auction | MỚI |
| 04 | `POST /internal/deposits/{depositId}/authorization-result` | Auction | MỚI (callback) |
| 05 | `POST /auctions/{id}/bids` | Auction | SỬA |
| 06 | `GET /auctions/{id}/result` | Auction | SỬA |
| 07 | `POST /auctions/{id}/second-chance/accept` | Auction | MỚI |
| 08 | `POST /auctions/{id}/second-chance/decline` | Auction | MỚI |
| 09 | `GET /auctions/{id}/second-chance/me` | Auction | MỚI |
| 10 | `POST /internal/auctions/{id}/order-outcome` | Auction | MỚI (callback từ Commerce) |
| 11 | `GET /admin/auction-configs/deposit-policy` | Auction | MỚI |
| 12 | `PUT /admin/auction-configs/deposit-policy` | Auction | MỚI |
| 13 | `GET /admin/auctions/{id}/deposits` | Auction | MỚI |
| 14 | `POST /internal/payments/deposit-holds` | Wallet/Payment | MỚI (Auction gọi) |
| 15 | `POST /internal/payments/deposit-holds/{holdId}/capture` | Wallet/Payment | MỚI (Auction gọi) |
| 16 | `POST /internal/payments/deposit-holds/{holdId}/release` | Wallet/Payment | MỚI (Auction gọi) |
| 17 | `GET /internal/payments/deposit-holds/{holdId}` | Wallet/Payment | MỚI (Auction gọi) |
| 18 | `POST /auction-orders/settlement` | Commerce | SỬA (Auction gọi) |
| 19 | `GET /internal/users/{userId}/auction-deposit-profile` | User | MỚI (Auction gọi) |
| 20 | `POST /internal/users/{userId}/auction-violations` | User | MỚI (Auction gọi) |
| 21 | `GET /admin/users/{userId}/auction-violations` | User | MỚI |
| 22 | `POST /admin/users/{userId}/auction-violations/adjust` | User | MỚI |
| 23 | `POST /internal/inventory/auction-holds/{auctionId}/release` | Catalog | KIỂM TRA (có thể đã tồn tại) |
| 24 | Seller cancel + Admin force-cancel | Auction | SỬA |
| 25 | `POST /internal/users/{userId}/auction-payments` | User | MỚI (Auction gọi, reset Streak) |

Notification (mail/thông báo) chưa phân tích vì chưa rõ API hiện có. Các thông báo cần gửi: đã đặt cọc, cọc bị Release, cọc bị Capture/tịch thu, lời mời người về nhì, người về nhì hết hạn, cảnh báo Streak/hạn chế.

## 4. Quy ước chung

- **Tiền:** số nguyên VND (`long`), `currency: "VND"`. **Thời gian:** ISO 8601 UTC. **ID:** GUID.
- **Auth người dùng:** JWT qua Gateway. **Auth nội bộ:** header `X-Internal-Service-Key` **bắt buộc** (thiếu hoặc sai → 401). Không lặp lại lỗi P0 của `ReceiveSettlement`. Nhóm `/api/v1/internal/**` không được route công khai ra Internet.
- **Error envelope đề xuất** (đối chiếu envelope hiện có):
```json
{ "code": "DEPOSIT_REQUIRED", "message": "…", "details": { }, "traceId": "…" }
```
- **Quy ước HTTP:** 400 sai định dạng · 401 chưa xác thực/sai key · 403 không đủ quyền hoặc bị hạn chế · 404 không tồn tại · 409 sai trạng thái/xung đột · 412 `If-Match` không khớp · 422 vi phạm quy tắc nghiệp vụ · 502/503 service phụ thuộc lỗi.
- **Idempotency:** API ghi từ user dùng `Idempotency-Key`. API nội bộ dùng khoá nghiệp vụ (xem từng file). Request lặp trả **đúng kết quả cũ**, không tạo bản ghi mới.
- **Vì không có event/RabbitMQ:** không dùng bảng Outbox/Inbox. Lời gọi ra ngoài có hậu quả (Capture, Release, Settlement, Violation, nhả kho, reset Streak) được theo dõi bằng **cột trạng thái trên bản ghi nghiệp vụ** và một **job quét** gọi lại việc còn dang dở. Chi tiết ở mục 4.1.

### 4.1. Cơ chế retry thay cho Outbox/Inbox

**Nguyên tắc:** chuyển trạng thái và đặt "việc cần làm" nằm trong **cùng một transaction**; gọi service ngoài **sau** transaction (fast path), lỗi thì job quét gọi lại.

| Bản ghi | Cột việc-nền | Giá trị |
|---|---|---|
| Deposit | `PendingAction` | `None` · `Release` · `Capture` |
| Deposit | `PendingPurpose` / `PendingReason` | `OrderPayment` · `Forfeit` / lý do Release |
| Auction | `ViolationState`, `StreakResetState`, `StockReleaseState` | `None` · `Pending` · `Done` |
| Offer | `SettlementState` | `None` · `Pending` · `Done` |
| Mọi bản ghi trên | `AttemptCount`, `NextRetryAt`, `LastError`, `NeedsAttention` | theo dõi retry |

**Quy trình:**
1. Nhận sự kiện (API 10, hủy phiên, job kết thúc phiên): trong một transaction đổi trạng thái chính và đặt các cột `Pending`. Không gọi service ngoài trong transaction.
2. Sau commit, gọi thử ngay (fast path) để không phải chờ job.
3. **Job quét** mỗi 1 phút: lấy bản ghi `Pending` có `NextRetryAt <= now` và `NeedsAttention = false`, gọi API đích bằng khoá idempotent, thành công → `Done`.
4. Lỗi → `AttemptCount + 1`, backoff 1, 2, 5, 10, 30 phút. Quá 10 lần → `NeedsAttention = true`, cảnh báo Admin (hiện ở API 13).
5. Mỗi việc nền **độc lập**: lỗi một việc (vd ghi Streak) không chặn việc khác (vd nhả kho).
6. Nhiều instance chạy cùng lúc: khoá hàng khi lấy việc (`SKIP LOCKED` hoặc `UPDLOCK, READPAST`, tuỳ DB) để không gọi trùng.

**Chống callback trùng (thay Inbox):** (a) bảng chuyển trạng thái hợp lệ, xử lý lại không đổi gì; (b) unique `(orderId, outcome)` cho callback API 10; (c) `LastGatewayEventId` trên deposit cho callback API 04.

**Điều kiện bắt buộc:** mọi API đích phải idempotent theo khoá Auction gửi: 15 Capture, 16 Release, 18 Settlement, 20 Violation, 23 Nhả kho, 25 Reset Streak. Các file API đã ghi rõ khoá.

## 5. Mô hình trạng thái

**Deposit:** `Pending → Authorized → (Released | Captured | Expired)`, hoặc `Pending → Failed`. Đề xuất thêm cột: `CapturePurpose` (`OrderPayment` | `Forfeit`), `ReleaseReason`, `PolicyVersion`, `RatePercent`, `AttemptCount`.

**Phiên (đề xuất enum, đối chiếu code):** `SCHEDULED → ACTIVE → ENDED (chờ Winner trả) → SETTLED`. Nhánh phụ: `ENDED → SECOND_CHANCE → SETTLED`, và `FAILED`, `CANCELLED`.

**Lời mời người về nhì:** `Offered → Accepted → Paid`, hoặc `Declined`, `Expired`, `PaymentExpired`.

## 6. Luồng tổng thể

1. **Đóng cọc:** FE gọi 01 → 02. API 02 gọi 19 (hồ sơ User) rồi 14 (Authorize) → Buyer sang cổng thanh toán → Wallet gọi callback 04 → `Authorized`.
2. **Bid:** API 05 kiểm cọc + hạn chế, rồi nghiệp vụ bid cũ.
3. **Kết thúc phiên (job):** chốt Winner, đặt việc nền Release cọc mọi bidder khác (16), gọi 18 với `depositAmount`. Không có bid → `FAILED`, Release hết, nhả kho (23).
4. **Winner trả đúng hạn:** Commerce callback 10 (`PAID`) → phiên `SETTLED`; việc nền: Capture cọc `OrderPayment` (15), báo User reset Streak (25).
5. **Winner quá hạn:** Commerce callback 10 (`PAYMENT_EXPIRED`) → trong một transaction tạo lời mời người về nhì (hoặc `FAILED` nếu không có); việc nền: Capture `Forfeit` (15), ghi vi phạm (20), nhả kho (23) chỉ khi `FAILED`.
6. **Người về nhì:** chấp nhận (07) → gọi 18 (`SECOND_CHANCE`, `depositAmount = 0`) → Commerce tạo order 24h. Trả đúng hạn → reset Streak (25). Từ chối/hết hạn (08) → `FAILED`, không phạt. Nhận rồi không trả → callback 10 → ghi vi phạm (20) → `FAILED`, nhả kho.
7. **Seller từ chối/quá hạn chấp nhận:** Commerce hoàn tiền, callback 10, Auction ghi nhận và nhả kho. Cách hoàn cọc chờ Wallet.
8. **Hủy phiên:** Seller/Admin hủy (24) → Release toàn bộ cọc.

## 7. Quyết định đã chốt và việc còn mở

### 7.1. Đã chốt (team đồng ý)
| # | Chủ đề | Quyết định |
|---|---|---|
| 1 | Hold hết hạn | **(Đã thay ở v0.4: dùng khóa số dư ví, hết vấn đề hold, bỏ hướng B.)** Giai đoạn 1 theo spec (hướng A, `captureMode = ON_RESOLUTION`) với Wallet giả lập. Chờ Wallet xác nhận hold. Nếu cổng không hỗ trợ hold hoặc giữ dưới ~8 ngày → chuyển hướng B (`ON_WINNER_DETERMINED`: Capture ngay khi chốt Winner), chi tiết ở file 15 mục 6. |
| 2 | Kho | Commerce **không nhả kho** order đấu giá khi hết hạn, chỉ hủy order và callback. **Auction quyết định**: có người về nhì thì giữ/chuyển kho, không còn ai thì gọi Catalog nhả (23). |
| 3 | Reset Streak | Reset về 0 khi Buyer **thanh toán đúng hạn** một phiên thắng. Thêm API 25 bên User. Reset không tự gỡ hạn chế đang hiệu lực. |
| 4 | Mất đồng bộ liên service | Dùng **cột trạng thái + job quét** thay Outbox/Inbox (mục 4.1) và unique index chống callback trùng. |
| 5 | P0 Commerce | API 18 bắt buộc `X-Internal-Service-Key`, vá `ReceiveSettlement` cùng lúc. |

### 7.2. Đang chờ Wallet trả lời
- Ai là chủ tích hợp cổng thanh toán (giả định Wallet).
- Cổng có hỗ trợ hold không, giữ tối đa bao lâu (cần ≥ ~8 ngày).
- Phương án dự phòng thu thật rồi hoàn (`CHARGE_AND_REFUND`): hoàn về đâu, bao lâu, phí.
- 4 API 14–17, callback authorization, chia tiền tịch thu cho Seller/Platform, cách hoàn cọc khi Seller từ chối.

Trong lúc chờ: làm sau interface `IPaymentHoldClient` với bản fake. Khi Wallet trả lời chỉ thay bản thật, và đổi `captureMode` nếu cần.

### 7.3. Còn mở khác
1. Người về nhì: giả định cọc đã Release lúc kết thúc và họ trả toàn bộ giá. Cần xác nhận lại.
2. Thời hạn trả lời lời mời người về nhì (đề xuất 24h, là cấu hình mới).
3. Admin force-cancel khi đã có Winner đang chờ thanh toán: hủy order, hoàn tiền, xử lý cọc Winner chưa quy định.
4. Người về nhì không có cọc nên hình phạt chỉ là Streak + uy tín. Muốn mạnh hơn phải giữ Hold của họ.
5. Cache hồ sơ User khi bid (đề xuất 30–60 giây), chấp nhận trễ ngắn khi mới bị hạn chế.
6. Tên enum, path, mã lỗi ở mọi file cần đối chiếu code hiện có.

## 8. Phân giai đoạn triển khai (phía Auction)

Tạo sẵn 4 interface client để chạy được khi service khác chưa xong: `IPaymentHoldClient` (14–17), `IUserProfileClient` (19, 20, 25), `ICommerceSettlementClient` (18), `ICatalogStockClient` (23). Mỗi interface có bản fake.

| Giai đoạn | Mục tiêu | Việc của Auction |
|---|---|---|
| **1** | Mở khóa nút bid cho FE | Bảng `AuctionDeposits`, model chính sách cọc, API **01, 02, 04, 05**, đọc chính sách (11), các client fake |
| **2** | Kết thúc phiên và thanh toán | API **06, 10, 24**, job kết thúc phiên, job quét (mục 4.1), Capture/Release thật, nối Commerce (18) và Catalog (23) |
| **3** | Người về nhì và Streak | API **07, 08**, nối User thật (19, 20, 25), `NeedsAttention` cho Admin |

**Có thể hoãn hoặc gộp:** API **03** (API 01 đã trả `existingDeposit`), API **09** (gộp vào 06 vì `secondChance` đã nằm trong response), API **12** (dùng `auction-configs` có sẵn, chưa cần màn sửa), API **13** (đối soát cho Admin).

## 9. Lịch sử thay đổi

**v0.3** (theo quyết định ở mục 7.1)
- Mục 4 và 4.1: thay Outbox/Inbox bằng cột trạng thái + job quét. Cập nhật các file 04, 07, 08, 10, 13, 15, 16, 18, 20, 23, 24.
- API 10: lời mời người về nhì tạo ngay trong transaction; Capture, ghi vi phạm, nhả kho chạy nền. Thêm gọi API 25 khi `PAID`.
- API 11, 12: thêm `captureMode`. API 15: thêm mục 6 "Hướng B".
- Thêm file **25** (reset Streak). Cập nhật mục 6, 7, thêm mục 8.

**v0.4** (hướng C, sau khi Wallet trả lời)
- Chốt khóa cọc bằng số dư ví Nexus thay vì hold Stripe. Wallet cho biết Stripe chỉ ở test mode, VNPay đã gỡ, hold tối đa ~7 ngày, và chưa có API hold/capture/release.
- Thay API 14–17 bằng W1–W6 (file `WALLET_REPLY_AND_API_REQUEST.md`). API 04 không dùng. Bỏ hướng B và `captureMode`.
- Thêm file **26** (cập nhật v0.4, rủi ro của hướng C và cách xử lý).
