# TÀI LIỆU PHÂN TÍCH NGHIỆP VỤ: TÍNH NĂNG MUA NGAY (BUY NOW)

> **Đối tượng đọc:** Product Owner / BA, Frontend (FE), Backend (BE), QA.  
> **Mục tiêu tài liệu:** Làm rõ lý do bài toán Buy Now xuất hiện, các xung đột nghiệp vụ tiềm ẩn, giải pháp **"Thanh toán tức thì qua Ví Nexus"**, và hướng dẫn chi tiết cách thể hiện chính sách + UI/UX cho đội Frontend triển khai đồng bộ.  
> **Tài liệu liên quan:** [01_AUCTION_END_TO_END_FLOW.md](./01_AUCTION_END_TO_END_FLOW.md)

---

## 1. Bối cảnh & Vì sao bài toán này xuất hiện?

### 1.1. Nhu cầu thực tế từ hai phía
- **Phía Người bán (Seller):** Muốn có một mức giá chốt nhanh ("Giá Mua ngay" — `BuyNowPrice`) cao hơn mức giá tối thiểu mong muốn (`ReservePrice` / Giá sàn). Nếu có người chấp nhận trả mức giá này sớm, Seller sẵn sàng bán luôn mà không cần chờ đến hết giờ đấu giá.
- **Phía Người mua (Buyer):** Có những người mua không muốn mất thời gian canh giờ đấu giá hay cạnh tranh từng bước giá. Họ sẵn sàng trả cao hơn ngay từ đầu để sở hữu chắc chắn món hàng.

### 1.2. Tại sao cách làm thông thường ("Bấm Buy Now -> Chờ X phút thanh toán") lại gây lỗi nghiệp vụ?
Nếu thiết kế theo kiểu truyền thống: *Ai bấm Buy Now thì giữ suất cho họ trong X phút để đi thanh toán (VNPay/MoMo/Chuyển khoản), trong lúc đó phiên vẫn chạy hoặc tạm khóa*:
1. **Xung đột nếu KHÔNG khóa phiên trong X phút:**
   - Trong lúc Người mua A đang mở cổng thanh toán (chưa trả tiền xong), Người mua B hoàn toàn có thể đặt một mức bid **cao hơn cả giá Buy Now**.
   - Lúc này hệ thống bị kẹt: Nếu cho A thắng với giá thấp hơn $\rightarrow$ Seller mất tiền, B ức chế; nếu hủy Buy Now của A để cho B thắng $\rightarrow$ A ức chế vì đang nhập OTP thanh toán thì bị cướp đơn.
2. **Xung đột nếu KHÓA CỨNG phiên trong X phút:**
   - Trải nghiệm của toàn bộ các bidder khác bị đứt gãy mỗi khi có người bấm Buy Now.
   - Tạo kẽ hở **phá phiên (Troll/Sabotage)**: Đối thủ có thể dùng tài khoản phụ bấm Buy Now liên tục để đóng băng phiên đấu giá ngay vào thời điểm cao trào, chấp nhận không trả tiền.
3. **Phức tạp hóa luồng phạt & hoàn cọc:**
   - Phải xây thêm bộ đếm timeout `X` phút, xử lý phạt điểm uy tín khi người bấm Buy Now "bùng" thanh toán, và xử lý trạng thái lơ lửng khi phiên gần hết giờ.

---

## 2. Giải pháp nghiệp vụ được chọn: "Buy Now Thanh toán tức thì qua Ví Nexus"

Thay vì cho **"Giữ chỗ trước, trả tiền sau"**, sàn Nexus áp dụng mô hình **Zero-wait Buy Now (Tiền trao cháo múc ngay trong 1 giây)** dựa trên nền tảng **Ví nội bộ Nexus** hiện có.

### 2.1. Quy tắc thiết lập & Điều kiện tồn tại của Buy Now
1. **Quan hệ giá (validate duy nhất):** `BuyNowPrice` là tùy chọn, do Seller tự nhập. Nếu có nhập thì bắt buộc `BuyNowPrice > ReservePrice` (hoặc `BuyNowPrice > StartPrice` nếu phiên không có Giá sàn). Hệ thống **không** áp thêm khoảng cách tối thiểu hay bội số.
2. **Ngưỡng đóng Buy Now:** Buy Now chỉ hoạt động khi phiên ở trạng thái `Running` và:
   - **Phiên có Giá sàn:** giá hiện tại còn thấp hơn Giá sàn (`CurrentBid < ReservePrice` hoặc chưa có bid). Khi có lượt bid chạm/vượt Giá sàn, nút Buy Now **đóng vĩnh viễn** cho phiên đó.
   - **Phiên không có Giá sàn:** chỉ hoạt động khi **chưa có lượt bid nào**. Lượt bid đầu tiên sẽ đóng Buy Now (giống chuẩn eBay).

