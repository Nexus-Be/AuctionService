# AUCTION SERVICE — ARCHITECTURE & FOLDER STRUCTURE GUIDE
## (CẨM NANG CẤU TRÚC THƯ MỤC & PHÁT TRIỂN DÀNH CHO DEVELOPER VÀ AI AGENT)

> **Thông tin dịch vụ**:
> - **Service Port**: `8005` (HTTP/1.1 REST & HTTP/2 gRPC)
> - **Database**: PostgreSQL (`auction_db`), Port máy host: `5437`
> - **Nhiệm vụ chính**: Phiên đấu giá (Auction), Đặt giá (Bid), Đồng hồ đếm ngược, Xử lý Anti-sniping, Chốt phiên và thanh toán đấu giá.

---

## 1. SƠ ĐỒ PHÂN TẦNG VÀ CẤU TRÚC THƯ MỤC CHUẨN

```
src/Services/Auction/
│
├── Auction.Domain/                                    # 1. TẦNG DOMAIN (POCO 100%, ZERO DEPENDENCY)
│   ├── Base/                                          # BaseEntity, IAuditable, ISoftDelete
│   ├── Entities/                                      # Auction, Bid, BidHistory, AuctionResult, AuctionSettlement...
│   ├── Enums/                                         # AuctionStatus, BidStatus, SettlementStatus...
│   └── Exceptions/                                    # DomainException, BusinessRuleValidationException
│
├── Auction.Application/                               # 2. TẦNG APPLICATION (USE CASES & BUSINESS LOGIC)
│   ├── Interfaces/                                    # Interfaces của CSDL và các dịch vụ hạ tầng ngoài
│   │   └── IAuctionDbContext.cs                       # Abstraction của DbContext
│   │
│   ├── Common/                                        # Tiện ích và models dùng chung trong Application
│   │
│   ├── Services/                                      # CÁC DỊCH VỤ XỬ LÝ USE CASE (THEO TÍNH NĂNG)
│   │   ├── Auction/                                   # Module Quản lý Phiên đấu giá
│   │   │   ├── IAuctionService.cs                     # Interface ca sử dụng phiên đấu giá
│   │   │   ├── AuctionService.cs                      # Xử lý nghiệp vụ phiên đấu giá
│   │   │   └── AuctionDtos.cs                         # AuctionDto, CreateAuctionDto...
│   │   └── Bid/                                       # Module Đặt giá & Lịch sử trả giá
│   │       ├── IBidService.cs                         # Interface ca sử dụng đặt giá
│   │       ├── BidService.cs                          # Xử lý nghiệp vụ đặt giá
│   │       └── BidDtos.cs                             # BidDto, PlaceBidDto...
│   │
│   └── DependencyInjection.cs                         # Đăng ký AddApplicationServices()
│
├── Auction.Infrastructure/                            # 3. TẦNG INFRASTRUCTURE (CÔNG NGHỆ & HẠ TẦNG CỤ THỂ)
│   ├── Persistence/                                   # Quản lý CSDL PostgreSQL & EF Core 8
│   │   ├── AppDbContext.cs                            # Triển khai IAuctionDbContext
│   │   ├── Configurations/                            # 13 Fluent API Configurations (*Configuration.cs)
│   │   ├── Migrations/                                # Code-first Database Migrations
│   │   └── Seeds/                                     # Dữ liệu hạt giống cấu hình phiên đấu giá
│   │
│   └── DependencyInjection.cs                         # Đăng ký AddInfrastructureServices(configuration)
│
└── Auction.Api/                                       # 4. TẦNG API / HOST (ENTRY POINT & MIDDLEWARE)
    ├── Middleware/                                    # MIDDLEWARE TOÀN CỤC CỦA SERVICE
    │   └── GlobalExceptionHandlerMiddleware.cs        # Bắt mọi lỗi ngoại lệ, format RFC 9110 ProblemDetails
    ├── Services/                                      # gRPC Server Service (AuctionGrpcService.cs)
    ├── Contracts/                                     # shop.proto (Hợp đồng gRPC)
    └── Program.cs                                     # Khởi động Kestrel HTTP/1 & HTTP/2, gọi DI App & Infra
```

---

## 2. NGUYÊN TẮC PHÂN CHIA TRÁCH NHIỆM

1. **`Auction.Domain`**: Chứa các Entity phiên đấu giá, lượt bid, lịch sử đặt giá, kết quả và thanh toán. Tuyệt đối không import thư viện ngoài hay EF Core vào đây.
2. **`Auction.Application`**: Chứa toàn bộ ca sử dụng (Use Cases). Mỗi tính năng nằm trong thư mục riêng `Services/{Feature}/` gồm DTOs, Interface và Implementation. Gọi CSDL qua `IAuctionDbContext`.
3. **`Auction.Infrastructure`**: Triển khai `AppDbContext` kế thừa `IAuctionDbContext`. Không dùng Generic Repository bọc ngoài EF Core (ADR 05).
4. **`Auction.Api`**: Cổng tiếp nhận cực kỳ mỏng. Trang bị `GlobalExceptionHandlerMiddleware` để chuẩn hóa lỗi trả về cho client.

---

## 3. QUY TRÌNH THÊM TÍNH NĂNG MỚI (DÀNH CHO DEV & AI AGENT)

1. **Bước 1**: Tạo Entity & Enum trong `Auction.Domain/Entities/`.
2. **Bước 2**: Tạo Configuration trong `Auction.Infrastructure/Persistence/Configurations/` và khai báo `DbSet<T>` trong `IAuctionDbContext` & `AppDbContext`.
3. **Bước 3**: Tạo thư mục tính năng trong `Auction.Application/Services/{Feature}/` gồm `I{Feature}Service.cs`, `{Feature}Service.cs`, và `{Feature}Dtos.cs`.
4. **Bước 4**: Đăng ký Service vào `Auction.Application/DependencyInjection.cs`.
5. **Bước 5**: Gọi Service trong `Auction.Api/Services/AuctionGrpcService.cs`.

---

## 4. QUY CHUẨN THÔNG BÁO LỖI (MANDATORY ERROR RULES)

1. **100% English**: Tuyệt đối không dùng tiếng Việt.
2. **Sentence case**: Viết hoa duy nhất chữ cái đầu (`"Auction has already ended or is not active."`).
3. **Error Code SCREAMING_SNAKE_CASE**: Ví dụ `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`.

---

## 5. CÁCH CHẠY VÀ KIỂM THỬ ĐỘC LẬP SERVICE

```bash
# 1. Bật database của Auction Service (Port 5437):
cd src/Services/Auction
docker compose up -d auction-db

# 2. Tạo & Cập nhật migration:
./.tools/dotnet-ef migrations add InitialAuctionSchema --project Auction.Infrastructure --startup-project Auction.Api
./.tools/dotnet-ef database update --project Auction.Infrastructure --startup-project Auction.Api

# 3. Chạy service:
dotnet run --project Auction.Api
```
