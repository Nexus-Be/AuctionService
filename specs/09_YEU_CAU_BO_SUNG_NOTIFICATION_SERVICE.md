# Tài liệu phối hợp bổ sung Thông báo Đấu giá (Gửi team NotificationService & Gateway)

Tài liệu này tổng hợp các luồng thông báo mà `AuctionService` vừa bổ sung thêm để khép kín vòng đời phiên đấu giá (từ lúc Seller tạo phiên chờ duyệt, Staff duyệt phiên, phiên bắt đầu mở bán, đến lúc chốt kết quả và mời người về nhì).

Nhờ bạn phụ trách `NotificationService` (và `ApiGateway`) kiểm tra và bổ sung giúp các mục dưới đây để thông báo đẩy về chuông của người dùng mượt 100%.

---

## 1. Luồng tạo phiên chờ duyệt & Staff duyệt phiên (Qua cổng `/api/v1/notifications/in-app`)

Khi Seller vừa bấm tạo phiên đấu giá (trạng thái `PendingApproval`) và khi Staff/Admin bấm **Duyệt phiên** (chuyển từ `PendingApproval` sang `Scheduled`), `AuctionService` sẽ bắn request sang cổng có sẵn của `NotificationService`:

- **Endpoint:** `POST /api/v1/notifications/in-app`
- **Header xác thực:** `Authorization: Bearer <Service_JWT_Token>` (cùng loại token mà `AuctionService` đang dùng để gọi các cổng `/api/v1/notifications/auction-*`).

### Payload `AuctionService` gửi sang:

#### A. Khi Seller vừa tạo phiên mới / tạo lại phiên (Đang chờ duyệt)
```json
{
  "sourceEventId": "3f4637a8-152f-4353-ad40-adb765add545",
  "userId": "22222222-2222-2222-2222-222222222222",
  "title": "Phiên đấu giá đang chờ duyệt",
  "body": "Phiên đấu giá \"Đồng hồ Seiko cổ điển\" (lịch mở bán 14:00 28/11/2026) đã được tạo thành công và đang chờ nhân viên sàn phê duyệt.",
  "notificationType": "AuctionStarted",
  "payload": "{\"type\":\"AuctionStarted\",\"auctionId\":\"11111111-1111-1111-1111-111111111111\",\"audience\":\"seller\",\"status\":\"PendingApproval\"}"
}
```

#### B. Khi Staff/Admin bấm Duyệt phiên (`PendingApproval -> Scheduled`)
```json
{
  "sourceEventId": "7a8912bc-452f-4353-ad40-adb765add999",
  "userId": "22222222-2222-2222-2222-222222222222",
  "title": "Phiên đấu giá đã được duyệt",
  "body": "Phiên đấu giá \"Đồng hồ Seiko cổ điển\" đã được phê duyệt và sẽ tự động mở bán vào lúc 14:00 28/11/2026.",
  "notificationType": "AuctionStarted",
  "payload": "{\"type\":\"AuctionStarted\",\"auctionId\":\"11111111-1111-1111-1111-111111111111\",\"audience\":\"seller\",\"status\":\"Scheduled\"}"
}
```

### Nhờ bên `NotificationService` & `Gateway` kiểm tra giúp 3 điểm ở cổng `/api/v1/notifications/in-app`:
1. **Phân quyền (Auth Policy):** Đảm bảo endpoint `POST /api/v1/notifications/in-app` chấp nhận Service Token của `AuctionService` (giống như các endpoint `/api/v1/notifications/auction-*`), và trên `ApiGateway` đã mở route `/api/v1/notifications/in-app` trỏ về `NotificationService`.
2. **Chống trùng bằng `sourceEventId`:** Nếu `NotificationService` nhận được request có `sourceEventId` đã tồn tại trong DB thì trả về `200 OK` (idempotent) thay vì báo lỗi `409` hoặc lưu trùng 2 thông báo.
3. **Lưu giữ nguyên trường `payload` và `notificationType`:** Bên Frontend (`notification-target.ts`) đang đọc `notificationType: "AuctionStarted"` và `payload.auctionId` để khi Seller bấm vào thông báo trên chuông là chuyển thẳng vào trang chi tiết phiên đấu giá.

---

## 2. Luồng phiên đấu giá bắt đầu mở bán (`POST /api/v1/notifications/auction-starts`)

Khi tới giờ `ScheduledStartAt` và worker của `AuctionService` chuyển phiên từ `Scheduled` sang `Running`, hệ thống bắt đầu gọi cổng `POST /api/v1/notifications/auction-starts`:

