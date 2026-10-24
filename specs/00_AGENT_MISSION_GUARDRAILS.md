# 00 — NHIỆM VỤ CỐT LÕI & RÀO CHẮN CHỐNG ẢO GIÁC (AUCTION SERVICE)
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có — 0/5 rpc gRPC, 0 controller, 0 event, 0 test. Bề mặt thật chỉ có `GET /health` (`Auction.Api/Program.cs:58`); 13 entity + 13 configuration + 1 migration đã có nhưng `SaveChangesAsync` 0 lời gọi.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
---
## 1. NHIỆM VỤ & RANH GIỚI MIỀN (BOUNDED CONTEXT)
**AuctionService** chịu trách nhiệm độc quyền về các phiên đấu giá trực tuyến và lượt đặt giá.
Bảng dưới tách rõ **đặc tả mục tiêu** và **thứ đã thật sự có trong code**:
| Nhiệm vụ (đặc tả) | Trạng thái | Bằng chứng / thiếu gì |
|---|---|---|
| Mô hình dữ liệu phiên đấu giá (13 entity: `Auction`, `AuctionState`, `AuctionConfig`, `AuctionExtension`, `AuctionResult`, `AuctionSettlement`, `AuctionStatusHistory`, `AuditLog`, `Bid`, `BidHistory`, `BidInvalidation`, `IdempotencyKey`, `ProductOpenAuction`) | ✅ CÓ | `Auction.Infrastructure/Persistence/AppDbContext.cs:14-26` |
| Truy vấn chi tiết phiên (`GetByIdAsync`) | ✅ CÓ (chỉ đọc) | `Auction.Application/Services/Auction/AuctionService.cs:20` |
| Truy vấn danh sách phiên (`ListActiveAuctionsAsync`) | ⚠️ MỘT PHẦN | `AuctionService.cs:39` — **không lọc theo trạng thái** dù tên là "Active"; chỉ phân trang |
| Truy vấn lượt bid theo phiên (`ListBidsByAuctionIdAsync`) | ✅ CÓ (chỉ đọc) | `Auction.Application/Services/Bid/BidService.cs:20` |
| Tạo phiên đấu giá (`CreateAuction`) — Seller, giá khởi điểm, bước giá, thời gian | ❌ CHƯA CÓ | chỉ có DTO `CreateAuctionDto` (`AuctionDtos.cs:15`) **0 nơi dùng**; không có method, không có `SaveChangesAsync` |
| Đặt giá (`PlaceBid`) — kiểm tra giá/bước giá/thời hạn, chống race | ❌ CHƯA CÓ | chỉ có DTO `PlaceBidDto` (`BidDtos.cs:13`) **0 nơi dùng**; 0 `throw new` toàn repo |
| Tự động đóng phiên khi hết hạn (Background Timer / Quartz.NET), xác định người thắng, bắn sự kiện | ❌ CHƯA CÓ | 0 `BackgroundService`/`IHostedService`, 0 package Quartz (grep = 0) |
| Cung cấp dịch vụ gRPC Server tại cổng `8005` cho ApiGateway | ❌ CHƯA CÓ | 0 file `.proto`, 0 package `Grpc.AspNetCore`, 0 class gRPC; port `8005` chỉ có trong `Dockerfile:3-4`. Chạy local là `5230`/`7202` (`Auction.Api/Properties/launchSettings.json:17,27`) |
---
## 2. NHỮNG ĐIỀU CẤM KỴ ĐỐI VỚI AI AGENTS (ANTI-HALLUCINATION GUARDRAILS)
1. **TUYỆT ĐỐI KHÔNG DÙNG REPOSITORY PATTERN BỌC NGOÀI EF CORE**:
   - Sử dụng trực tiếp `AppDbContext` và `DbSet<T>` trong tầng Application (qua `IAuctionDbContext`).
   - Không được tạo `IAuctionRepository`, `IBidRepository`, `UnitOfWork`.
   - ✅ Hiện trạng: 0 `*Repository`, 0 `UnitOfWork` trong repo (grep = 0). Giữ nguyên.
2. **CHỐNG XUNG ĐỘT ĐẶT GIÁ (RACE CONDITION BIDDING)**:
   - Khi có nhiều người cùng bid 1 lúc, phải dùng cơ chế Khóa bi quan (Pessimistic Lock: `SELECT ... FOR UPDATE`) hoặc Khóa lạc quan (Optimistic Concurrency với RowVersion) để đảm bảo không có 2 người cùng đặt thành công ở cùng 1 mức giá.
   - ⚠️ Hiện trạng: **chưa có cơ chế nào được dùng**. `AuctionState.RowVersion` (`Auction.Domain/Entities/AuctionState.cs:17`) đã khai báo và cấu hình `IsConcurrencyToken()` (`Auction.Infrastructure/Persistence/Configurations/AuctionStateConfiguration.cs:14`), nhưng **0 lời gọi `SaveChangesAsync`** ⇒ token chưa bao giờ được kiểm tra. Không có `FOR UPDATE` nào trong repo.
3. **SELLER KHÔNG ĐƯỢC TỰ BID TRÊN PHIÊN CỦA CHÍNH MÌNH**:
   - Nghiệp vụ cấm seller tự đặt giá để đẩy giá ảo (shill bidding). ❌ Chưa có code nào thực thi (use case `PlaceBid` chưa tồn tại).
4. **TUYỆT ĐỐI KHÔNG THAY ĐỔI CỔNG 8005 VÀ DATABASE 5437**:
   - Service chạy tại `http://localhost:8005` **trong Docker** (`Dockerfile:3-4` EXPOSE/`ASPNETCORE_URLS`, `docker-compose.yml:37`).
   - ⚠️ Chạy local bằng `dotnet run` lại lên **`5230` (http) / `7202` (https)** (`Auction.Api/Properties/launchSettings.json:17,27`) — lệch spec. Muốn khớp: `ASPNETCORE_URLS=http://localhost:8005` hoặc `--no-launch-profile`.
   - Chuỗi kết nối DB trỏ tới `localhost:5437/auction_db` (`Auction.Api/appsettings.json:10`, fallback trong `Auction.Infrastructure/DependencyInjection.cs:15`).
5. **KHÔNG MÔ TẢ GIAO DIỆN KHÔNG TỒN TẠI**: repo **không có** `Auction.Api/Services/AuctionGrpcService.cs`, **không có** `Contracts/shop.proto` (0 file `.proto` toàn repo). Mọi câu "service cung cấp rpc X" phải kèm nhãn ❌ nếu X chưa có.