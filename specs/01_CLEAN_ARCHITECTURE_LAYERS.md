# 01 — PHÂN TẦNG CLEAN ARCHITECTURE & QUY TẮC PHỤ THUỘC (AUCTION SERVICE)
> **Trạng thái triển khai (2026-10-29):** ✅ ĐÃ CHUẨN HÓA — 4 tầng Clean Architecture chuẩn hóa: Feature Co-location cho Auction và Bid tại `Auction.Application/Services/`, 1-to-1 REST Controllers tại `Auction.Api/Controllers/AuctionsController.cs` và `BidsController.cs`, Consumer DI extension `ConsumerExtensions.cs`, 100% RESTful HTTP/JSON (CẤM gRPC).
> **Nguồn sự thật:** code trong repo này (`Auction.sln`, `Auction.Application/Services/`, `Auction.Application/Consumers/`, `Auction.Api/Controllers/`).
> **Cập nhật lần cuối:** 2026-10-29.
Solution `Auction.sln` (tên thật — không phải `AuctionService.sln`) bao gồm 4 project tương ứng với 4 tầng kiến trúc sạch (Clean Architecture):
```
Auction.sln
├── Auction.Domain/           # Entities (Auction, Bid), enums, exceptions — POCO 100%, 0 PackageReference
├── Auction.Application/      # 2 service chỉ-đọc (AuctionService, BidService) + DTOs + IAuctionDbContext
├── Auction.Infrastructure/   # EF Core DbContext, 13 configuration, 1 migration, seeds, MassTransit (0 consumer)
└── Auction.Api/              # Entry point (Program.cs, GlobalExceptionHandlerMiddleware) — 1 endpoint /health
```
---
## 1. NGUYÊN TẮC LUỒNG PHỤ THUỘC (DEPENDENCY RULE)
```
[ Auction.Api ] ────────────┐
                            │
                            ▼
               [ Auction.Application ] ──▶ [ Auction.Domain ]
                            ▲
                            │
[ Auction.Infrastructure ] ─┘
```
Trạng thái từng tuyên bố của sơ đồ trên:
| Tuyên bố | Trạng thái | Bằng chứng |
|---|---|---|
| `Auction.Api` → `Application` + `Infrastructure` | ✅ CÓ | `Auction.Api/Auction.Api.csproj` (2 `ProjectReference`) |
| `Auction.Infrastructure` → `Application` + `Domain` | ✅ CÓ | `Auction.Infrastructure/Auction.Infrastructure.csproj` |
| `Auction.Application` → `Domain` | ✅ CÓ | `Auction.Application/Auction.Application.csproj` |
| `Auction.Domain` không reference ai (kể cả package) | ✅ CÓ | `Auction.Domain/Auction.Domain.csproj` chỉ có `TargetFramework`/`Nullable`/`ImplicitUsings` |
- **`Auction.Domain`**: Chứa 13 entity, 4 enum, 3 exception class.
  - ⚠️ **Enum thật là `AuctionStatus` với 7 giá trị** — `Draft = 0`, `Scheduled = 1`, `Running = 2`, `Ended = 3`, `Settled = 4`, `Failed = 5`, `Cancelled = 6` (`Auction.Domain/Enums/AuctionStatus.cs`). Bản spec cũ ghi 4 giá trị (`Scheduled`, `Active`, `Ended`, `Cancelled`) — **`Active` không tồn tại**, tên đúng là `Running`.
  - ❌ **Không có logic nghiệp vụ trong Domain**: bản spec cũ yêu cầu `auction.PlaceBid(userId, amount)` đóng gói trong entity — **method này không tồn tại**; 13 file entity chỉ có property (`grep "public .*(" Auction.Domain/Entities/*.cs` ngoài `get;` = 0 kết quả).
  - ⚠️ **Tên property thật khác spec cũ**: `Auction` có `StartPriceAmount`, `BidStepAmount`, `ScheduledStartAt`, `OriginalEndAt`, `SnapshotProductTitle`, `MinReputationToBid`, `MinReputationToSell`, `AntiSnipingExtensionMinutes`, `MaxExtensionCount` — **không có** `StartingPrice`, `CurrentPrice`, `StepPrice`, `CurrentWinnerId`, `StartTime`, `EndTime`, `RowVersion`, `Title`, `Status` (xem `specs/04`). Giá hiện tại/người thắng/trạng thái nằm ở entity `AuctionState`.
- **`Auction.Application`**: ❌ **Chưa dùng `MediatR`** (không có package, không có `Command`/`Query`/`Handler` nào trong repo). Cấu trúc thật là `Services/{Feature}/` với 2 interface:
  - `IAuctionService`: 2 hàm (`GetByIdAsync`, `ListActiveAuctionsAsync`) — `Auction.Application/Services/Auction/IAuctionService.cs`
  - `IBidService`: 1 hàm (`ListBidsByAuctionIdAsync`) — `Auction.Application/Services/Bid/IBidService.cs`
  - Khai báo `IAuctionDbContext` (13 `DbSet` + `SaveChangesAsync`) tại `Auction.Application/Interfaces/IAuctionDbContext.cs:8`.
