# TÀI LIỆU MÔ TẢ NGHIỆP VỤ: QUY TRÌNH ĐẤU GIÁ TRỰC TUYẾN TRÊN SÀN NEXUS (TỪ ĐẦU ĐẾN CUỐI)

> **Góc nhìn tài liệu:** Phân tích nghiệp vụ (Business Analysis — BA) dành cho tất cả các bên liên quan. Tài liệu mô tả hành trình thực tế của **Người bán**, **Người mua**, **Nhân viên vận hành / Quản trị viên (Staff/Admin)** và **Hệ thống** từ lúc tạo phiên đấu giá cho tới khi đơn hàng hoàn tất, kèm đầy đủ quy định về thời gian, mức tiền cọc, giá sàn, mua ngay và chế tài xử phạt.

---

## 1. BỨC TRANH TỔNG QUAN QUY TRÌNH ĐẤU GIÁ

```mermaid
flowchart TD
    A["1. Người bán tạo phiên đấu giá (PendingApproval)"] -->|Staff/Admin Từ chối| C1["Phiên bị hủy -> Mở khóa hàng"]
    A -->|Staff/Admin Duyệt| A2["2. Phiên lên lịch (Scheduled)"]
    A2 -->|Hủy trước khi có người đặt giá| C["Phiên bị hủy -> Hoàn 100% cọc & Mở khóa hàng"]
    A2 -->|Tới giờ mở phiên| B["3. Phiên đấu giá bắt đầu diễn ra (Running)"]
    B -->|Người mua đóng cọc & Trả giá| B
    B -->|Có người trả giá trong 5 phút cuối| B1["Tự động cộng thêm 5 phút (Tối đa 10 lần)"]
    B1 --> B
    B -->|Người mua bấm Mua ngay (Buy Now)| F0["Kết thúc ngay -> Tạo đơn 24h & Hoàn cọc người khác"]
    B -->|Hết giờ mà không có ai trả giá| D["Phiên thất bại -> Mở khóa hàng để đăng bán lại"]
    B -->|Hết giờ & Giá < Giá sàn| R0["Chờ Seller duyệt bán dưới giá sàn (12h)"]
    R0 -->|Seller Từ chối / Quá 12h| R1["Phiên thất bại -> Hoàn cọc & Tính chuỗi từ chối (3 lần khóa 14 ngày)"]
    R0 -->|Seller Đồng ý| E
    B -->|Hết giờ & Giá >= Giá sàn| E["4. Phiên kết thúc (Ended)<br/>Giữ lại cọc của Hạng 1 & Hạng 2; Mở khóa cọc từ Hạng 3 trở đi"]
    E -->|Người thắng thanh toán đơn trong 24h| F["5. Đã thanh toán (Chờ Người bán xác nhận)<br/>Tiền cọc trừ thẳng vào đơn + Xóa chuỗi vi phạm về 0"]
    F -->|Người bán xác nhận giao hàng trong 24h| G["6. Đấu giá thành công (Settled)<br/>Mở khóa trả lại cọc cho Hạng 2 & Giao hàng"]
    F -->|Người bán từ chối / Quá 24h không giao| H["Phiên thất bại do Người bán<br/>Hoàn 100% tiền hàng & tiền cọc cho Người mua + Trừ 5 điểm uy tín Người bán"]
    E -->|Người thắng bỏ kèo quá 24h không trả tiền| I["Tịch thu cọc Người thắng (Chia 70% Người bán / 30% Sàn)<br/>+ Tăng chuỗi vi phạm & Trừ điểm uy tín"]
    I -->|Không có Người về nhì| D
    I -->|Có Người về nhì & Giá >= Giá sàn| J["7. Gửi lời mời mua cho Người về nhì (Hạn trả lời 24h)"]
    I -->|Có Người về nhì & Giá < Giá sàn| J0["Hỏi Seller duyệt bán dưới giá sàn (12h)"]
    J0 -->|Seller Từ chối / Quá 12h| R1
    J0 -->|Seller Đồng ý| J
    J -->|Người về nhì Từ chối hoặc Quá 24h không trả lời| K["Phiên thất bại<br/>Hoàn trả 100% cọc cho Người về nhì (Không phạt) & Mở khóa hàng"]
    J -->|Người về nhì Đồng ý mua| L["Tạo đơn hàng cho Người về nhì (Hạn thanh toán 24h)<br/>Khấu trừ tiền cọc của Người về nhì vào đơn"]
    L -->|Người về nhì thanh toán trong 24h| F
    L -->|Người về nhì bỏ kèo quá 24h không trả tiền| M["Phiên thất bại<br/>Tịch thu cọc Người về nhì (70% Người bán / 30% Sàn) + Tăng chuỗi vi phạm & Trừ điểm uy tín + Mở khóa hàng"]
```

