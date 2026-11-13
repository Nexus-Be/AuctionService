# Phản hồi team Wallet và yêu cầu API cho tiền cọc đấu giá

> Từ: team Auction · Gửi: team Wallet · Phiên bản: `v0.4`
> Cảm ơn team đã đối chiếu code và trả lời chi tiết. Dưới đây là hướng Auction đã chốt, các API cần Wallet cung cấp, và vài câu hỏi cuối cùng. Toàn bộ path và tên lỗi là đề xuất, Wallet có thể đổi cho khớp quy ước hiện có.

---

## 1. Auction đã chốt

| # | Chủ đề | Quyết định |
|---|---|---|
| 1 | Nguồn tiền cọc | **Khóa số dư trong ví Nexus** (bút toán nội bộ). Không dùng hold/authorization của Stripe, không chuyển Buyer sang cổng thanh toán khi đóng cọc. |
| 2 | Lý do bỏ hold Stripe | Authorization tối đa ~7 ngày không đủ cho phiên dài 168 giờ cộng 24 giờ thanh toán, và extended authorization chưa chắc dùng được. Khóa số dư không có giới hạn thời gian và không cần refund Stripe. |
| 3 | Callback Wallet → Auction | **Không cần.** Khóa số dư là đồng bộ (Auction gọi, Wallet trả kết quả ngay). Phần việc nền (Capture, Release, hoàn) Auction tự retry bằng job quét; Wallet không phải gọi sang Auction. |
| 4 | Hoàn tiền | **Về ví Nexus**, không hoàn về thẻ. Người thua: mở khóa. Seller từ chối sau khi Buyer đã trả: hoàn phần cọc đã Capture về ví Buyer. |
| 5 | Phương thức thanh toán | Cọc chỉ dùng ví Nexus. Cách Buyer nạp tiền vào ví là việc riêng, không ảnh hưởng thiết kế cọc. |
| 6 | VNPay | Chưa cần cho cọc. Khi khôi phục VNPay cho việc nạp tiền thì cọc không phải sửa gì. |
| 7 | Chia khoản tịch thu | Auction tính số tiền cho từng bên (theo tỷ lệ Admin cấu hình), Wallet chỉ **thực thi danh sách chuyển tiền** gửi sang. Wallet không cần lưu cấu hình tỷ lệ. |

Phản hồi các điểm Wallet đã hỏi: Extended authorization không còn cần. Chính sách hoàn tiền: mở khóa không mất phí; hoàn phần cọc đã Capture về ví Nexus. Retry callback: không áp dụng như mục 3.

---

## 2. Mô hình số dư đề xuất

```
Balance          tổng số dư của ví
HeldBalance      tổng các khoản đang bị khóa (hold ở trạng thái Held)
AvailableBalance = Balance − HeldBalance
```

**Bất biến bắt buộc:** `0 ≤ HeldBalance ≤ Balance`. Nên có ràng buộc ở tầng DB (CHECK) bên cạnh kiểm tra trong code.

**Quy tắc quan trọng nhất:** mọi thao tác **trừ tiền** của ví (thanh toán đơn bằng ví, tạo lệnh rút, chuyển tiền, khóa cọc mới) phải kiểm tra theo `AvailableBalance`, không phải `Balance`. Nếu bỏ sót một luồng, Buyer có thể tiêu hoặc rút đúng phần tiền đang khóa.

Cách làm đơn giản để tránh tranh chấp: dùng **một câu cập nhật có điều kiện duy nhất** cho mọi thao tác trừ, ví dụ:
```sql
UPDATE Wallets SET Balance = Balance - @amount
WHERE Id = @id AND (Balance - HeldBalance) >= @amount
```
Số dòng bị ảnh hưởng bằng 0 nghĩa là không đủ khả dụng. Với khóa cọc: `SET HeldBalance = HeldBalance + @amount WHERE ... (Balance - HeldBalance) >= @amount`. Cách này an toàn khi Buyer đóng cọc nhiều phiên cùng lúc.

**Vòng đời hold:** `Held → Captured` · `Held → Released` · `Captured (ORDER_PAYMENT) → Refunded`.

Ví dụ: ví 1.000.000, ba phiên khóa 200.000, 150.000, 300.000 → `HeldBalance = 650.000`, `AvailableBalance = 350.000`. Phiên thứ tư cần cọc 400.000 sẽ bị từ chối vì thiếu 50.000.

