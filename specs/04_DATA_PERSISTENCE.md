# 04 — MÔ HÌNH THỰC THỂ & CƠ SỞ DỮ LIỆU (AUCTION SERVICE DATA PERSISTENCE)
> **Trạng thái triển khai (2026-10-24):** ⚠️ Một phần — **13 entity + 13 Fluent configuration + 1 migration** đã có (migration tạo **16 bảng**, gồm 13 bảng nghiệp vụ + 3 bảng outbox của MassTransit); **0 lời gọi `SaveChangesAsync`** trong toàn repo, 3 entity `BidInvalidation`/`AuditLog`/`IdempotencyKey` **không được dùng ở đâu**. Tên bảng thực tế là **PascalCase**, không phải snake_case như đặc tả.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
**Đối tượng ánh xạ:** `Auction.Infrastructure/Persistence/AppDbContext.cs:14-26` khai báo 13 `DbSet<T>`; `OnModelCreating` (L28-38) nạp toàn bộ configuration qua `ApplyConfigurationsFromAssembly` (L29), seed dữ liệu mẫu (L30-33) và đăng ký 3 entity outbox (L35-37).
---
## 1. MÔ HÌNH THỰC THỂ CHÍNH (DOMAIN ENTITIES)
### 1.1. Bảng đối chiếu: đặc tả gốc → thực thể thật
| # | Thực thể (đặc tả) | Thực thể thật trong code | Trạng thái | Bằng chứng |
|---|---|---|---|---|
| 1 | `Auction` | `Auction : AuditableEntity<Guid>` — **không** có `Title`, `CurrentPrice`, `StepPrice`, `CurrentWinnerId`, `StartTime`, `EndTime`, `Status`, `RowVersion` | ⚠️ MỘT PHẦN | `Auction.Domain/Entities/Auction.cs`; các trường bị tách sang `AuctionState` |
| 2 | `Bid` | `Bid : BaseEntity<Guid>` — có `BidderId` (không phải `UserId`), `BidSeq`, `PlacedAt`; **không** có `CreatedAt` riêng | ⚠️ MỘT PHẦN | `Auction.Domain/Entities/Bid.cs` |
| 3 | — | `AuctionState` (giá hiện tại, người thắng, trạng thái, `RowVersion`) | ✅ CÓ (không có trong đặc tả) | `Auction.Domain/Entities/AuctionState.cs:17` |
| 4 | — | `ProductOpenAuction` (ràng buộc 1 phiên mở / 1 sản phẩm) | ✅ CÓ (không có trong đặc tả) | `Auction.Domain/Entities/ProductOpenAuction.cs` |
| 5 | — | `BidInvalidation`, `BidHistory`, `AuctionExtension`, `AuctionResult`, `AuctionSettlement`, `AuctionStatusHistory`, `AuctionConfig`, `IdempotencyKey`, `AuditLog` | ✅ CÓ (không có trong đặc tả) | `Auction.Domain/Entities/*.cs` |
### 1.2. Trường thật của 2 thực thể trọng tâm (đặc tả gốc giữ nguyên bên dưới để đối chiếu)
Đặc tả gốc (giữ nguyên — **đặc tả mục tiêu, chưa triển khai** đúng như viết):
1. **`Auction`**: `Id`, `ProductId`, `SellerId`, `Title`, `StartingPrice`, `CurrentPrice`, `StepPrice`, `CurrentWinnerId`, `StartTime`, `EndTime`, `Status` (`Scheduled`, `Active`, `Ended`, `Cancelled`), `RowVersion` (`byte[]`), nav `ICollection<Bid> Bids`.
2. **`Bid`**: `Id`, `AuctionId`, `UserId`, `Amount`, `CreatedAt`.
Thực tế trong code:
| Thực thể | Trường thật | Ghi chú |
|---|---|---|
| `Auction` | `ProductId`, `SkuId?`, `SellerId`, `SnapshotProductTitle?`, `RecreatedFromAuctionId?`, `StartPriceAmount`, `BidStepAmount`, `ScheduledStartAt`, `OriginalEndAt`, `MinReputationToBid`, `MinReputationToSell`, `AntiSnipingExtensionMinutes`, `MaxExtensionCount` + `Id`/`CreatedAt`/`UpdatedAt`/`IsDeleted`/`DeletedAt`/`CreatedBy`/`UpdatedBy` | Tiêu đề nằm ở `SnapshotProductTitle` (**snapshot**, không đồng bộ lại khi sản phẩm đổi tên); giá/người thắng/trạng thái nằm ở `AuctionState` |
| `AuctionState` | `AuctionId`, `Status`, `CurrentPriceAmount?`, `CurrentWinnerId?`, `BidCount`, `ExtensionCount`, `ScheduledEndAt`, `StartedAt?`, `EndedAt?`, `RowVersion` (`long`, concurrency token) | Quan hệ 1-1 với `Auction` (`AuctionStateConfiguration.cs:16-18`) |
| `Bid` | `AuctionId`, `BidSeq` (`int`), `BidderId`, `Amount` (`decimal(18,2)`), `PlacedAt` (`DateTimeOffset`) | **Không** có `UserId`; **không** có `CreatedAt` (chỉ kế thừa `BaseEntity`) |
---
## 2. QUY CHUẨN CƠ SỞ DỮ LIỆU & EF CORE
### 2.1. Thông số thật
| Hạng mục | Giá trị | Bằng chứng |
|---|---|---|
| Engine | PostgreSQL | `Auction.Infrastructure/DependencyInjection.cs:18` (`UseNpgsql`) |
| Database | `auction_db` @ port `5437` | `Auction.Api/appsettings.json:10`; `docker-compose.yml:19` (`${AUCTION_DB_PORT:-5437}:5432`) |
| Chuỗi kết nối | `Host=localhost;Port=5437;Database=auction_db;Username=postgres;Password=MatkhaucuaTui123@` | `Auction.Api/appsettings.json:10`, fallback tại `DependencyInjection.cs:15` |
| `DbContext` | `AppDbContext : DbContext, IAuctionDbContext` | `Auction.Infrastructure/Persistence/AppDbContext.cs` |
| Đăng ký DI | `AddDbContext<AppDbContext>` + scoped `IAuctionDbContext` trỏ về cùng instance | `DependencyInjection.cs:17-20` |
| Migration | 1 migration `20261024103234_InitialAuctionSchema` | `Auction.Infrastructure/Migrations/` |
| Tự migrate khi khởi động | ✅ CÓ, có `try/catch` → chỉ log warning | `Auction.Api/Program.cs:42-56` |
### 2.2. Tên bảng: đặc tả snake_case ❌ — thực tế PascalCase ✅
Đặc tả ghi `auctions`, `bids` là **sai**: không có `ToTable(...)` hay naming-convention nào đổi tên, nên EF dùng tên `DbSet` → **PascalCase**. Migration tạo **16 bảng**:
`Auctions`, `AuctionStates`, `ProductOpenAuctions`, `Bids`, `BidInvalidations`, `BidHistories`, `AuctionExtensions`, `AuctionResults`, `AuctionSettlements`, `AuctionStatusHistories`, `AuctionConfigs`, `IdempotencyKeys`, `AuditLogs`, `InboxState`, `OutboxMessage`, `OutboxState`.
> Hệ quả: mọi câu SQL/DBML viết `auctions`/`bids` sẽ không chạy trên DB thật (Postgres quoted-identifier). Xem `Auction.Infrastructure/Migrations/20261024103234_InitialAuctionSchema.cs`.
### 2.3. Enum: giá trị thật
| Enum | Giá trị thật | Bằng chứng |
|---|---|---|
| `AuctionStatus` | `Draft=0`, `Scheduled=1`, `Running=2`, `Ended=3`, `Settled=4`, `Failed=5`, `Cancelled=6` → **`Active` trong đặc tả KHÔNG tồn tại** (tương ứng là `Running`) | `Auction.Domain/Enums/AuctionStatus.cs` |
| Lưu xuống DB | `Status` lưu dạng **chuỗi** | `AuctionStateConfiguration.cs:12` (`HasConversion<string>()`) |
| `AuditAction` | 7 giá trị `AuctionCreate` … `ConfigUpdate` | `Auction.Domain/Enums/AuditAction.cs` |
| `BidInvalidationReason` | chỉ `BidderBanned` | `Auction.Domain/Enums/BidInvalidationReason.cs` |
| `IdempotencyScope` | `Bid=0`, `Settlement=1` | `Auction.Domain/Enums/IdempotencyScope.cs` |
### 2.4. Index thật: 1/4 so với đặc tả
| Index (đặc tả mục tiêu) | Trạng thái | Bằng chứng |
|---|---|---|
| Unique `ProductOpenAuctions.ProductId` | ✅ CÓ | `ProductOpenAuctionConfiguration.cs:12` → `IX_ProductOpenAuctions_ProductId` |
| Index theo `Auction.Status` + thời gian kết thúc | ❌ CHƯA CÓ | không có `HasIndex` trong `AuctionStateConfiguration.cs` |
| Index theo `Bid.AuctionId` + `Amount` | ⚠️ MỘT PHẦN — chỉ có FK index `IX_Bids_AuctionId` do EF tự sinh, **không** có index composite theo `Amount` | migration |
| Index theo `Bid.BidderId` | ❌ CHƯA CÓ | `BidConfiguration.cs` không khai báo `HasIndex` |
> Tổng cộng migration có **20 `CreateIndex`**, nhưng chỉ **1** xuất phát từ khai báo nghiệp vụ; 19 còn lại do EF sinh cho FK và cho bảng outbox.
### 2.5. Optimistic Concurrency
- `RowVersion` tồn tại trên **`AuctionState`** (`long`, `AuctionState.cs:17`), cấu hình `IsConcurrencyToken()` tại `AuctionStateConfiguration.cs:14` — **không** phải `byte[]` trên `Auction`.
- ⚠️ Hiện **vô hiệu trên thực tế**: `SaveChangesAsync` có 0 lời gọi trong repo, nên token chưa bao giờ được kiểm tra. Ngoài ra `long` do ứng dụng tự tăng, không phải `xmin` của Postgres.
### 2.6. Dữ liệu seed
`Auction.Infrastructure/Persistence/Seeds/AuctionSeedData.cs` — `SeedAll(ModelBuilder)`, mốc thời gian `2026-01-01T00:00:00Z`, 5 bản ghi `HasData`: 1 `AuctionConfig` (`DEFAULT_AUCTION_POLICY`: `antiSnipingExtensionMinutes=5`, `maxExtensionCount=10`, `minReputationToBid=40`, `minReputationToSell=50`), 1 `Auction` (giá khởi điểm `500000`, bước giá `50000`), 1 `AuctionState` (`Status=Running`, `CurrentPriceAmount=600000`, `BidCount=1`), 1 `ProductOpenAuction`, 1 `Bid` (`BidSeq=1`, `Amount=600000`).
⚠️ Chuỗi seed đang là **tiếng Việt** (ví dụ `SnapshotProductTitle = "Mũ Bảo Hiểm Xe Đạp Nexus Aero - Đỏ / Size M"`), không tuân Nguyên tắc 5 (thông báo lỗi phải 100% English) — tuy đây là dữ liệu hiển thị chứ không phải thông báo lỗi, cần chốt lại quy ước ngôn ngữ cho seed.
### 2.7. Lệch với DBML ở workspace root
`dbml/service/auction-db.dbml` (14 bảng) ≠ code (16 bảng):
- DBML có `SagaStates` (dòng 299) nhưng **code không có** state machine nào ⇒ bảng này là ảo.
- Code có 3 bảng outbox `InboxState`, `OutboxMessage`, `OutboxState` nhưng **DBML không có**.
### 2.8. Tầng ghi dữ liệu: chưa tồn tại
- **No Repository Pattern**: ✅ đúng như đặc tả — grep `*Repository|UnitOfWork` = **0 kết quả**; Application dùng trực tiếp `IAuctionDbContext`.
- ⚠️ Nhưng `SaveChangesAsync` chỉ **được khai báo** tại `Auction.Application/Interfaces/IAuctionDbContext.cs:24`, **0 lời gọi** toàn repo. Toàn bộ hệ thống hiện là **read-only**; 13 bảng chỉ được tạo bởi migration + seed.
- 3 entity `BidInvalidation`, `AuditLog`, `IdempotencyKey` chỉ xuất hiện ở `DbSet`/configuration, **không** có service/DTO nào chạm tới.
- Lớp `AuctionDbContext : AppDbContext` (`AppDbContext.cs:41`) là **dead code** — 0 tham chiếu.