---

## 2. CÁC VAI TRÒ THAM GIA TRONG PHIÊN ĐẤU GIÁ

1. **Người bán (Seller)**: Chủ sở hữu món hàng đưa lên đấu giá.
2. **Người mua (Buyer / Bidder)**: Người tham gia đặt cọc, trả giá hoặc bấm Mua ngay để giành quyền mua món hàng.
3. **Người thắng (Winner — Hạng 1)**: Người có mức trả giá hợp lệ cao nhất khi phiên đấu giá khép lại (hoặc người bấm Mua ngay).
4. **Người về nhì (Runner-up — Hạng 2)**: Người có mức trả giá hợp lệ cao thứ hai. Nếu Người thắng bỏ kèo không thanh toán, Người về nhì sẽ được mời mua lại món hàng theo đúng mức giá mà chính Người về nhì đã đặt.
5. **Nhân viên vận hành / Quản trị viên (Staff / Admin)**: Người kiểm duyệt phiên đấu giá mới, cấu hình chính sách đặt cọc toàn sàn, giám sát đối soát tiền cọc, xử lý tố cáo và can thiệp khi có vi phạm.

---

## 3. DIỄN BIẾN CHI TIẾT TỪNG BƯỚC TRONG LUỒNG ĐẤU GIÁ

### Bước 1: Người bán tạo phiên đấu giá & Staff/Admin kiểm duyệt
Trước khi một phiên đấu giá có thể diễn ra, Người bán chọn sản phẩm của mình và thiết lập thông tin phiên đấu giá:

1. **Điều kiện để Người bán được mở phiên**:
   - Tài khoản Người bán đang hoạt động bình thường, đã được sàn xét duyệt tư cách Người bán.
   - Có **Điểm uy tín từ 50 điểm trở lên** (trên thang điểm 100). Nếu điểm uy tín dưới 50, hệ thống từ chối không cho mở phiên.
   - Sản phẩm đem ra đấu giá đang ở trạng thái hoạt động bình thường và **chưa nằm trong bất kỳ phiên đấu giá nào khác đang mở**.
2. **Các thông số Người bán cần thiết lập**:
   - **Giá khởi điểm**: Mức giá ban đầu của món hàng (ví dụ: `1.000.000 VNĐ`).
   - **Bước giá**: Khoảng chênh lệch tối thiểu cho mỗi lần nâng giá. Nếu Người bán không tự nhập, hệ thống mặc định lấy bằng **10% giá khởi điểm** (ví dụ: giá khởi điểm `1.000.000 VNĐ` thì bước giá mặc định là `100.000 VNĐ`).
   - **Giá sàn bí mật (`ReservePrice`, tùy chọn)**: Mức giá tối thiểu Người bán kỳ vọng bán được (ẩn hoàn toàn với Người mua). Không được đặt giá sàn nếu Người bán đang bị khóa quyền đặt giá sàn 14 ngày.
   - **Giá mua ngay (`BuyNowPrice`, tùy chọn)**: Mức giá cho phép Người mua chốt mua đứt phiên ngay lập tức (phải lớn hơn giá khởi điểm và lớn hơn giá sàn nếu có).
   - **Thời gian bắt đầu và Thời gian kết thúc**:
     - Thời gian bắt đầu phải nằm ở tương lai và được đặt lịch trước **tối đa không quá 30 ngày**.
     - Thời lượng diễn ra một phiên đấu giá phải kéo dài **tối thiểu 60 phút (1 giờ)** và **tối đa 168 giờ (7 ngày)**.
3. **Kiểm duyệt phiên (`PendingApproval` → `Scheduled`)**:
   - Ngay khi tạo phiên thành công, **sản phẩm sẽ bị khóa lại (`InAuction`)** để Người bán không thể tự ý xóa sản phẩm hoặc đem bán lẻ ở nơi khác.
   - Phiên được tạo ở trạng thái **`PendingApproval`** (chưa hiển thị công khai) và Người bán nhận thông báo In-App xác nhận phiên đang chờ duyệt.
   - **Staff/Admin duyệt phiên**: Phiên chuyển sang **`Scheduled`**, Người bán nhận thông báo In-App đã duyệt. *(Nếu đã quá giờ `ScheduledStartAt` thì không duyệt được, Người bán phải cập nhật lại lịch trước).*
   - **Staff/Admin từ chối duyệt**: Bắt buộc nhập lý do. Phiên chuyển sang **`Cancelled`**, mở khóa sản phẩm bên Catalog, Người bán nhận thông báo `auction-cancelled` kèm lý do.
