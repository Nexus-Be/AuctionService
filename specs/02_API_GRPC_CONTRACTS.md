# 02 — HỢP ĐỒNG GIAO TIẾP GRPC (AUCTION SERVICE GRPC CONTRACTS)
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có — **0/5 rpc**; **0 file `.proto`** trong repo; **0 package `Grpc.AspNetCore`** trong cả 4 `.csproj`; 0 class kế thừa gRPC base. Bề mặt thật chỉ có `GET /health` (`Auction.Api/Program.cs:58`).
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
Dịch vụ gRPC của AuctionService **được đặc tả** trong hợp đồng `Contracts/shop.proto` dưới service `AuctionApi`. ⚠️ **Đường dẫn này không tồn tại trong repo này**: `find . -name "*.proto"` = 0 kết quả; `Auction.Api/` chỉ có `Middleware/`, `Properties/`, `Program.cs`, `appsettings*.json`. Toàn bộ mục 1 dưới đây là **đặc tả mục tiêu, chưa triển khai**.
---
## 1. DANH SÁCH CÁC RPC METHODS
### 1.1. Bảng trạng thái thực tế (đối chiếu code)
| # | RPC (đặc tả) | Request → Response (đặc tả) | Trạng thái | Bằng chứng |
|---|---|---|---|---|
| 1 | `ListAuctions` | `AuctionQuery` → `AuctionPage` | ❌ CHƯA CÓ | chưa có lớp gRPC; chỉ có `AuctionService.ListActiveAuctionsAsync(int, int, CancellationToken)` trả `IReadOnlyList<AuctionDto>` (`Auction.Application/Services/Auction/AuctionService.cs:39`) — và hàm này **không lọc trạng thái** |
| 2 | `GetAuction` | `IdRequest` → `Auction` | ❌ CHƯA CÓ | chưa có lớp gRPC; chỉ có `AuctionService.GetByIdAsync(Guid, CancellationToken)` trả `AuctionDto?` (`AuctionService.cs:20`) — DTO **không chứa** giá hiện tại/người đang thắng/trạng thái |
| 3 | `CreateAuction` | `CreateAuctionRequest` → `Auction` | ❌ CHƯA CÓ | không có method nào; chỉ có record `CreateAuctionDto` (`AuctionDtos.cs:15`) **0 nơi sử dụng** |
| 4 | `PlaceBid` | `PlaceBidRequest` → `Bid` | ❌ CHƯA CÓ | không có method nào; chỉ có record `PlaceBidDto` (`BidDtos.cs:13`) **0 nơi sử dụng**; 0 `throw new` toàn repo |
| 5 | `ListBids` | `IdRequest` → `BidPage` | ❌ CHƯA CÓ | chưa có lớp gRPC; chỉ có `BidService.ListBidsByAuctionIdAsync(Guid, CancellationToken)` trả `IReadOnlyList<BidDto>` (`Auction.Application/Services/Bid/BidService.cs:20`) |
### 1.2. Bề mặt thật hiện có (thay thế tạm cho gRPC)
| Thành phần | Trạng thái | Bằng chứng |
|---|---|---|
| `AuctionService.GetByIdAsync(Guid, CancellationToken)` | ✅ CÓ (1 truy vấn đọc) | `Auction.Application/Services/Auction/AuctionService.cs:20` |
| `AuctionService.ListActiveAuctionsAsync(int page, int pageSize, CancellationToken)` | ⚠️ MỘT PHẦN | `AuctionService.cs:39` — phân trang nhưng **không `Where` theo trạng thái** |
| `BidService.ListBidsByAuctionIdAsync(Guid, CancellationToken)` | ✅ CÓ (1 truy vấn đọc) | `Auction.Application/Services/Bid/BidService.cs:20` |
| Ghi dữ liệu (`Add`/`Update`/`Remove` + `SaveChangesAsync`) | ❌ CHƯA CÓ | `SaveChangesAsync` chỉ **khai báo** ở `Auction.Application/Interfaces/IAuctionDbContext.cs:24`, **0 lời gọi** trong toàn repo |
| gRPC transport (`AddGrpc`, `MapGrpcService`, `Grpc.AspNetCore`) | ❌ CHƯA CÓ | grep `Grpc` trên `*.csproj` và `*.cs` = 0 kết quả |
| Minimal endpoint `/health` | ✅ CÓ | `Auction.Api/Program.cs:58` |
### 1.3. Hợp đồng đặc tả gốc (giữ nguyên — đặc tả mục tiêu, chưa triển khai)
```protobuf
service AuctionApi {
  // Xem danh sách và chi tiết phòng đấu giá
  rpc ListAuctions(AuctionQuery) returns (AuctionPage);
  rpc GetAuction(IdRequest) returns (Auction);
  // Tạo phiên & Đặt giá bid
  rpc CreateAuction(CreateAuctionRequest) returns (Auction);
  rpc PlaceBid(PlaceBidRequest) returns (Bid);
  rpc ListBids(IdRequest) returns (BidPage);
}
```
> Ghi chú đồng bộ hợp đồng: khi triển khai, `.proto` phải là bản duy nhất được ApiGateway sinh client từ đó. Hiện **chưa có** bản `.proto` nào trong repo này để đối chiếu.
### 1.4. Rào chắn kỹ thuật phải xử lý trước khi thêm gRPC
`Auction.Api/Program.cs:10-16` cấu hình `HttpProtocols.Http1AndHttp2` cho endpoint **không TLS** (`ASPNETCORE_URLS=http://+:8005` — `Dockerfile:4`). Trên cleartext **không có ALPN**, Kestrel sẽ đàm phán HTTP/1.1 ⇒ thêm `MapGrpcService` cũng sẽ fail với lỗi kiểu `HTTP/2 over cleartext is not supported`. Muốn chạy h2c phải đặt `HttpProtocols.Http2` riêng cho endpoint gRPC, hoặc bật TLS.
---
## 2. QUY CHUẨN XỬ LÝ LỖI & MÃ LỖI (ERROR STATUS CODES)
⚠️ **Phần này là đặc tả mục tiêu**: class `AuctionGrpcService` **không tồn tại**, nên repo **0 lần** ném `RpcException` (grep `RpcException` = 0 kết quả) và **0 lần** ném bất kỳ exception nào (`grep "throw new"` = 0 kết quả toàn repo).
Đặc tả mong muốn khi xử lý trong `AuctionGrpcService`:
- **`StatusCode.InvalidArgument`** (400 Bad Request): Giá khởi điểm hoặc bước giá $\le 0$, số tiền bid không đủ bước giá, thời gian kết thúc trước thời gian bắt đầu.
- **`StatusCode.NotFound`** (404 Not Found): Không tìm thấy phiên đấu giá với ID yêu cầu.
- **`StatusCode.PermissionDenied`** (403 Forbidden): Người bán tự bid trên phiên của chính mình.
- **`StatusCode.FailedPrecondition`** (412 Precondition Failed): Đặt giá trên phiên đấu giá đã kết thúc hoặc chưa bắt đầu.
### 2.1. Mã lỗi thật đang chạy (middleware HTTP, không phải gRPC)
`Auction.Api/Middleware/GlobalExceptionHandlerMiddleware.cs:36-63` — các mã này chỉ chạy khi có endpoint ném exception; hiện repo không có đường nào ném:
| Exception nguồn | HTTP | `errorCode` |
|---|---|---|
| `EntityNotFoundException` | 404 | `NOT_FOUND` |
| `BusinessRuleValidationException` | 400 | `BUSINESS_RULE_VIOLATION` |
| `DomainException` | 400 | `DOMAIN_ERROR` |
| `ArgumentException` | 400 | `VAL_INVALID_ARGUMENT` |
| khác | 500 | `INTERNAL_SERVER_ERROR` |
⚠️ Đường ném `ArgumentException` khả dĩ duy nhất hiện tại nằm ở `AuctionService.cs:44`: `Skip((page - 1) * pageSize)` không validate `page`/`pageSize`, nên `page = 0` → `Skip(-pageSize)` → `ArgumentException`.