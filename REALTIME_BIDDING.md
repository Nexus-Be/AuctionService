# Đặt giá đồng thời và thông báo realtime

Tài liệu này giải thích hai vấn đề realtime của AuctionService, dựa trên code của nhánh `feature/auction-bid-settlement-apis`:

1. Nhiều người đặt giá cùng lúc thì hệ thống xử lý thế nào.
2. Cách bidder nhận thông tin bid mới mà không cần F5 (SignalR).

---

## 1. Nhiều bidder đặt giá cùng lúc

### Nguyên tắc

- Mỗi request `POST /api/v1/auctions/{auctionId}/bids` được xử lý **riêng**. Service không gom các bid lại để chọn bid tốt nhất.
- Request nào **lưu vào DB trước** thì thắng. Việc này do DB quyết định lúc `SaveChanges`, nhờ `RowVersion` của `AuctionState` được khai báo là concurrency token.
- Chỉ bid **hợp lệ và lưu thành công** mới làm `RowVersion` tăng thêm 1. Bid không hợp lệ không ghi gì vào DB và `RowVersion` giữ nguyên.
- Phiên mới tạo có `RowVersion = 1`.

### Thứ tự kiểm tra trong `BidService.PlaceBidAsync`

| Bước | Kiểm tra                                                                                                           | Nếu lỗi                      |
| ---- | ------------------------------------------------------------------------------------------------------------------ | ---------------------------- |
| 1    | Input: auctionId, `Idempotency-Key`, `If-Match`, số tiền                                                           | 400                          |
| 2    | `Idempotency-Key` đã dùng: cùng request thì trả lại kết quả lần đầu; khác request thì lỗi                          | 422 `IDEMPOTENCY_KEY_REUSED` |
| 3    | Phiên tồn tại, seller không bid phiên của mình, phiên đang `Running` và chưa hết giờ                               | 404 / 403 / 410              |
| 4    | `If-Match` phải bằng `RowVersion` hiện tại                                                                         | 409 `CONCURRENT_UPDATE`      |
| 5    | User service xác nhận bidder đủ điều kiện (uy tín, trạng thái tài khoản)                                           | lỗi tương ứng                |
| 6    | Số tiền ≥ giá hiện tại + bước giá (`BidStepAmount`)                                                                | 422                          |
| 7    | Ghi bid, lịch sử bid, giá hiện tại, `RowVersion + 1`, anti-sniping, idempotency, audit trong **một** `SaveChanges` | 409 nếu có người lưu trước   |

Lưu ý: bước 4 chạy **trước** bước 5 và 6. Request vừa cũ version vừa thấp tiền sẽ nhận 409, không nhận 422.

### Ví dụ

| Bước | Request                                                               | Kết quả                                    | RowVersion |
| ---- | --------------------------------------------------------------------- | ------------------------------------------ | ---------- |
| 1    | A bid 100k, `If-Match: 1`                                             | Thành công                                 | 1 → **2**  |
| 2    | B bid 50k, `If-Match: 2`                                              | 422 (thấp hơn mức tối thiểu), không ghi gì | 2          |
| 3    | B, C, D cùng bid 200k, `If-Match: 2`                                  | B lưu trước nên thành công                 | 2 → **3**  |
| 4    | C và D                                                                | 409 `CONCURRENT_UPDATE` (version đã cũ)    | 3          |
| 5    | C, D tải lại phiên (thấy `RowVersion = 3`, giá 200k) rồi bid lại 200k | 422, cần ≥ 200k + bước giá                 | 3          |

- C và D bị 409 không phải vì bid "không hợp lệ", mà vì họ đang giữ version cũ. Họ cần tải lại phiên rồi bid lại.
- Tiêu chí là **ai lưu trước**, không phải ai bid cao hơn. Nếu B bid 200k lưu trước thì C bid 300k đến sau vẫn bị 409, rồi bid lại thì thành công nếu 300k còn đủ mức tối thiểu.

### Bid trùng do bấm hai lần hoặc retry