---

## 3. API cần Wallet cung cấp

W1–W5 là API chuyển tiền do service gọi, **không dành cho JWT của người dùng cuối**. Path không có tiền tố `internal`, việc bảo vệ dựa vào header `X-Internal-Service-Key` (thiếu hoặc sai → 401), giống cách Commerce đang dùng cho settlement. Nhờ Wallet kiểm header này ở cả 5 API (hoặc dùng cơ chế phân quyền service khác nếu Wallet đã có). Mọi API ghi đều có `Idempotency-Key`. W6 dùng JWT của người dùng như các API ví hiện có. Tiền là số nguyên VND, thời gian ISO 8601 UTC. Lỗi dùng envelope `{ "code", "message", "details", "traceId" }` (đối chiếu envelope hiện có của Wallet).

| # | API | Mục đích | Ưu tiên |
|---|---|---|---|
| W1 | `POST /api/v1/wallets/holds` | Khóa số dư làm cọc | **P1** |
| W3 | `POST /api/v1/wallets/holds/{holdId}/release` | Mở khóa (Buyer không mất cọc) | **P1** |
| W4 | `GET /api/v1/wallets/holds/{holdId}` và `.../holds/by-reference/{referenceType}/{referenceId}` | Tra trạng thái hold | **P1** |
| W2 | `POST /api/v1/wallets/holds/{holdId}/capture` | Chuyển tiền đã khóa (tính vào giá thắng hoặc tịch thu) | P2 |
| W5 | `POST /api/v1/wallets/holds/{holdId}/refund-captured` | Hoàn phần cọc đã Capture về ví Buyer | P2 |
| W6 | `GET /api/v1/wallets/me` (mở rộng) và `GET /api/v1/wallets/me/holds` | FE hiển thị số dư khóa theo phiên | P3 |

Thay đổi trên API hiện có (không phải API mới): xem mục 4.

### W1 · Khóa số dư

`Idempotency-Key: hold:{depositId}`

**Request**
```json
{
  "ownerUserId": "…",
  "referenceType": "AUCTION_DEPOSIT",
  "referenceId": "<depositId>",
  "amount": 50000,
  "currency": "VND",
  "metadata": { "auctionId": "…", "policyVersion": 3 }
}
```

**Response 201 (happy case)**
```json
{
  "holdId": "…",
  "status": "Held",
  "amount": 50000,
  "balance": 1000000,
  "heldBalance": 200000,
  "availableBalance": 800000,
  "createdAt": "2026-11-13T10:00:00Z"
}
```
Gọi lại cùng `referenceId` với cùng số tiền và chủ ví: **200** trả lại hold cũ, không khóa thêm.

**Lỗi**
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu trường, `amount ≤ 0` |
| 401 | `INTERNAL_KEY_INVALID` | Thiếu hoặc sai key |
| 403 | `WALLET_FROZEN` | Ví bị Admin đóng băng; **không** khóa mới |
| 404 | `WALLET_NOT_FOUND` | Người dùng chưa có ví |
| 409 | `HOLD_REFERENCE_CONFLICT` | Cùng `referenceId` nhưng khác số tiền hoặc chủ ví |
| 422 | `INSUFFICIENT_AVAILABLE_BALANCE` | Không đủ khả dụng. `details`: `availableBalance`, `required`, `shortfall` |
| 503 | `WALLET_UNAVAILABLE` | Auction sẽ tra lại bằng W4 rồi retry |

Ràng buộc duy nhất: `(referenceType, referenceId)`. Không khóa một phần (đủ hoặc không).

### W2 · Capture

`Idempotency-Key: capture:{depositId}`

**Request**
```json
{
  "purpose": "FORFEIT",
  "orderId": null,
  "transfers": [
    { "toType": "SELLER_WALLET", "toOwnerId": "…", "amount": 35000 },
    { "toType": "PLATFORM",      "toOwnerId": null, "amount": 15000 }
  ]
}
```
- `purpose = ORDER_PAYMENT` (Winner đã trả, cọc thành một phần giá thắng): bắt buộc `orderId`, đúng một transfer `toType = SYSTEM_ESCROW` (ví hệ thống, gắn `orderId`; Commerce phân phối cho Seller khi order `COMPLETED` như luồng hiện có).
- `purpose = FORFEIT` (Winner quá hạn): `transfers` do Auction tính theo tỷ lệ Admin cấu hình.
- Tổng `transfers.amount` **phải bằng** số tiền hold. Không Capture một phần.