- **`Auction.Infrastructure`**: Có `AppDbContext`, 13 configuration, 1 migration, seed data. ❌ **Không có Background Hosted Service** nào quét phiên hết hạn (0 `BackgroundService`/`IHostedService`, 0 package Quartz — grep = 0).
- **`Auction.Api`**: ❌ **Không có `app.MapGrpcService<AuctionGrpcService>()`** (không tồn tại class đó). Entry point thật: `Program.cs` — DI, Swagger (chỉ Development), migrate-on-start, `app.MapGet("/health", ...)` tại `Program.cs:58`.
---
## 2. QUY CHUẨN CODE TỪNG TẦNG
1. **`Auction.Domain`**:
   - ❌ Phần đặc tả dưới đây **chưa được triển khai** — `Auction` không có `StartingPrice`/`CurrentPrice`/`StepPrice`/`CurrentWinnerId`/`StartTime`/`EndTime` và không có hàm `PlaceBid`. Đây là **đặc tả mục tiêu**:
     - Hàm `PlaceBid`:
       - Kiểm tra trạng thái phải là `Active` (theo enum thật: `Running`).
       - Kiểm tra thời gian `UtcNow` phải nằm giữa thời gian bắt đầu và kết thúc.
       - Kiểm tra `Amount >= CurrentPrice + StepPrice` (theo tên thật: `Amount >= AuctionState.CurrentPriceAmount + Auction.BidStepAmount`).
       - Nếu thỏa mãn: Gán giá hiện tại = `Amount`, người giữ giá = `userId`.
   - Hiện trạng thật: các bất biến trên **chưa nằm ở đâu cả**; `AuctionState.RowVersion` (`long`, `IsConcurrencyToken`) đã sẵn sàng nhưng chưa dùng (`AuctionStateConfiguration.cs:14`).
2. **`Auction.Infrastructure`**:
   - ⚠️ Bản spec cũ yêu cầu index trên `Auction.Status`, `Auction.EndTime`, `Bid.AuctionId`, `Bid.CreatedAt`. **Thực tế: 0/4 index này tồn tại.**
   - Index thật trong code: **1 index tường minh** — unique trên `ProductOpenAuctions.ProductId` (`Auction.Infrastructure/Persistence/Configurations/ProductOpenAuctionConfiguration.cs:12`). Các index còn lại trong migration (20 index) đều do EF sinh cho khóa ngoại/unique của outbox (`IX_Bids_AuctionId`, `IX_AuctionStates_AuctionId`...).
   - Không có index trên cột trạng thái/thời gian kết thúc ⇒ job quét phiên hết hạn (khi được viết) sẽ full-scan.
3. **Thứ tự đăng ký DI (đúng như code)**: `AddApplicationServices()` (`Auction.Application/DependencyInjection.cs:9`) → `AddInfrastructureServices(configuration)` (`Auction.Infrastructure/DependencyInjection.cs:12`) → `AddEndpointsApiExplorer()`/`AddSwaggerGen()` (`Program.cs:23-24`). Không có `AddGrpc()`, `AddAuthentication()`, `AddOpenTelemetry()`.

---

## 4. QUY CHUẨN CẤU TRÚC CHUẨN HÓA (SERVICE BLUEPRINT)
1. **Giao tiếp đồng bộ (RESTful)**:
   - `Auction.Api/Controllers/`: Thin Dispatcher Controller, chỉ nhận request, gọi Service Interface trong Application, trả về `IActionResult`. Tuyệt đối cấm logic nghiệp vụ, cấm map anonymous object, cấm khai báo class DTO trong file controller.
   - `Auction.Application/Services/{Feature}/`: Mỗi feature (`Auction`, `Bid`...) chứa `I{Feature}Service.cs`, `{Feature}Service.cs`, và Request/Response DTOs dùng chung cho Controller.
2. **Bãi bỏ hoàn toàn gRPC & Message Broker**:
   - Không sử dụng RabbitMQ, MassTransit, Saga state machine hay Outbox tables.
   - Toàn bộ giao tiếp được thực hiện 100% qua RESTful Web API (`AuctionsController.cs`, `BidsController.cs`).
3. **Quản lý Dependency Injection**:
   - `Auction.Application/DependencyInjection.cs`: Đăng ký toàn bộ Application Services.
   - `Auction.Infrastructure/DependencyInjection.cs`: Đăng ký `AppDbContext` (hiện thực `IAuctionDbContext`).
4. **Validation**:
   - Input format validation đặt tại Request DTOs qua Data Annotations (`[Required]`, `[Range]`...).
   - Business validation đặt tại Application Services hoặc Domain Exceptions.