### 2.2. Luồng xử lý cốt lõi khi bấm Buy Now (Buy Now = Thắng phiên ngay lập tức)
Buy Now **không tạo loại đơn mới**, mà được coi như **người thắng phiên ngay lập tức** và đi tiếp **đúng luồng chốt phiên hiện có**. Tiền được tách thành **2 khoản riêng**:

| Khoản | Thời điểm | Cách thu |
| :--- | :--- | :--- |
| **Tiền hàng (`BuyNowPrice`)** | Ngay khi bấm xác nhận | Khóa (Hold) trong Ví Nexus, được cấn trừ cọc đã đóng (nếu có) |
| **Phí vận chuyển** | Trong 24h sau đó, lúc thanh toán đơn | Buyer trả khi checkout đơn trên Commerce (lúc này mới có địa chỉ để tính phí ship) |

**Các bước:**
1. Người mua bấm **Mua ngay** $\rightarrow$ mở **Popup xác nhận** (hiển thị số tiền sẽ khóa trong Ví và nhắc phí ship thanh toán sau).
2. Người mua bấm **Xác nhận** (kèm `Idempotency-Key`) $\rightarrow$ Backend:
   1. Kiểm tra quyền như lúc đóng cọc: không phải Seller, không bị ban, không bị hạn chế quyền đấu giá, đã xác minh tài khoản.
   2. Kiểm tra lại phiên `Running` và Buy Now còn mở (dùng `RowVersion` chống tranh chấp với lượt bid / Buy Now khác — **không khóa phiên X phút**).
   3. Hệ thống tính mức **tiền cọc theo uy tín (`5%–30%` của `StartPrice`)** nếu khách chưa đóng cọc, và khóa trong Ví **2 phần tách biệt**: (a) Khoản **Tiền cọc** (hoặc giữ nguyên khoản cọc đã đóng trước đó) và (b) Khoản **Tiền hàng còn lại** (`BuyNowPrice − Tiền cọc`).
3. **Nếu Ví đủ tiền:**
   - Phiên chuyển `Ended` (lý do `BuyNow`), người mua là người thắng với giá `BuyNowPrice`.
   - Đẩy kết quả sang Commerce như người thắng bình thường (`Source = "BUY_NOW"`, khoản tiền hàng đã khóa được ghi nhận sẵn) $\rightarrow$ Commerce tạo đơn, Buyer chỉ còn phải trả **phí ship trong 24h**.
   - Mở khóa hoàn 100% cọc cho **toàn bộ** bidder khác ngay lập tức (không giữ cọc Hạng 2).
4. **Nếu Ví KHÔNG đủ tiền:** Từ chối ngay $\rightarrow$ phiên chạy tiếp, **không trừ uy tín**, **không giữ suất**.
5. **Nếu Buyer không trả phí ship trong 24h:** Áp dụng luật bỏ kèo — **chỉ tịch thu đúng khoản Tiền cọc** (chia 70% Seller / 30% Sàn), trừ uy tín, tăng chuỗi vi phạm, và **mở khóa hoàn trả lại toàn bộ khoản Tiền hàng còn lại** về Ví cho Buyer. Không mời Hạng 2.
6. **Lỗi giữa chừng (Ví đã khóa tiền nhưng lưu DB thất bại):** Đánh dấu khoản giữ cần giải tỏa và để job nền tự động trả lại tiền cho Buyer (giống cơ chế giải tỏa cọc hiện có).

---

## 3. Giải pháp này làm tốt (và tránh được) những vấn đề gì?

