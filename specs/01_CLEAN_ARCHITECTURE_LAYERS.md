# 01 — PHÂN TẦNG CLEAN ARCHITECTURE & QUY TẮC PHỤ THUỘC (AUCTION SERVICE)

Solution `Auction.sln` bao gồm 4 project tương ứng với 4 tầng kiến trúc sạch (Clean Architecture):

```
Auction.sln
├── Auction.Domain/           # Entities (Auction, Bid), Value Objects, Bidding Invariants
├── Auction.Application/      # Use cases CQRS (CreateAuctionCommand, PlaceBidCommand, Queries)
├── Auction.Infrastructure/   # EF Core DbContext, PostgreSQL mappings, Background Auction Timer
└── Auction.Api/              # Điểm khởi chạy gRPC Server (Program.cs, DI setup)
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
- **`Auction.Domain`**: Chứa Entity `Auction`, `Bid`, Enum trạng thái `AuctionStatus` (`Scheduled`, `Active`, `Ended`, `Cancelled`). Các logic kiểm tra hợp lệ giá bid (ví dụ: `auction.PlaceBid(userId, amount)`) bắt buộc đóng gói trong Domain Entity.
- **`Auction.Application`**: Chứa các Commands và Queries qua `MediatR`. Khai báo interface `IAuctionDbContext`.
- **`Auction.Infrastructure`**: Chứa `AuctionDbContext`, cấu hình bảng PostgreSQL, và Background Hosted Service định kỳ quét các phiên hết hạn để chuyển trạng thái sang `Ended`.
- **`Auction.Api`**: Điểm khởi động gRPC Server, đăng ký `app.MapGrpcService<AuctionGrpcService>()`.

---

## 2. QUY CHUẨN CODE TỪNG TẦNG

1. **`Auction.Domain`**:
   - `Auction`: Chứa `StartingPrice`, `CurrentPrice`, `StepPrice`, `CurrentWinnerId`, `StartTime`, `EndTime`.
   - Hàm `PlaceBid`:
     - Kiểm tra trạng thái phải là `Active`.
     - Kiểm tra thời gian `UtcNow` phải nằm giữa `StartTime` và `EndTime`.
     - Kiểm tra `Amount >= CurrentPrice + StepPrice`.
     - Nếu thỏa mãn: Gán `CurrentPrice = Amount`, `CurrentWinnerId = userId`.

2. **`Auction.Infrastructure`**:
   - Đánh Index trên `Auction.Status`, `Auction.EndTime`, `Bid.AuctionId`, `Bid.CreatedAt`.
