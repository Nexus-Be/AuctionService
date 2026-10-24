# AUCTION SERVICE — ARCHITECTURE & FOLDER STRUCTURE GUIDE
## (CẨM NANG CẤU TRÚC THƯ MỤC & PHÁT TRIỂN DÀNH CHO DEVELOPER VÀ AI AGENT)
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có bề mặt mạng — **0/5 rpc** gRPC, **0 controller**, **0 event** (0 publisher / 0 consumer), **0 test**. Bề mặt thật chỉ có 1 minimal endpoint `/health` (`Auction.Api/Program.cs:58`). 13 entity + 13 configuration + 1 migration đã có, nhưng `SaveChangesAsync` **0 lời gọi** trong toàn repo (chỉ khai báo ở `Auction.Application/Interfaces/IAuctionDbContext.cs:24`) và `AuctionService`/`BidService` **0 call site** ngoài DI.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
> **Thông tin dịch vụ**:
> - **Service Port**: `8005` — chỉ trong Docker (`Dockerfile:3-4`, `docker-compose.yml:37`). **Chạy local bằng `dotnet run` lại lên `5230` (http) / `7202` (https)** theo `Auction.Api/Properties/launchSettings.json:17,27` — lệch spec, xem mục 6.
> - **Database**: PostgreSQL (`auction_db`), Port máy host: `5437` ✅ (`docker-compose.yml:19`, `Auction.Api/appsettings.json:10`).
> - **Quy mô code**: 47 file `.cs` / 1.182 LOC (không tính `Migrations/`) — Application 8 file/198 LOC · Infrastructure 16/502 · Domain 21/327 · Api 2/155 · **0 test project**.
> - **Nhiệm vụ chính (đã có code)**: Mô hình dữ liệu phiên đấu giá (`Auction`), lượt đặt giá (`Bid`, `BidHistory`), trạng thái (`AuctionState`), gia hạn chống bắn tỉa (`AuctionExtension`), kết quả/thanh toán (`AuctionResult`, `AuctionSettlement`), cấu hình chính sách (`AuctionConfig`); 3 hàm truy vấn chỉ-đọc ở tầng Application.
> - **Nhiệm vụ đặc tả, CHƯA có code**: gRPC 5 rpc, tạo phiên, đặt giá, tự động đóng phiên, phát sự kiện — ❌ xem mục 6.
---
## 1. SƠ ĐỒ PHÂN TẦNG VÀ CẤU TRÚC THƯ MỤC CHUẨN
```
src/Services/Auction/
│
├── Auction.Domain/                                    # 1. TẦNG DOMAIN (POCO 100%, ZERO DEPENDENCY)
│   ├── Base/                                          # BaseEntity, AuditableEntity, IAuditable, IUserAuditable, ISoftDelete
│   ├── Entities/                                      # 13 entity: Auction, AuctionState, AuctionConfig, AuctionExtension,
│   │                                                  #   AuctionResult, AuctionSettlement, AuctionStatusHistory,
│   │                                                  #   AuditLog, Bid, BidHistory, BidInvalidation,
│   │                                                  #   IdempotencyKey, ProductOpenAuction
│   ├── Enums/                                         # 4 enum: AuctionStatus (7 giá trị), AuditAction,
│   │                                                  #   BidInvalidationReason, IdempotencyScope
│   └── Exceptions/                                    # DomainException, EntityNotFoundException, BusinessRuleValidationException
│
├── Auction.Application/                               # 2. TẦNG APPLICATION (USE CASES & BUSINESS LOGIC)
│   ├── Interfaces/
│   │   └── IAuctionDbContext.cs                       # 13 DbSet + SaveChangesAsync (chỉ khai báo)
│   │
│   ├── Services/                                      # CÁC DỊCH VỤ XỬ LÝ USE CASE (THEO TÍNH NĂNG)
│   │   ├── Auction/                                   # Module Quản lý Phiên đấu giá
│   │   │   ├── IAuctionService.cs                     # 2 hàm: GetByIdAsync, ListActiveAuctionsAsync
│   │   │   ├── AuctionService.cs                      # 2 truy vấn chỉ-đọc (39 + 20 dòng)
│   │   │   └── AuctionDtos.cs                         # AuctionDto, CreateAuctionDto (CreateAuctionDto chưa dùng)
│   │   └── Bid/                                       # Module Đặt giá & Lịch sử trả giá
│   │       ├── IBidService.cs                          # 1 hàm: ListBidsByAuctionIdAsync
│   │       ├── BidService.cs                          # 1 truy vấn chỉ-đọc (17 dòng)
│   │       └── BidDtos.cs                             # BidDto, PlaceBidDto (PlaceBidDto chưa dùng)
│   │
│   └── DependencyInjection.cs                         # Đăng ký AddApplicationServices() (2 service, 0 consumer)
│
├── Auction.Infrastructure/                            # 3. TẦNG INFRASTRUCTURE (CÔNG NGHỆ & HẠ TẦNG CỤ THỂ)
│   ├── Persistence/                                   # Quản lý CSDL PostgreSQL & EF Core 8
│   │   ├── AppDbContext.cs                            # Triển khai IAuctionDbContext (+ class AuctionDbContext trùng lặp, 0 ref)
│   │   ├── Configurations/                            # 13 Fluent API Configurations (*Configuration.cs)
│   │   ├── Migrations/                                # 1 migration: 20261024103234_InitialAuctionSchema
│   │   └── Seeds/                                     # AuctionSeedData: 5 bản ghi seed (Config, Auction, State, ProductOpen, Bid)
│   │
│   └── DependencyInjection.cs                         # AddInfrastructureServices(): DbContext + MassTransit/RabbitMQ + EF Outbox
│
└── Auction.Api/                                       # 4. TẦNG API / HOST (ENTRY POINT & MIDDLEWARE)
    ├── Middleware/                                    # MIDDLEWARE TOÀN CỤC CỦA SERVICE
    │   └── GlobalExceptionHandlerMiddleware.cs        # Bắt mọi lỗi, format RFC 9110 ProblemDetails (đang không có lỗi nào để bắt)
    ├── Properties/launchSettings.json                 # 2 profile chạy local: http 5230, https 7202
    ├── appsettings.json / appsettings.Development.json# ConnectionStrings, RabbitMq, Otel (section Otel chưa được code đọc)
    └── Program.cs                                     # Kestrel Http1AndHttp2 (cleartext), DI, Swagger (Dev), migrate-on-start, /health
```
⚠️ **Hai đường dẫn trong bản README cũ KHÔNG tồn tại** (đã xác minh `ls` + `find . -name "*.proto"` = 0 kết quả):
- `Auction.Api/Services/AuctionGrpcService.cs` — ❌ không tồn tại. Thư mục thật của `Auction.Api` chỉ có `Middleware/` và `Properties/`.
- `Auction.Api/Contracts/shop.proto` — ❌ không tồn tại. Trong repo **không có file `.proto` nào** và **không có package `Grpc.AspNetCore`** trong bất kỳ `.csproj` (grep `Grpc` trên `*.csproj` = 0 kết quả).
---
## 2. NGUYÊN TẮC PHÂN CHIA TRÁCH NHIỆM
1. **`Auction.Domain`**: Chứa các Entity phiên đấu giá, lượt bid, lịch sử đặt giá, kết quả và thanh toán. Chỉ 1 project reference duy nhất là chính nó (0 package ngoài — `Auction.Domain/Auction.Domain.csproj` không có `PackageReference` nào). ✅ Đang đúng.
   - ⚠️ **Ngoại lệ đã có trong code**: `Auction.Domain/Entities/AuditLog.cs` **không** kế thừa `BaseEntity<Guid>` (không có `UpdatedAt`/`IsDeleted`), trong khi 12 entity còn lại đều kế thừa.
   - ⚠️ **Domain hiện là POCO thuần dữ liệu**: 0 method nghiệp vụ trong `Auction.Domain/Entities/*.cs` (grep `public ...(` ngoài property = 0). Mọi bất biến đấu giá (giá > giá hiện tại + bước giá, seller không tự bid) **chưa được đóng gói ở đâu cả**.
