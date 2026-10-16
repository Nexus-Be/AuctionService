# 02 — HỢP ĐỒNG GIAO TIẾP GRPC (AUCTION SERVICE GRPC CONTRACTS)

Dịch vụ gRPC của AuctionService được định nghĩa trong hợp đồng duy nhất `Contracts/shop.proto` dưới service `AuctionApi`:

---

## 1. DANH SÁCH CÁC RPC METHODS

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

---

## 2. QUY CHUẨN XỬ LÝ LỖI & MÃ LỖI (ERROR STATUS CODES)

Khi xử lý trong `AuctionGrpcService`, ném ra `RpcException`:
- **`StatusCode.InvalidArgument`** (400 Bad Request): Giá khởi điểm hoặc bước giá $\le 0$, số tiền bid không đủ bước giá, thời gian kết thúc trước thời gian bắt đầu.
- **`StatusCode.NotFound`** (404 Not Found): Không tìm thấy phiên đấu giá với ID yêu cầu.
- **`StatusCode.PermissionDenied`** (403 Forbidden): Người bán tự bid trên phiên của chính mình.
- **`StatusCode.FailedPrecondition`** (412 Precondition Failed): Đặt giá trên phiên đấu giá đã kết thúc hoặc chưa bắt đầu.