| Vấn đề thường gặp | Cách giải pháp "Thanh toán tức thì qua Ví" xử lý |
| :--- | :--- |
| **Xung đột người khác bid vượt Buy Now trong lúc chờ trả tiền** | **Triệt tiêu 100%:** Không có khoảng chờ `X` phút nên không bao giờ xảy ra chuyện một người đang thanh toán mà người khác vẫn bid đè lên được. |
| **Khóa phiên làm gián đoạn trải nghiệm người dùng** | **Không bao giờ khóa chờ:** Ai đủ tiền và bấm xác nhận trước thì phiên kết thúc luôn trong 1 giây; người không đủ tiền thì hệ thống từ chối ngay, phiên chạy tiếp bình thường không khựng lại 1 giây nào. |
| **Lỗ hổng bấm Buy Now ảo để phá phiên** | **Không thể phá:** Muốn Buy Now thành công bắt buộc phải có sẵn 100% tiền thật trong Ví và bị trừ ngay lập tức. Không đủ tiền thì hệ thống từ chối luôn, không ảnh hưởng tới ai. |
| **Phức tạp giữ cọc Hạng 2 & phạt uy tín bùng kèo** | **Đơn giản hóa tối đa:** Đã khóa đủ 100% tiền hàng nên tỷ lệ bùng cực thấp; nếu quá 24h không trả phí ship thì chỉ thu đúng khoản Tiền cọc (hoàn trả phần tiền hàng còn lại), không cần giữ cọc Hạng 2. |
| **Phải làm thêm loại đơn mới ở Commerce** | **Không cần:** Buy Now dùng lại luồng chốt phiên, giữ cọc, Outbox đang có; Commerce chỉ nhận thêm `Source = "BUY_NOW"`. |

---

## 4. Quy tắc Nghiệp vụ chi tiết về Tiền cọc & Ví (BE + FE cần nắm)

Vì trong hệ thống Nexus hiện tại, **tiền cọc (`Deposit`) bản chất là tạm khóa (Hold) một phần số dư khả dụng trong Ví Nexus**, nên cần đặc biệt lưu ý 2 trường hợp sau:

1. **Người mua CHƯA đóng cọc phiên đó có được bấm Buy Now không?**
   - **CÓ.** Không cần bắt người mua đóng cọc trước rồi mới được Buy Now (giảm rào cản chốt đơn nhanh), miễn là **Số dư khả dụng trong Ví $\ge$ 100% `BuyNowPrice`** và tài khoản không phải Seller của phiên / không bị khóa quyền.
2. **Người mua ĐÃ đóng cọc phiên đó thì tính số dư thế nào? (Tránh lỗi trừ nhầm 2 lần)**
   - Ví dụ: Giá Buy Now = `15.000.000 đ`. Ví khách có tổng `15.000.000 đ`, đã đóng cọc (đang Hold) `1.000.000 đ` $\rightarrow$ Số dư khả dụng hiển thị còn `14.000.000 đ`.
   - Khi khách bấm Buy Now, hệ thống **PHẢI cấn trừ khoản cọc đang Hold**: chỉ kiểm tra và **khóa (Hold) thêm** phần chênh lệch **`15.000.000 - 1.000.000 = 14.000.000 đ`** từ số dư khả dụng, gộp cùng khoản `1.000.000 đ` đang Hold làm tiền hàng cho đơn Buy Now (phí ship thanh toán riêng trong 24h trên Commerce).
   - Nếu không cấn trừ khoản đang Hold, khách có đủ 15 triệu nhưng vẫn bị báo *"Không đủ số dư"*.

---

## 5. Hướng dẫn triển khai dành riêng cho Frontend (FE)

Để người dùng hiểu rõ luật chơi ngay từ đầu và không bị bất ngờ khi nút Mua ngay biến mất hoặc khi bị trừ tiền ví, FE cần triển khai đầy đủ **4 điểm** sau:

### 5.1. Chính sách & UI nhắc nhẹ xuyên suốt (Từ lúc phiên `Scheduled`)
Thông tin về **Giá Mua ngay** được hiển thị ngay từ khi phiên ở trạng thái `Scheduled` (Sắp diễn ra) cho tới khi có lượt bid chạm Giá sàn. Do đó:
- **Trong phần Chính sách / Điều khoản tham gia đấu giá:** Cần bổ sung rõ quy tắc Buy Now:
  > *"Tính năng Mua ngay cho phép sở hữu sản phẩm ngay lập tức bằng cách tạm khóa 100% tiền hàng trong Ví Nexus (được cấn trừ tiền cọc nếu đã đóng) và hoàn tất thanh toán phí vận chuyển trong vòng 24 giờ. Tính năng này sẽ tự động đóng khi mức giá đấu chạm ngưỡng Giá sàn (hoặc ngay ở lượt đặt giá đầu tiên nếu phiên không có Giá sàn)."*