```json
{
  "sourceEventId": "8b9012cd-152f-4353-ad40-adb765add111",
  "auctionId": "11111111-1111-1111-1111-111111111111",
  "sellerUserId": "22222222-2222-2222-2222-222222222222",
  "auctionTitle": "Đồng hồ Seiko cổ điển",
  "auctionStatus": "Active",
  "scheduledStartAt": "2026-11-28T07:00:00+00:00",
  "startedAt": "2026-11-28T07:00:02+00:00",
  "auctionEndsAt": "2026-11-29T07:00:00+00:00",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

- **Nhờ bên `NotificationService` kiểm tra:** Khi lưu bản ghi In-App Notification cho `sellerUserId`, trong JSON `payload` lưu xuống DB nhớ gắn kèm `{"type":"AuctionStarted","auctionId":"...","audience":"seller"}` để Seller bấm vào thông báo là nhảy tới phòng đấu giá.

---

## 3. Luồng thông báo kết quả phiên & quá hạn thanh toán vừa mới kích hoạt

Ngoài 2 mục trên, `AuctionService` cũng vừa bật gọi 3 cổng dưới đây (trước đây bên `NotificationService` đã mở cổng nhưng `AuctionService` chưa gọi). Nhờ bên `NotificationService` kiểm tra lại template hiển thị và chống trùng:

### 3.1. Báo kết quả phiên cho Seller (`POST /api/v1/notifications/auction-seller-results`)
- Được gọi khi:
  1. Phiên hết giờ có người thắng (`outcome: "WinnerSelected"`).
  2. Có người bấm Mua ngay (`outcome: "WinnerSelected"`).
  3. Seller đồng ý bán dưới giá sàn (`outcome: "WinnerSelected"`).
  4. **Người về nhì bấm Đồng ý nhận lời mời mua lại** (`outcome: "WinnerSelected"`).
- **Lưu ý quan trọng về khóa chống trùng (Unique Key / Idempotency):**
  - Trong một phiên đấu giá, nếu người thắng đầu tiên bỏ cọc và người về nhì đồng ý mua lại, `AuctionService` sẽ gửi thêm một sự kiện `auction-seller-results` (và `auction-winners`) mới với **`sourceEventId` mới** và **`winnerUserId` mới**, nhưng vẫn giữ cùng `auctionId` và `resultId` của phiên đó.
  - Vì vậy, bên `NotificationService` chỉ nên chặn trùng (unique constraint) theo **`sourceEventId`**, **không** đặt unique index cứng trên cặp `(auctionId, resultId)` để tránh làm rơi thông báo khi chuyển người thắng sang người về nhì.

### 3.2. Báo quá hạn thanh toán 24h cho Buyer và Seller
- **Cho Buyer bỏ cọc:** `POST /api/v1/notifications/auction-payment-timeouts` (vừa tạo thông báo trên chuông `AuctionPaymentTimedOut`, vừa gửi Email báo hủy đơn & khấu trừ cọc qua `winnerEmail`).
- **Cho Seller:** `POST /api/v1/notifications/auction-seller-payment-timeouts` (báo trên chuông cho Seller biết người thắng đã quá hạn 24h không thanh toán).

---

## 4. Bảng kiểm tra nhanh (Checklist cho team Notification)

| STT | Endpoint bên `NotificationService` | Trạng thái bên `AuctionService` | Việc cần bên `NotificationService` kiểm tra |
| :-: | :--- | :---: | :--- |
| 1 | `POST /api/v1/notifications/in-app` | ✅ Đã gọi khi Tạo phiên chờ duyệt & khi Staff duyệt phiên | Mở quyền cho Service Token của `AuctionService` + chống trùng theo `sourceEventId` |
| 2 | `POST /api/v1/notifications/auction-starts` | ✅ Đã gọi khi phiên chuyển sang `Running` | Đảm bảo lưu `payload.auctionId` và `notificationType = "AuctionStarted"` |
| 3 | `POST /api/v1/notifications/auction-winners` | ✅ Đã gọi cho cả Winner đầu & Người về nhì nhận kèo | Chống trùng theo `sourceEventId` (không khóa cứng theo `resultId`) |
| 4 | `POST /api/v1/notifications/auction-seller-results` | ✅ Đã gọi khi chốt Winner & khi Người về nhì nhận kèo | Chống trùng theo `sourceEventId` (không khóa cứng theo `resultId`) |
| 5 | `POST /api/v1/notifications/auction-payment-timeouts` | ✅ Đã gọi khi Winner / Người về nhì quá hạn 24h | Kiểm tra gửi cả In-App lẫn Email qua `winnerEmail` |
| 6 | `POST /api/v1/notifications/auction-seller-payment-timeouts` | ✅ Đã gọi khi Winner / Người về nhì quá hạn 24h | Kiểm tra hiển thị thông báo trên chuông của Seller |