4. **Quyền chỉnh sửa / hủy phiên**:
   - **Chỉnh sửa phiên**: Người bán chỉ được sửa giá hoặc lịch đấu giá **trước khi phiên bắt đầu** (`PendingApproval` hoặc `Scheduled`). Khi phiên đã bắt đầu chạy thì không được sửa nữa.
   - **Người bán tự hủy phiên**: Chỉ được phép hủy khi phiên **chưa bắt đầu**, hoặc **đã bắt đầu nhưng chưa có bất kỳ ai vào đặt giá**. Một khi đã có người đặt giá đầu tiên, Người bán không thể tự ý hủy phiên.
   - **Admin hủy phiên cưỡng chế**: Trong trường hợp phát hiện hàng giả/vi phạm chính sách (hoặc xử lý tố cáo của Người mua), Admin có quyền hủy phiên bất cứ lúc nào trước khi phiên kết thúc (bắt buộc ghi rõ lý do). Toàn bộ tiền cọc của người mua sẽ được mở khóa trả lại ngay lập tức.

---

### Bước 2: Người mua xem điều khoản và Đóng tiền cọc tham gia
Để đảm bảo chỉ những người có nhu cầu mua thật mới tham gia trả giá (tránh tình trạng phá giá ảo rồi bỏ chạy), mỗi Người mua **bắt buộc phải đóng tiền cọc một lần duy nhất cho phiên đó** trước khi được mở nút Đặt giá.

1. **Ai được phép đóng cọc tham gia?**
   - Tài khoản Người mua đã xác minh danh tính và đang hoạt động bình thường.
   - Không phải là chính Người bán của phiên đó (Người bán bị cấm tự đặt giá trên sản phẩm của mình để chống đẩy giá ảo).
   - Không phải là tài khoản Admin hoặc Nhân viên sàn.
   - Không đang trong thời gian bị phạt khóa quyền đấu giá do bỏ kèo nhiều lần trước đó.
2. **Cách tính số tiền cọc phải đóng**:
   - Tiền cọc được tính theo **% trên Giá khởi điểm của sản phẩm** (chứ không tính theo giá đang trả, nên người mua chỉ cần đóng cọc 1 lần đầu tiên, về sau nâng giá bao nhiêu lần cũng không phải đóng thêm cọc).
   - **Người mua càng uy tín thì đóng cọc càng ít**:
     | Nhóm Người mua | Điểm uy tín hiện tại | Mức cọc cơ bản (% Giá khởi điểm) |
     | :--- | :---: | :---: |
     | Người mua Uy tín Rất Tốt | Từ `80 đến 100 điểm` | **5%** |
     | Người mua Uy tín Khá | Từ `60 đến 79 điểm` | **10%** |
     | Người mua Uy tín Trung bình | Từ `40 đến 59 điểm` | **15%** |
     | Người mua Uy tín Thấp | Từ `0 đến 39 điểm` | **20%** |
     | **Người mua Mới (Chưa có lịch sử)** | Chưa có điểm | **20%** |
   - **Phụ thu đối với người từng bỏ kèo liên tiếp**:
     - Nếu Người mua đang có chuỗi bỏ kèo liên tiếp từ **2 lần trở lên**, hệ thống cộng phạt thêm **+10%** vào tỷ lệ cọc (nhưng tổng tỷ lệ cọc tối đa không vượt quá **30%** giá khởi điểm).
   - **Mức cọc tối thiểu**:
     - Số tiền cọc tối thiểu cho một phiên là **`50.000 VNĐ`** (trừ trường hợp bản thân giá khởi điểm của món hàng thấp hơn `50.000 VNĐ` thì tiền cọc bằng đúng giá khởi điểm).
   - *Ví dụ minh họa*: Một chiếc đồng hồ có giá khởi điểm `1.000.000 VNĐ`.
     - Anh A có điểm uy tín là `90 điểm` (mức 5%) $\rightarrow$ Chỉ cần cọc **`50.000 VNĐ`**.
     - Anh B là tài khoản mới tạo (mức 20%) $\rightarrow$ Phải cọc **`200.000 VNĐ`**.