**Xử lý:** trong **một transaction**: `HeldBalance` và `Balance` của Buyer cùng giảm, ghi có cho từng bên nhận, ghi sổ cái tham chiếu `holdId`. Tất cả hoặc không gì.

**Response 200 (happy case)**
```json
{
  "holdId": "…", "status": "Captured", "purpose": "FORFEIT",
  "capturedAt": "2026-11-14T10:00:05Z",
  "transfers": [ { "toType": "SELLER_WALLET", "amount": 35000, "ledgerRef": "…" } ],
  "buyerBalanceAfter": 950000
}
```
Gọi lại cùng `purpose` và `transfers`: **200** trả kết quả cũ.

**Lỗi**
| HTTP | code | Khi nào |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Thiếu `orderId` (ORDER_PAYMENT) hoặc `transfers` |
| 404 | `HOLD_NOT_FOUND` | |
| 409 | `HOLD_ALREADY_CAPTURED_DIFFERENT` | Đã Capture với `purpose` hoặc `transfers` khác (chống Capture hai lần) |
| 409 | `HOLD_ALREADY_RELEASED` | |
| 422 | `TRANSFERS_SUM_MISMATCH` | Tổng khác số tiền hold |
| 422 | `RECIPIENT_NOT_FOUND` | Ví Seller/Platform không tồn tại |
| 503 | `WALLET_UNAVAILABLE` | Auction retry |

Ví bị đóng băng: Capture **vẫn được phép** (hệ thống cần tịch thu/chuyển được).

### W3 · Release

`Idempotency-Key: release:{depositId}`

**Request:** `{ "reason": "AUCTION_ENDED_NOT_WINNER" }`
`reason`: `AUCTION_ENDED_NOT_WINNER` · `AUCTION_CANCELLED` · `AUCTION_FAILED` · `ADMIN_FORCE_CANCEL` (chỉ để ghi log).

**Response 200:** `{ "holdId": "…", "status": "Released", "releasedAt": "…", "availableBalance": 850000 }`. Hold đã `Released` thì 200 kết quả cũ.

| HTTP | code | Khi nào |
|---|---|---|
| 404 | `HOLD_NOT_FOUND` | Auction coi như đã mở khóa |
| 409 | `HOLD_ALREADY_CAPTURED` | Không Release được; Auction cảnh báo Admin |
| 503 | `WALLET_UNAVAILABLE` | Auction retry |

Ví bị đóng băng: Release **vẫn được phép**.

### W4 · Tra hold

`GET .../holds/{holdId}` hoặc `GET .../holds/by-reference/AUCTION_DEPOSIT/{depositId}`

**Response 200**
```json
{
  "holdId": "…", "ownerUserId": "…",
  "referenceType": "AUCTION_DEPOSIT", "referenceId": "…",
  "amount": 50000, "status": "Held", "capturePurpose": null,
  "createdAt": "…", "resolvedAt": null
}
```
`status`: `Held` · `Captured` · `Released` · `Refunded`. Lỗi: 404 `HOLD_NOT_FOUND`, 401, 503.
Route `by-reference` là đoạn cố định; nên ràng buộc `{holdId}` kiểu GUID để hai route không đụng nhau.
Bản `by-reference` quan trọng: khi Auction gọi W1 bị timeout mà không biết Wallet đã khóa hay chưa, Auction dùng nó để biết kết quả thay vì khóa lần hai.

### W5 · Hoàn phần cọc đã Capture

`Idempotency-Key: refund:{depositId}`

Dùng khi Seller từ chối hoặc quá hạn chấp nhận **sau khi** Buyer đã trả. Commerce hoàn phần Buyer đã trả cho order bằng luồng hoàn ví hiện có; Auction gọi W5 để hoàn **phần cọc** nên Wallet không phải tự gộp hai khoản.

**Request:** `{ "reason": "SELLER_REJECTED", "orderId": "…" }`
**Xử lý:** chỉ cho hold `Captured` với `purpose = ORDER_PAYMENT`; chuyển từ `SYSTEM_ESCROW` của `orderId` về ví Buyer; hold → `Refunded`.