- **UI nhắc nhẹ ngay tại khu vực hiển thị Buy Now:**
  - Khi phiên `Scheduled`: Hiển thị mức giá Mua ngay kèm trạng thái chờ mở phiên và dòng nhắc nhỏ (helper text / tooltip): *"Khóa tiền hàng qua Ví Nexus khi phiên mở (trả phí ship sau 24h). Tự động ẩn khi đạt giá sàn."*
  - Khi phiên `Running` (còn mở Buy Now): Vẫn giữ dòng nhắc nhẹ ngắn gọn ngay dưới nút **Mua ngay** (ví dụ: *"Khóa tiền hàng qua Ví Nexus • Tự đóng khi chạm giá sàn"*) để người dùng chủ động chuẩn bị sẵn tiền trong ví và hiểu vì sao nút này có thể biến mất bất cứ lúc nào nếu có người bid lên.

### 5.2. Ma trận hiển thị nút Buy Now theo trạng thái phiên

| Trạng thái phiên | Điều kiện phụ | Hiển thị trên UI |
| :--- | :--- | :--- |
| **`Scheduled`** (Sắp diễn ra) | Phiên có cài `BuyNowPrice` | **Hiện thông tin Giá Mua ngay** (dạng badge/khối thông tin + nhắc nhẹ), nút bấm **Disabled** (hoặc ghi *"Mở bán khi phiên bắt đầu"*). |
| **`Running`** (Đang diễn ra) | (**Có Giá sàn:** `CurrentBid < ReservePrice` hoặc chưa có bid; **Không Giá sàn:** `BidCount == 0`) **VÀ** người xem **không phải** Seller | **Hiện nút `Mua ngay (Giá X đ)` ở trạng thái Active**, cho phép bấm. |
| **`Running`** (Đang diễn ra) | Người xem **là Seller** của chính phiên đó | **Ẩn nút Mua ngay** (hoặc disable hoàn toàn) — không cho phép tự mua sản phẩm của mình. |
| **`Running`** (Đang diễn ra) | `CurrentBid >= ReservePrice` | **Ẩn / Đóng nút Mua ngay ngay lập tức**. Nếu người dùng đang mở màn hình lúc có người bid chạm sàn $\rightarrow$ ẩn nút realtime và hiện thông báo nhẹ: *"Giá đấu đã đạt giá sàn, tính năng Mua ngay đã đóng."* |
| **`Ended` / `Cancelled`** | Bất kỳ | **Ẩn nút Mua ngay**. Nếu kết thúc do `BuyNow` $\rightarrow$ hiển thị rõ trạng thái *"Đã bán theo hình thức Mua ngay"*. |

### 5.3. Luồng Modal Xác nhận (Bắt buộc trước khi gọi API khóa tiền)
Vì Buy Now **khóa tiền hàng trong Ví ngay lập tức**, FE **tuyệt đối không** gọi API Buy Now ngay khi vừa bấm nút ở ngoài màn hình, mà phải qua **1 bước Popup xác nhận**:

```text
[Bấm nút "Mua ngay"]
        │
        ▼
[Mở Modal Xác nhận Mua ngay]
  • Giá Mua ngay:                 15.000.000 đ
  • Tiền cọc đã đóng (nếu có):    - 1.000.000 đ
  • Số tiền khóa từ Ví khả dụng:  14.000.000 đ
  • Số dư Ví khả dụng hiện tại:   XX.XXX.XXX đ
  • Lưu ý: Phí vận chuyển thanh toán riêng trong 24h khi checkout đơn.
    Nếu quá hạn không hoàn tất thanh toán phí ship, hệ thống sẽ tịch thu phần tiền cọc (và hoàn trả phần tiền hàng còn lại).
        │
        ├──► Nếu Ví khả dụng < Số tiền cần trừ:
        │       - Khóa nút "Xác nhận thanh toán"
        │       - Hiện cảnh báo đỏ: "Số dư ví khả dụng không đủ để Mua ngay"
        │       - Hiện nút CTA: [Nạp tiền vào Ví]
        │
        └──► Nếu Ví khả dụng >= Số tiền cần trừ:
                - Bấm [Xác nhận Mua ngay] (disable nút + loading chống bấm đúp)
                - Gửi API kèm Idempotency-Key
```