Việc này do `Idempotency-Key` xử lý, không phải `If-Match`. Cùng key và cùng nội dung thì trả lại kết quả lần đầu, không tạo bid mới và không bắn SignalR lần nữa. Key chỉ được lưu khi bid thành công, nên bid lỗi có thể gửi lại với cùng key.

---

## 2. Thông báo realtime bằng SignalR

SignalR giữ một đường truyền liên tục giữa **trình duyệt và server API** (dùng WebSocket khi trình duyệt hỗ trợ, nếu không thì tự chuyển sang SSE hoặc long polling). Nhờ đó server đẩy tin xuống được mà trình duyệt không cần hỏi liên tục.

### Luồng hoạt động

1. **Client kết nối.** Khi mở trang một phiên, trình duyệt kết nối tới hub tại `/hubs/auctions`.
2. **Client vào nhóm của phiên.** Client gọi `JoinAuction(auctionId)`. `AuctionHub` kiểm tra:
   - `auctionId` không rỗng (không thì `VAL_INVALID_IDENTIFIER`),
   - kết nối chưa theo dõi đủ 20 phiên (không thì `TOO_MANY_AUCTIONS`),
   - phiên có tồn tại và chưa bị xóa trong DB (không thì `AUCTION_NOT_FOUND`).

   Hợp lệ thì kết nối được thêm vào nhóm `auction:{id}` (id viết liền, không có dấu gạch ngang). Vào lại phiên đã vào thì bỏ qua. Muốn thôi theo dõi thì gọi `LeaveAuction(auctionId)`. Hub chỉ làm việc này, **không xử lý đặt giá**.

3. **Bidder đặt giá bằng REST.** Trình duyệt gửi `POST /api/v1/auctions/{id}/bids` như một API thường, không đi qua hub. `BidService` kiểm tra luật đấu giá, `Idempotency-Key`, `If-Match`, rồi lưu DB.
4. **Lưu xong mới thông báo.** Chỉ khi `SaveChanges` thành công, `BidService` mới gọi `IAuctionNotifier.BidPlacedAsync(...)`. `BidService` chỉ yêu cầu "hãy báo", không biết báo bằng cách nào. Bản cài đặt thật là `SignalRAuctionNotifier`, dùng `IHubContext<AuctionHub>` để phát sự kiện `BidPlaced` vào nhóm của phiên.
5. **Mọi người trong nhóm nhận tin**, kể cả người vừa bid. Tin gồm: mã bid, số thứ tự bid, số tiền, giá hiện tại, số lượt bid, thời điểm, giờ kết thúc mới, số lần gia hạn, giá trước đó và `RowVersion` mới. Giao diện cập nhật mà không cần F5. FE nên dùng luôn `RowVersion` trong tin này làm `If-Match` cho lần bid tiếp theo.

Nếu việc đẩy tin bị lỗi thì chỉ ghi log cảnh báo, bid vẫn thành công vì dữ liệu đã được lưu.

### Các sự kiện

| Sự kiện                | Khi nào                                                        |
| ---------------------- | -------------------------------------------------------------- |
| `BidPlaced`            | Có lượt đặt giá mới được lưu                                   |
| `AuctionCancelled`     | Phiên bị hủy                                                   |
| `AuctionStatusChanged` | Trạng thái phiên đổi (ví dụ kết thúc, thất bại, đã thanh toán) |

---

## Giới hạn hiện tại

- **Không có backplane.** Code chỉ gọi `AddSignalR` với giới hạn kích thước tin nhận (4 KB). Nếu chạy nhiều instance API cùng lúc, người dùng nối vào instance A sẽ không nhận sự kiện phát ra từ instance B. Muốn mở rộng cần thêm backplane (ví dụ Redis).
- **Không phát lại tin bị lỡ.** Client nên gọi `GET /api/v1/auctions/{id}` trước để lấy trạng thái và `RowVersion` hiện tại rồi mới nghe sự kiện; khi mất kết nối rồi nối lại cũng nên gọi lại `GET`.