**Response 200:** `{ "holdId": "…", "status": "Refunded", "refundedAmount": 50000, "buyerBalanceAfter": 1000000 }`. Gọi lại: 200 kết quả cũ.

| HTTP | code | Khi nào |
|---|---|---|
| 404 | `HOLD_NOT_FOUND` | |
| 409 | `HOLD_NOT_REFUNDABLE` | Hold chưa Capture, hoặc là `FORFEIT` (tiền tịch thu không hoàn bằng API này) |
| 409 | `ESCROW_INSUFFICIENT` | Escrow của order đã bị phân phối cho Seller |
| 503 | `WALLET_UNAVAILABLE` | Auction retry |

### W6 · Hiển thị cho Buyer (FE)

- `GET /api/v1/wallets/me` trả thêm `heldBalance` và `availableBalance`.
- `GET /api/v1/wallets/me/holds?status=Held` trả danh sách `{ holdId, referenceType, referenceId, auctionId, amount, createdAt }` để Buyer thấy tiền đang khóa ở phiên nào. Có `auctionId` nhờ `metadata` lúc tạo hold.

---

## 4. Thay đổi trên API và nghiệp vụ hiện có của Wallet

1. **Mọi luồng trừ tiền dùng `AvailableBalance`**: thanh toán đơn bằng ví, tạo lệnh rút, chuyển tiền, và bất kỳ luồng trừ nào khác. Đề nghị Wallet liệt kê các luồng này để cùng rà soát.
2. **Lệnh rút:** chỉ rút từ phần khả dụng. Flow 12 có nói lệnh rút "giữ số dư" và từ chối thì "trả lại số dư đã giữ". Nếu Wallet đã có cơ chế giữ số dư cho lệnh rút thì có thể **dùng chung** với hold cọc, chỉ thêm loại tham chiếu.
3. **Ví hệ thống/Platform:** cần có ví (hoặc tài khoản sổ cái) cho `SYSTEM_ESCROW` theo `orderId` và cho `PLATFORM`. Nếu chưa có, cần tạo.
4. **Đóng băng ví (Admin):** chặn W1, nhưng cho phép W2, W3, W5.

---

## 5. Auction sẽ tự lo (Wallet không phải làm)

- **Retry:** job quét phía Auction gọi lại W2, W3, W5 khi lỗi, backoff 1, 2, 5, 10, 30 phút, tối đa 10 lần rồi cảnh báo Admin. Vì vậy W2, W3, W5 **phải idempotent**.
- **Timeout khi khóa:** Auction tra W4 theo `referenceId` trước khi gọi lại W1.
- **Thời hạn:** không có hạn cho hold. Auction chịu trách nhiệm Release hoặc Capture từng hold; Wallet không tự mở khóa.
- **Tính số tiền** cọc, tỷ lệ chia tịch thu và `transfers`. Wallet chỉ kiểm tổng bằng số hold.
- **Giới hạn thời gian phản hồi mong muốn:** W1 và W4 p95 dưới 500 ms (Auction gọi đồng bộ khi Buyer bấm đóng cọc).

---

## 6. Câu hỏi cần Wallet trả lời

1. Wallet **hiện giữ số dư cho lệnh rút** bằng cách nào (trừ thẳng, hay có cột/bảng "đang giữ")? Có tái dùng được cho hold cọc không?
2. Các luồng **trừ tiền** hiện có là những luồng nào? Wallet có thể chuyển hết sang kiểm `AvailableBalance` không?
3. **Ví hệ thống/Platform** và `SYSTEM_ESCROW` theo order đã có chưa? Nếu chưa, ghi sổ thế nào cho Capture ORDER_PAYMENT?
4. Hiện Buyer **nạp tiền vào ví** bằng đường nào ở môi trường test/dev (Stripe test)? Auction cần một cách nạp tiền thử để test cọc.
5. **Mốc thời gian:** Wallet dự kiến khi nào có W1, W3, W4 (P1), để Auction nối thật ở Giai đoạn 1 thay vì dùng bản fake? W2 và W5 (P2) và W6 (P3) khi nào?
6. Có cần đổi tên path, mã lỗi hoặc envelope để khớp quy ước Wallet hiện có không?

Trong lúc chờ, Auction làm trước phía mình với bản giả lập của Wallet (cùng contract ở trên), nên Wallet không bị chặn ai. Khi Wallet sẵn sàng chỉ cần đổi sang bản thật.