### 5.4. Xử lý Realtime & Các tình huống lỗi từ Backend
**Dữ liệu realtime cần có:** Sự kiện `BidPlaced` bổ sung cờ `isReserveMet` và `buyNowAvailable`; sự kiện `AuctionStatusChanged` bổ sung lý do kết thúc (`BuyNow`) để FE đóng nút / Modal ngay.

FE cần lắng nghe sự kiện realtime (SignalR/WebSocket) và bắt các trường hợp lỗi sau để cập nhật UI mượt mà (đặc biệt là các ca **cùng bấm trong 1 mili-giây**):

1. **Đã có người Buy Now trước đó 1 giây:**
   - Khi nhận sự kiện phiên kết thúc (`BuyNow`) qua realtime: Lập tức đóng Modal xác nhận (nếu đang mở), khóa toàn bộ nút Bid & Buy Now, hiện banner/toast: *"Phiên đấu giá đã kết thúc — Sản phẩm vừa được người khác Mua ngay."*
   - Nếu API trả về lỗi phiên đã đóng/đã có người mua $\rightarrow$ Hiển thị thông báo tương tự và refresh lại trạng thái phiên.
2. **Có người vừa Bid chạm/vượt Giá sàn đúng lúc người dùng đang mở Modal Buy Now:**
   - Khi nhận sự kiện giá bid mới `>= ReservePrice`: Nếu Modal Buy Now đang mở $\rightarrow$ tự động đóng Modal (hoặc disable nút xác nhận) và báo: *"Giá đấu vừa đạt giá sàn, tính năng Mua ngay không còn khả dụng."*
   - Nếu API trả về lỗi giá đã đạt sàn $\rightarrow$ Báo lỗi rõ ràng cho người dùng biết để chuyển sang đấu giá tiếp.
3. **Lỗi thiếu số dư tại thời điểm bấm xác nhận** (ví dụ khách vừa dùng ví thanh toán đơn khác ở tab bên cạnh):
   - Hiển thị thông báo *"Số dư ví khả dụng không đủ"* kèm nút dẫn sang trang Nạp tiền.

### 5.5. Trường hợp Buyer đặt giá Bid `>= BuyNowPrice` khi nút Buy Now đang còn hiện
* **Nguyên tắc Backend:** Không cấm quyền đặt giá tự do của người dùng. Nếu người dùng vẫn xác nhận đặt giá `>= BuyNowPrice`, `CurrentBid` nhảy lên mức đó; vì lúc này `CurrentBid >= ReservePrice` nên **nút Buy Now tự động ẩn đi**, phiên tiếp tục chạy bình thường.
* **Modal hỏi xác nhận trên FE (chống gõ nhầm / trả hớ):** Khi `CurrentBid < ReservePrice` (nút Buy Now đang mở) mà người dùng nhập số tiền đặt giá `>= BuyNowPrice`, FE bật popup hỏi xác nhận:
  > *"Mức giá bạn nhập đang cao hơn Giá Mua ngay (`BuyNowPrice`). Bạn có muốn chuyển sang **Mua ngay** với giá rẻ hơn và chốt phiên luôn, hay vẫn tiếp tục đặt giá này?"*
* **Xử lý Realtime trong lúc Popup đang mở:**
  * Nếu trong lúc người dùng đang xem Popup mà SignalR (`BidPlaced` / `AuctionStatusChanged`) báo về **đã có người khác đặt giá cao hơn hoặc bằng mức người dùng định đặt** (hoặc phiên đã chạm Giá sàn / đã có người Buy Now): FE lập tức **đóng/khóa nút xác nhận trong Popup** và cập nhật giá mới kèm thông báo *"Giá đấu đã thay đổi trong lúc bạn xác nhận, vui lòng kiểm tra lại."*
  * **Chốt chặn Backend:** Mọi lệnh đặt giá đều bắt buộc kèm header `If-Match` (`RowVersion`). Nếu phiên đã có lượt bid mới chen vào trong lúc Popup đang mở, Backend từ chối với `409 CONCURRENT_UPDATE` để tuyệt đối không xử lý trên dữ liệu cũ.