2. **`Auction.Application`**: Chứa toàn bộ ca sử dụng (Use Cases). Mỗi tính năng nằm trong thư mục riêng `Services/{Feature}/` gồm DTOs, Interface và Implementation. Gọi CSDL qua `IAuctionDbContext`. ✅ Đang đúng về cấu trúc — nhưng chỉ có **3 hàm truy vấn chỉ-đọc**, không có use case ghi.
3. **`Auction.Infrastructure`**: Triển khai `AppDbContext` kế thừa `IAuctionDbContext`. Không dùng Generic Repository bọc ngoài EF Core (ADR 05). ✅ Đang đúng (0 `*Repository`, 0 `UnitOfWork` trong repo).
4. **`Auction.Api`**: Cổng tiếp nhận cực kỳ mỏng. Trang bị `GlobalExceptionHandlerMiddleware` để chuẩn hóa lỗi trả về cho client. ⚠️ Middleware có thật (`Auction.Api/Middleware/GlobalExceptionHandlerMiddleware.cs:9`) nhưng **chưa từng chạy**: repo không có `throw new` nào (grep toàn repo = 0) và không có endpoint nghiệp vụ nào.
---
## 3. QUY TRÌNH THÊM TÍNH NĂNG MỚI (DÀNH CHO DEV & AI AGENT)
1. **Bước 1**: Tạo Entity & Enum trong `Auction.Domain/Entities/`.
2. **Bước 2**: Tạo Configuration trong `Auction.Infrastructure/Persistence/Configurations/` và khai báo `DbSet<T>` trong `IAuctionDbContext` & `AppDbContext`.
3. **Bước 3**: Tạo thư mục tính năng trong `Auction.Application/Services/{Feature}/` gồm `I{Feature}Service.cs`, `{Feature}Service.cs`, và `{Feature}Dtos.cs`.
4. **Bước 4**: Đăng ký Service vào `Auction.Application/DependencyInjection.cs`.
5. **Bước 5**: Gọi Service trong tầng Api. ⚠️ **Điểm nối này chưa tồn tại**: bản cũ ghi `Auction.Api/Services/AuctionGrpcService.cs` — file đó ❌ không có. Muốn expose ra ngoài, phải tạo mới một trong hai (xem mục 6):
   - gRPC: thêm `Grpc.AspNetCore` + `services.AddGrpc()` + `app.MapGrpcService<...>()`, kèm file `.proto`;
   - REST: `app.MapGet/MapPost(...)` trong `Auction.Api/Program.cs`.
