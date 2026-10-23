# 04 — MÔ HÌNH THỰC THỂ & CƠ SỞ DỮ LIỆU (AUCTION SERVICE DATA PERSISTENCE)

---

## 1. MÔ HÌNH THỰC THỂ CHÍNH (DOMAIN ENTITIES)

1. **`Auction`**:
   - `Id`: `Guid`.
   - `ProductId`: `Guid` (Sản phẩm đem đấu giá).
   - `SellerId`: `Guid` (Người bán tạo phiên).
   - `Title`: `string` (Tiêu đề phiên đấu giá).
   - `StartingPrice`: `decimal` (Precision: 18, Scale: 2).
   - `CurrentPrice`: `decimal` (Giá trả cao nhất hiện tại).
   - `StepPrice`: `decimal` (Bước giá tối thiểu).
   - `CurrentWinnerId`: `Guid?` (User đang giữ mức giá cao nhất).
   - `StartTime`: `DateTimeOffset`.
   - `EndTime`: `DateTimeOffset`.
   - `Status`: `AuctionStatus` (`Scheduled`, `Active`, `Ended`, `Cancelled`).
   - `RowVersion`: `byte[]` (Dùng cho Optimistic Concurrency).
   - Navigation: `ICollection<Bid> Bids`.

2. **`Bid`**:
   - `Id`: `Guid`.
   - `AuctionId`: `Guid` (FK $\rightarrow$ `Auction.Id`).
   - `UserId`: `Guid` (Người đặt giá).
   - `Amount`: `decimal` (Số tiền bid).
   - `CreatedAt`: `DateTimeOffset`.

---

## 2. QUY CHUẨN CƠ SỞ DỮ LIỆU & EF CORE

- Engine: PostgreSQL.
- Database: `auction_db` tại port `5437`.
- Chuỗi kết nối: `Host=localhost;Port=5437;Database=auction_db;Username=postgres;Password=MatkhaucuaTui123@`.
- Tên bảng: `auctions`, `bids`.
- **No Repository Pattern**: Tầng Application tương tác trực tiếp với `AuctionDbContext`.