3. **Cách thức giữ tiền cọc**:
   - Tiền cọc **không bị trừ mất khỏi ví**, mà hệ thống chỉ **tạm khóa số tiền đó ngay trong Ví Nexus của Người mua**.
   - Người mua vẫn nhìn thấy khoản tiền này trong mục "Số dư đang khóa theo phiên đấu giá", chỉ là không thể rút ra hoặc tiêu vào việc khác trong lúc phiên đang diễn ra. Ngay khi khóa cọc thành công, nút Đặt giá lập tức mở ra cho Người mua.

---

### Bước 3: Diễn biến Đặt giá trực tiếp, Mua ngay & Gia hạn chống bắn tỉa phút chót
Khi tới giờ bắt đầu, hệ thống tự động mở phiên đấu giá và thông báo cho Người bán:

1. **Quy tắc trả giá**:
   - **Người trả giá đầu tiên của phiên**: Phải đặt mức giá tối thiểu bằng **`Giá khởi điểm + 1 Bước giá`** (ví dụ: giá khởi điểm `1.000.000 VNĐ`, bước giá `100.000 VNĐ` $\rightarrow$ lượt đầu tiên tối thiểu phải đặt **`1.100.000 VNĐ`**).
   - **Những lượt trả giá tiếp theo**: Phải cao hơn mức giá dẫn đầu hiện tại tối thiểu **1 Bước giá** (ví dụ: giá hiện tại đang là `1.100.000 VNĐ`, bước giá `100.000 VNĐ`, thì người tiếp theo phải đặt từ `1.200.000 VNĐ` trở lên).
   - **Ai bấm trước được ghi nhận trước**: Nếu có 2 người cùng nhìn thấy giá `1.100.000 VNĐ` và cùng bấm đặt `1.200.000 VNĐ` ở cùng một giây, người nào hệ thống ghi nhận trước sẽ dẫn đầu; người chậm hơn nửa giây sẽ nhận thông báo *"Đã có người vừa đặt mức giá mới trước bạn"* và màn hình tự động cập nhật mức giá mới ngay lập tức để họ quyết định có trả cao hơn nữa hay không.
   - Người vừa bị người khác vượt mặt sẽ nhận được **thông báo bị vượt giá** (`auction-outbids`); những người đã bấm **Theo dõi phiên** sẽ nhận thông báo có giá mới (`auction-watchlist-bids`).
2. **Quy tắc Gia hạn chống "bắn tỉa" phút cuối (Anti-Sniping)**:
   - Để ngăn tình trạng có người dùng phần mềm tự động canh đúng 1 giây cuối cùng mới bấm trả giá khiến những người khác không kịp trở tay:
   - Nếu có bất kỳ lượt đặt giá hợp lệ nào diễn ra khi thời gian đếm ngược của phiên chỉ còn **từ 5 phút trở xuống**, hệ thống sẽ **tự động cộng thêm 5 phút** vào giờ kết thúc phiên.
   - Việc tự động gia hạn thêm 5 phút này được phép lặp lại **tối đa 10 lần cho một phiên** (tức là phiên có thể kéo dài thêm tối đa `50 phút` so với giờ kết thúc ban đầu). Từ lần thứ 10 trở đi, giờ kết thúc sẽ chốt cố định.
3. **Quy tắc Mua ngay (Buy Now)**:
   - Khả dụng khi phiên có cấu hình `BuyNowPrice` và giá hiện tại chưa vượt ngưỡng Mua ngay.
   - Khi Người mua bấm Mua ngay, hệ thống khóa phần tiền hàng còn lại (`BuyNowPrice - Tiền cọc`) qua Ví Nexus, kết thúc phiên ngay lập tức (`Ended`), hoàn cọc cho tất cả các bidder khác, tạo đơn hàng 24 giờ cho người mua, gửi thông báo thắng phiên cho người mua, kết quả cho Người bán và thông báo chia buồn cho các bidder khác.

---

### Bước 4: Kết thúc phiên đấu giá, Xử lý Giá sàn & Quy tắc "Chỉ giữ cọc Top 2"
Khi đồng hồ đếm ngược về 0, phiên đấu giá chính thức khép lại:

1. **Nếu cả phiên không có ai đặt giá**:
   - Phiên đấu giá kết thúc với trạng thái **Thất bại (`Failed`)**.
   - Hệ thống mở khóa trả lại tiền cọc cho tất cả những ai đã đăng ký cọc (nếu có), mở khóa sản phẩm để Người bán có thể tạo lại phiên đấu giá mới và gửi thông báo cho Người bán.
