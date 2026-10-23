# 00 — NHIỆM VỤ CỐT LÕI & RÀO CHẮN CHỐNG ẢO GIÁC (AUCTION SERVICE)

---

## 1. NHIỆM VỤ & RANH GIỚI MIỀN (BOUNDED CONTEXT)

**AuctionService** chịu trách nhiệm độc quyền về các phiên đấu giá trực tuyến và lượt đặt giá:
- Tạo phiên đấu giá (`CreateAuction`): Dành cho Seller, xác định giá khởi điểm (`StartingPrice`), bước giá tối thiểu (`StepPrice`), thời gian bắt đầu và kết thúc (`StartTime`, `EndTime`).
- Xử lý đặt giá (`PlaceBid`): Dành cho người mua (`authenticated`), kiểm tra tính hợp lệ của số tiền bid so với giá hiện tại và bước giá, kiểm tra thời hạn phiên.
- Tra cứu danh sách và chi tiết phiên đấu giá, lịch sử các lượt bid theo thứ tự thời gian.
- Tự động đóng phiên khi hết hạn (Background Timer / Quartz.NET) $\rightarrow$ Xác định người thắng cuộc với giá bid cao nhất $\rightarrow$ Bắn sự kiện kết thúc.
- Cung cấp dịch vụ gRPC Server tại cổng `8005` cho ApiGateway.

---

## 2. NHỮNG ĐIỀU CẤM KỴ ĐỐI VỚI AI AGENTS (ANTI-HALLUCINATION GUARDRAILS)

1. **TUYỆT ĐỐI KHÔNG DÙNG REPOSITORY PATTERN BỌC NGOÀI EF CORE**:
   - Sử dụng trực tiếp `AuctionDbContext` và `DbSet<T>` trong tầng Application.
   - Không được tạo `IAuctionRepository`, `IBidRepository`, `UnitOfWork`.

2. **CHỐNG XUNG ĐỘT ĐẶT GIÁ (RACE CONDITION BIDDING)**:
   - Khi có nhiều người cùng bid 1 lúc, phải dùng cơ chế Khóa bi quan (Pessimistic Lock: `SELECT ... FOR UPDATE`) hoặc Khóa lạc quan (Optimistic Concurrency với RowVersion) để đảm bảo không có 2 người cùng đặt thành công ở cùng 1 mức giá.

3. **SELLER KHÔNG ĐƯỢC TỰ BID TRÊN PHIÊN CỦA CHÍNH MÌNH**:
   - Nghiệp vụ cấm seller tự đặt giá để đẩy giá ảo (shill bidding).

4. **TUYỆT ĐỐI KHÔNG THAY ĐỔI CỔNG 8005 VÀ DATABASE 5437**:
   - Service gRPC chạy tại `http://localhost:8005`.
   - Chuỗi kết nối DB trỏ tới `localhost:5437/auction_db` với mật khẩu mặc định `MatkhaucuaTui123@`.