6. **Bước 6 (bắt buộc, đang bị thiếu)**: Trong use case ghi, gọi `_dbContext.SaveChangesAsync(...)` — hiện **0 lời gọi** trên toàn repo nên mọi thay đổi in-memory sẽ mất khi request kết thúc.
7. **Bước 7 (bắt buộc khi thêm migration)**: `dotnet ef migrations add <Name> --project Auction.Infrastructure --startup-project Auction.Api` (xem mục 5 — **không dùng** `./.tools/dotnet-ef`, đường dẫn này không tồn tại).
---
## 4. QUY CHUẨN THÔNG BÁO LỖI (MANDATORY ERROR RULES)
1. **100% English**: Tuyệt đối không dùng tiếng Việt.
2. **Sentence case**: Viết hoa duy nhất chữ cái đầu (`"Entity 'Auction' with key '...' was not found."`).
3. **Error Code SCREAMING_SNAKE_CASE**: mã lỗi do `GlobalExceptionHandlerMiddleware` sinh ra. **Đây là danh sách mã thật đang có trong code** (`Auction.Api/Middleware/GlobalExceptionHandlerMiddleware.cs:36-63`) — không phải ví dụ minh hoạ:
| Exception nguồn | HTTP | `errorCode` | `title` | Trạng thái | Bằng chứng |
|---|---|---|---|---|---|
| `EntityNotFoundException` | 404 | `NOT_FOUND` | `Resource not found` | ✅ CÓ | `GlobalExceptionHandlerMiddleware.cs:38-42` |
| `BusinessRuleValidationException` | 400 | `BUSINESS_RULE_VIOLATION` | `Business rule violation` | ✅ CÓ | `GlobalExceptionHandlerMiddleware.cs:43-47` |
| `DomainException` | 400 | `DOMAIN_ERROR` | `Domain error` | ✅ CÓ | `GlobalExceptionHandlerMiddleware.cs:48-52` |
| `ArgumentException` | 400 | `VAL_INVALID_ARGUMENT` | `Invalid argument` | ✅ CÓ | `GlobalExceptionHandlerMiddleware.cs:53-57` |
| mọi exception khác | 500 | `INTERNAL_SERVER_ERROR` | `An unexpected error occurred` | ✅ CÓ | `GlobalExceptionHandlerMiddleware.cs:58-62` |
> ⚠️ **Các mã `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW` trong bản README cũ KHÔNG tồn tại trong code** (grep = 0 kết quả). Chúng chỉ là đặc tả mục tiêu cho use case đặt giá chưa được viết.
> ⚠️ 3 class exception domain (`DomainException`, `EntityNotFoundException`, `BusinessRuleValidationException` — `Auction.Domain/Exceptions/DomainException.cs:3,9,15`) hiện **0 lần được ném** (`grep "throw new"` = 0 trên toàn repo). Middleware chỉ có thể bắt `ArgumentException` từ `Skip((page-1)*pageSize)` nếu page ≤ 0 (xem mục 6).
---
## 5. CÁCH CHẠY VÀ KIỂM THỬ ĐỘC LẬP SERVICE
```bash
# 1. Bật database của Auction Service (Port 5437):
cd src/Services/Auction
docker compose up -d auction-db
# 2. Tạo & Cập nhật migration (dùng dotnet-ef global/local tool — KHÔNG có ./.tools/dotnet-ef):
dotnet ef migrations add InitialAuctionSchema --project Auction.Infrastructure --startup-project Auction.Api
dotnet ef database update --project Auction.Infrastructure --startup-project Auction.Api
# 3. Chạy service (local: http 5230 / https 7202; muốn đúng 8005 thì ép URL):
dotnet run --project Auction.Api
# hoặc: ASPNETCORE_URLS=http://localhost:8005 dotnet run --project Auction.Api
# 4. Kiểm tra bề mặt thật (chỉ có /health):
curl http://localhost:5230/health   # {"status":"Healthy","service":"Auction.Api"}
```
| Lệnh / đường dẫn trong bản cũ | Trạng thái | Ghi chú |
|---|---|---|
| `./.tools/dotnet-ef` | ❌ KHÔNG tồn tại | repo không có thư mục `.tools/` và không có `dotnet-tools.json` → dùng `dotnet ef` |
| `docker compose up -d auction-db` | ✅ CÓ | service `auction-db` (`docker-compose.yml:11-28`) |
| `dotnet run --project Auction.Api` | ✅ CÓ | nhưng port local là `5230`/`7202`, không phải `8005` |
| `dotnet build Auction.sln -c Release` | ✅ CÓ | solution thật tên `Auction.sln` (không phải `AuctionService.sln`) |
| `dotnet test Auction.sln -c Release` | ❌ KHÔNG có gì để chạy | 0 test project trong solution |
| Migration tự áp khi khởi động | ✅ CÓ | `Program.cs:42-56` gọi `db.Database.Migrate()`, lỗi thì chỉ `LogWarning` (không chặn khởi động) |
---
## 6. BỀ MẶT THẬT ĐANG CHẠY & CÁC ĐIỂM LỆCH SPEC (đối chiếu 2026-10-24)
### 6.1. Bề mặt mạng
| Thành phần | Trạng thái | Bằng chứng |
|---|---|---|
| Minimal endpoint `/health` | ✅ CÓ (duy nhất) | `Auction.Api/Program.cs:58` |
| Controller (`[ApiController]`, `ControllerBase`) | ❌ CHƯA CÓ (0 file) | không có thư mục `Controllers/` |
| gRPC server (5 rpc theo `specs/02`) | ❌ CHƯA CÓ (0/5) | 0 file `.proto`, 0 package `Grpc.AspNetCore`, 0 class kế thừa `*Base` |
| Swagger UI | ⚠️ MỘT PHẦN | bật ở `Program.cs:23-24,35-39` nhưng chỉ thấy `/health` (không có endpoint nghiệp vụ) |
| Cổng Docker `8005` | ✅ CÓ | `Dockerfile:3-4`, `docker-compose.yml:37` |
| Cổng local `5230`/`7202` | ⚠️ LỆCH SPEC | `Auction.Api/Properties/launchSettings.json:17,27` |
### 6.2. Tầng Application — 3 hàm, 0 use case ghi
| Thành phần | Trạng thái | Bằng chứng |
|---|---|---|
| `AuctionService.GetByIdAsync` | ✅ CÓ (đọc) | `Auction.Application/Services/Auction/AuctionService.cs:20` |
| `AuctionService.ListActiveAuctionsAsync` | ⚠️ MỘT PHẦN | `AuctionService.cs:39` — **không lọc trạng thái** dù tên là "Active": chỉ `OrderByDescending(ScheduledStartAt).Skip().Take()`, không `Where` |
| `BidService.ListBidsByAuctionIdAsync` | ✅ CÓ (đọc) | `Auction.Application/Services/Bid/BidService.cs:20` |
| `CreateAuction` / `PlaceBid` (use case ghi) | ❌ CHƯA CÓ | `CreateAuctionDto` (`AuctionDtos.cs:15`) và `PlaceBidDto` (`BidDtos.cs:13`) được khai báo nhưng **0 nơi sử dụng** |
| `AuctionService`, `BidService` có được gọi? | ❌ 0 call site | đăng ký DI ở `Auction.Application/DependencyInjection.cs:11-12`, nhưng không có consumer nào resolve |
| `SaveChangesAsync` | ❌ 0 lời gọi | chỉ khai báo ở `IAuctionDbContext.cs:24` |
### 6.3. Rủi ro đã xác minh trong code
1. **`page = 0` ném `ArgumentException` thật**: `Skip((page - 1) * pageSize)` tại `AuctionService.cs:44` không validate `page`/`pageSize`, nên `page=0` → `Skip(-20)` → `ArgumentException`. Không có FluentValidation/ModelState nào chặn trước. Middleware sẽ trả 400 `VAL_INVALID_ARGUMENT`.
2. **Kestrel không thể phục vụ gRPC cleartext**: `Program.cs:10-16` đặt `HttpProtocols.Http1AndHttp2` trên endpoint **không TLS**. Không có ALPN thì Kestrel chỉ đàm phán HTTP/1.1 → thêm `MapGrpcService` cũng sẽ fail. Muốn h2c phải đặt `HttpProtocols.Http2` riêng cho endpoint gRPC (kèm `AddGrpc`) hoặc bật TLS.
3. **MassTransit đã dựng nhưng rỗng**: `AddMassTransit` + RabbitMQ + EF Outbox (`Auction.Infrastructure/DependencyInjection.cs:23-48`), `ConfigureEndpoints` không có consumer nào → **0 publisher, 0 consumer, 0 event class** trong repo. Outbox kéo theo 3 bảng `InboxState`/`OutboxState`/`OutboxMessage` trong migration.
4. **`OTEL_EXPORTER_OTLP_ENDPOINT` được set nhưng vô dụng**: `docker-compose.yml:47` và section `Otel` trong `appsettings.json:18-20` không được code nào đọc; repo **không có** `AddOpenTelemetry` (grep = 0).
5. **Không có auth**: 0 `AddAuthentication`/`JwtBearer` (grep = 0) → mọi endpoint (nếu thêm) sẽ anonymous; không có chỗ đọc `x-trace-id`, chỉ dùng `Activity.Current?.Id` của middleware.
6. **`AuctionDbContext` là class chết**: `AppDbContext.cs:41` khai báo `AuctionDbContext : AppDbContext` nhưng 0 reference (DI chỉ dùng `AppDbContext` — `DependencyInjection.cs:17,20`).
7. **3 entity không có đường dùng**: `BidInvalidation`, `AuditLog`, `IdempotencyKey` chỉ xuất hiện ở `DbSet` (`AppDbContext.cs:18,25,26`) — 0 truy vấn/ghi ngoài khai báo.
### 6.4. Việc phải làm trước khi coi Auction "xong"
Theo mục 4: (AU-01) dựng gRPC surface 5 rpc · (AU-02) test `PlaceBid` · (AU-03) event + publisher · (AU-04) đồng bộ `dbml` (bảng `SagaStates` không có trong code).