2. **Nếu có người đặt giá, nhưng giá cao nhất `< Giá sàn bí mật` (`ReservePrice`)**:
   - Settlement chuyển sang trạng thái **Chờ Người bán duyệt dưới giá sàn (`AwaitingSellerApproval`)** với thời hạn **12 giờ**.
   - Giữ cọc của Hạng 1 và mở khóa hoàn cọc cho các bidder còn lại.
   - Hệ thống gửi thông báo cho **Người bán** (`auction-reserve-approval-needed`) vào duyệt và gửi thông báo cho **Người mua Hạng 1** biết đang chờ Người bán xác nhận trong 12 giờ.
   - **Nếu Người bán Đồng ý bán dưới giá sàn:** Reset chuỗi từ chối về `0`, tạo đơn hàng (hạn thanh toán **24 giờ**), gửi thông báo thắng phiên cho Người mua Hạng 1.
   - **Nếu Người bán Từ chối hoặc quá hạn 12 giờ:** Phiên chuyển sang **`Failed`**, hoàn 100% cọc cho Hạng 1, mở khóa sản phẩm, gửi thông báo lý do từ chối cho Người mua Hạng 1. Người bán bị tăng `ConsecutiveDeclineCount` (lần 2 cảnh báo, **lần 3 liên tiếp bị khóa quyền đặt Giá sàn trong 14 ngày**).
3. **Nếu có người đặt giá và giá cao nhất `>= Giá sàn` (hoặc không có giá sàn)**:
   - Hệ thống xếp hạng những người tham gia dựa trên mức giá cao nhất của mỗi người (nếu hai người trả cùng mức giá thì người đặt sớm hơn xếp trên):
     - **Hạng 1 (Người thắng cuộc — Winner)**: Giành quyền mua sản phẩm với mức giá cao nhất đã đặt.
     - **Hạng 2 (Người về nhì — Runner-up)**: Là ứng viên dự phòng duy nhất nếu Hạng 1 bỏ kèo.
   - **Quy tắc giải phóng và giữ tiền cọc**:
     - Hệ thống **mở khóa trả lại 100% tiền cọc ngay lập tức cho toàn bộ những người từ Hạng 3 trở xuống** (vì họ chắc chắn không còn cơ hội mua và không cần phải bị giam tiền thêm).
     - Hệ thống **chỉ giữ lại tiền cọc của đúng 2 người là Hạng 1 (Người thắng) và Hạng 2 (Người về nhì)** cho đến khi đơn hàng ngã ngũ.
   - **Tạo đơn hàng & Thông báo**:
     - Hệ thống tự động tạo đơn hàng đấu giá cho Người thắng với **thời hạn thanh toán là 24 giờ** kể từ lúc phiên kết thúc.
     - Gửi `auction-winners` (In-App + Email) cho Hạng 1, `auction-seller-results` cho Người bán và thông báo chia buồn cho các bidder không thắng.

---

### Bước 5: Người thắng thanh toán & Người bán xác nhận giao hàng (Luồng thành công)
1. **Khấu trừ tiền cọc vào đơn hàng**:
   - Khi Người thắng vào thanh toán đơn hàng, số tiền cọc đã đóng trước đó được **trừ thẳng vào giá trúng đấu giá**. Người thắng chỉ phải trả phần tiền còn thiếu cộng với phí giao hàng:
     $$\text{Số tiền cần thanh toán thêm} = (\text{Giá trúng đấu giá} - \text{Tiền cọc}) + \text{Phí vận chuyển}$$
   - *Ví dụ*: Anh A trúng đấu giá chiếc đồng hồ ở mức `2.000.000 VNĐ`, trước đó đã cọc `200.000 VNĐ`, phí ship GHN là `45.000 VNĐ`. Khi thanh toán, anh A chỉ cần trả thêm `1.800.000 + 45.000 = 1.845.000 VNĐ`.
2. **Khi Người thắng thanh toán thành công trong vòng 24 giờ**:
   - Khoản tiền cọc `200.000 VNĐ` được chính thức thu vào quỹ giữ tiền trung gian của đơn hàng (cùng với `1.800.000 VNĐ` vừa trả thêm, đủ `2.000.000 VNĐ` tiền hàng), gửi thông báo `auction-order-payments` cho cả Người mua & Người bán.
   - **Phần thưởng hành vi tốt**: Nếu trước đó Người thắng từng bị ghi nhận chuỗi vi phạm bỏ kèo (ví dụ đang có chuỗi vi phạm = 1 hoặc 2), ngay khi thanh toán đúng hạn, **chuỗi vi phạm của họ được xóa sạch về `0`**.
3. **Người bán xác nhận giao hàng trong vòng 24 giờ**:
   - Sau khi Người thắng đã trả tiền, Người bán có **24 giờ** để bấm xác nhận chuẩn bị hàng và lên vận đơn giao hàng.
   - Ngay khi Người bán bấm xác nhận giao hàng:
     1. Phiên đấu giá chính thức **Hoàn tất thành công (`Settled`)**.
     2. Sản phẩm được đánh dấu **Đã bán đấu giá vĩnh viễn**.
     3. **Tiền cọc đang giữ của Người về nhì (Hạng 2) lập tức được mở khóa trả về ví cho Người về nhì**.
4. **Giao hàng hoàn tất & Chia tiền cho Người bán**:
   - Khi đơn hàng giao đến tay Người mua thành công, sàn thu **5% phí hoa hồng nền tảng** trên giá trúng đấu giá và chuyển **95% giá trúng đấu giá** về ví cho Người bán (ví dụ: đơn `2.000.000 VNĐ` thì Người bán nhận về `1.900.000 VNĐ`, sàn thu `100.000 VNĐ`).

---

### Bước 6: Các tình huống Bùng kèo (Vi phạm) & Luồng chuyển giao cho Người về nhì

#### Tình huống 6.1: Người thắng (Hạng 1) bỏ kèo không thanh toán sau 24 giờ
Nếu hết 24 giờ kể từ lúc thắng đấu giá mà Người thắng không thanh toán đơn hàng:
1. **Hủy đơn của Người thắng & Tịch thu 100% tiền cọc**:
   - Khoản tiền cọc của Người thắng bị tịch thu toàn bộ và chia theo tỷ lệ mặc định:
     - **70% tiền cọc**: Chuyển thẳng vào ví của **Người bán** để bồi thường thiệt hại vì bị giam hàng và lỡ phiên đấu giá.
     - **30% tiền cọc**: Thu về **Sàn** làm phí vận hành xử lý vi phạm.
   - Gửi thông báo quá hạn (`auction-payment-timeouts` cho Buyer và `auction-seller-payment-timeouts` cho Seller).
2. **Trừ điểm uy tín & Tăng chuỗi vi phạm của Người thắng**:
   - Người thắng bị tăng thêm 1 nấc trong **Chuỗi vi phạm liên tiếp** và bị trừ điểm uy tín (nếu chạm mốc lần 3 sẽ bị tạm khóa quyền đấu giá 7 ngày; lần 4 bị đình chỉ vô thời hạn — xem bảng tổng hợp ở Mục 4).
3. **Tự động xét Người về nhì (Hạng 2)**:
   - Ngay khi đơn của Người thắng bị hủy, hệ thống kiểm tra Người về nhì (Hạng 2) có đủ điều kiện và còn cọc `Authorized` hay không:
   - **Trường hợp giá của Người về nhì `>= Giá sàn` (hoặc phiên không có giá sàn):** Hệ thống tự động gửi **Lời mời mua lại cho Người về nhì** (`auction-winner-offers`), **thời hạn trả lời 24 giờ**, với giá mua bằng đúng **mức giá cao nhất mà chính Người về nhì đã trả**.
   - **Trường hợp giá của Người về nhì `< Giá sàn`:** Hệ thống chuyển settlement sang `AwaitingSellerApproval` (12 giờ) và **hỏi Người bán trước** (`auction-reserve-approval-needed`). Nếu Người bán đồng ý, lúc này mới gửi lời mời 24 giờ cho Người về nhì; nếu Người bán từ chối hoặc quá hạn 12 giờ, phiên chuyển `Failed` và hoàn cọc cho Người về nhì.

#### Tình huống 6.2: Ba cách phản hồi của Người về nhì (Hạng 2)
Khi nhận được lời mời mua lại trong 24 giờ, Người về nhì có toàn quyền lựa chọn:

1. **Cách 1 — Bấm "Từ chối mua" hoặc Không trả lời để lời mời tự hết hạn sau 24 giờ**:
   - Vì việc mua lại chỉ là quyền lợi tự nguyện chứ không phải nghĩa vụ bắt buộc, nếu Người về nhì từ chối hoặc không phản hồi:
     - **Người về nhì hoàn toàn KHÔNG bị phạt** (không mất cọc, không bị trừ điểm uy tín, không bị tính vi phạm).
     - Hệ thống lập tức **mở khóa trả lại 100% tiền cọc về ví cho Người về nhì**.
     - Phiên đấu giá kết thúc ở trạng thái **Thất bại (`Failed`)** (hệ thống không mời tiếp người thứ 3) và mở khóa sản phẩm để Người bán có thể tạo lại phiên đấu giá mới.
2. **Cách 2 — Bấm "Đồng ý mua" và Thanh toán đúng hạn trong 24 giờ**:
   - Hệ thống lập tức tạo đơn hàng mới cho Người về nhì với **hạn thanh toán 24 giờ**, đồng thời gửi thông báo `auction-winners` (In-App + Email) cho Người về nhì và `auction-seller-results` cho Người bán.
   - Khoản tiền cọc đang giữ của Người về nhì được **trừ thẳng vào số tiền đơn hàng** (Người về nhì chỉ cần trả phần còn thiếu + phí vận chuyển).
   - Khi thanh toán xong và Người bán xác nhận giao hàng, đơn hàng hoàn tất như bình thường và chuỗi vi phạm trước đó của Người về nhì (nếu có) cũng được xóa về `0`.
3. **Cách 3 — Đã bấm "Đồng ý mua" nhưng sau đó lại bỏ kèo không thanh toán trong 24 giờ**:
   - Một khi đã chủ động bấm **"Đồng ý mua"** để hệ thống tạo đơn hàng và giữ hàng cho mình mà lại không thanh toán sau 24 giờ, Người về nhì được xem là **bùng kèo có chủ đích** và chịu hình phạt giống hệt Người thắng:
     1. **Tịch thu 100% tiền cọc của Người về nhì** (chia **70% cho Người bán / 30% cho Sàn**).
     2. **Tăng chuỗi vi phạm liên tiếp + Trừ điểm uy tín của Người về nhì** (áp dụng đầy đủ các mốc phạt cảnh báo, tăng tỷ lệ cọc +10%, tạm khóa 7 ngày hoặc đình chỉ vô thời hạn).
     3. Phiên đấu giá chuyển sang **Thất bại (`Failed`)** và mở khóa sản phẩm cho Người bán.

#### Tình huống 6.3: Người mua đã trả đủ tiền nhưng Người bán từ chối giao hàng (hoặc ngâm đơn quá 24 giờ)
Nếu lỗi thuộc về phía Người bán (Người mua đã thanh toán xong xuôi nhưng Người bán bấm từ chối giao hoặc quá 24 giờ không xác nhận đơn):
1. **Bảo vệ tuyệt đối Người mua — Hoàn trả 100% cả Tiền hàng lẫn Tiền cọc**:
   - Hệ thống hoàn trả lại ngay vào ví Người mua **100% số tiền đơn hàng + phí vận chuyển** mà Người mua đã thanh toán.
   - Đồng thời hoàn trả lại ngay vào ví Người mua **100% số tiền đặt cọc** ban đầu (và mở khóa trả lại cọc cho Người về nhì nếu đang giữ). Người mua không mất bất kỳ một đồng nào.
2. **Xử phạt Người bán**:
   - Người bán bị **trừ ngay `5 điểm` uy tín**. Nếu việc hủy đơn làm điểm uy tín của Người bán rớt xuống dưới ngưỡng 50 điểm, Người bán sẽ bị khóa quyền mở phiên đấu giá mới trên sàn.
3. **Đóng phiên**:
   - Phiên đấu giá chuyển sang trạng thái **Thất bại (`Failed`)** và mở khóa sản phẩm.

---

## 4. BẢNG TỔNG HỢP NHANH CÁC CON SỐ QUY ĐỊNH (TRA CỨU NHANH)

### 4.1. Bảng Quy Định Về Thời Gian
| Sự kiện / Giai đoạn | Thời gian quy định |
| :--- | :---: |
| Đặt lịch mở phiên trước tối đa | **30 ngày** |
| Thời lượng diễn ra một phiên đấu giá | **Từ 1 giờ (60 phút) đến tối đa 7 ngày (168 giờ)** |
| Gia hạn khi có người trả giá trong 5 phút cuối | **Cộng thêm 5 phút / lần** (Tối đa **10 lần** = **50 phút**) |
| Thời hạn Người bán duyệt bán dưới giá sàn | **12 giờ** kể từ lúc kết thúc phiên (hoặc lúc Hạng 1 bỏ kèo) |
| Thời hạn Người thắng thanh toán đơn hàng | **24 giờ** kể từ lúc kết thúc phiên / được Seller duyệt |
| Thời hạn Người về nhì trả lời lời mời mua lại | **24 giờ** kể từ lúc nhận lời mời |
| Thời hạn Người về nhì thanh toán sau khi bấm Đồng ý | **24 giờ** kể từ lúc bấm Đồng ý |
| Thời hạn Người bán xác nhận giao hàng sau khi khách trả tiền | **24 giờ** kể từ lúc khách thanh toán |

### 4.2. Bảng Quy Định Về Tiền Cọc & Phí Sàn
| Hạng mục tài chính | Mức áp dụng |
| :--- | :--- |
| **Tỷ lệ đóng cọc theo Điểm uy tín** | • Uy tín `80–100 điểm`: **5%** giá khởi điểm<br>• Uy tín `60–79 điểm`: **10%** giá khởi điểm<br>• Uy tín `40–59 điểm`: **15%** giá khởi điểm<br>• Uy tín `0–39 điểm` hoặc **Khách mới**: **20%** giá khởi điểm |
| **Phụ thu cọc nếu đang có chuỗi bỏ kèo $\ge 2$ lần** | Cộng thêm **+10%** tỷ lệ cọc (Tổng tỷ lệ cọc tối đa không quá **30%**) |
| **Số tiền cọc tối thiểu** | **`50.000 VNĐ`** (hoặc bằng giá khởi điểm nếu giá khởi điểm $< 50.000$ VNĐ) |
| **Những ai bị giữ cọc sau khi phiên kết thúc** | Chỉ giữ của **Hạng 1 (Người thắng)** và **Hạng 2 (Người về nhì)**; mở khóa ngay cho Hạng 3 trở xuống |
| **Cách chia tiền cọc khi tịch thu do khách bỏ kèo** | **70% chuyển cho Người bán** / **30% thuộc về Sàn** |
| **Phí hoa hồng sàn khi đơn đấu giá giao thành công** | **5% cho Sàn** / **95% giải ngân cho Người bán** |

### 4.3. Bảng Mức Phạt Khi Vi Phạm Cam Kết Đấu Giá
| Đối tượng & Lần vi phạm | Mất tiền cọc | Điểm uy tín bị trừ | Hình phạt bổ sung trên tài khoản |
| :--- | :---: | :---: | :--- |
| **Người mua bỏ kèo lần 1 (`Chuỗi vi phạm = 1`)** | Mất 100% cọc | **Trừ 5 điểm** | Nhắc nhở cảnh báo |
| **Người mua bỏ kèo lần 2 liên tiếp (`Chuỗi vi phạm = 2`)** | Mất 100% cọc | **Trừ 10 điểm** | **Tăng mức cọc thêm +10%** ở các phiên đấu giá sau |
| **Người mua bỏ kèo lần 3 liên tiếp (`Chuỗi vi phạm = 3`)** | Mất 100% cọc | **Trừ 15 điểm** | **Tạm khóa quyền tham gia đấu giá trong 7 ngày** |
| **Người mua bỏ kèo lần 4 liên tiếp (`Chuỗi vi phạm >= 4`)** | Mất 100% cọc | **Trừ 20 điểm** | **Đình chỉ quyền đấu giá vô thời hạn** (Chỉ Admin mới mở lại được) |
| **Người mua thanh toán đúng hạn 1 phiên thắng** | Không mất (Trừ vào tiền mua hàng) | Cộng 1 điểm khi nhận hàng xong | **Xóa chuỗi vi phạm liên tiếp về `0`** |
| **Người về nhì từ chối lời mời hoặc không trả lời** | **Không mất cọc** (Hoàn lại 100%) | **Không trừ điểm** | **Không tính là vi phạm** |
| **Người bán từ chối bán dưới giá sàn** | Hoàn 100% cọc cho Người mua | Không trừ điểm | Lần 2 cảnh báo; **Lần 3 liên tiếp khóa quyền đặt Giá sàn trong 14 ngày** |
| **Người bán từ chối giao hàng / Quá 24h không duyệt đơn** | Hoàn 100% tiền & cọc cho Người mua | **Trừ 5 điểm Người bán** | Nếu uy tín Người bán xuống dưới **50 điểm** $\rightarrow$ **Cấm mở phiên đấu giá mới** |
