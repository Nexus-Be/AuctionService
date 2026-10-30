# 02 — HỢP ĐỒNG GIAO TIẾP RESTFUL (AUCTION SERVICE REST CONTRACTS)

> **Trạng thái triển khai (2026-10-30):** ✅ ĐÃ CHUẨN HÓA 100% RESTFUL — Toàn bộ giao tiếp đấu giá và đặt giá qua RESTful Web API (`AuctionsController`, `BidsController`). Bãi bỏ hoàn toàn gRPC và RabbitMQ.
> **Nguồn sự thật:** code (`Auction.Api/Controllers/AuctionsController.cs`, `Auction.Api/Controllers/BidsController.cs`).
> **Cập nhật lần cuối:** 2026-10-30.

---

## 1. DANH MỤC ENDPOINTS RESTFUL CHÍNH THỨC

### 1.1. Phiên đấu giá (`AuctionsController` — `/api/v1/auctions`)
* `GET /api/v1/auctions`: Danh sách phiên đấu giá đang hoạt động có phân trang (`page`, `pageSize`).
* `GET /api/v1/auctions/{id}`: Tra cứu chi tiết phiên đấu giá theo mã định danh `id` (GUID).

### 1.2. Đặt giá (`BidsController` — `/api/v1/auctions/{auctionId}/bids`)
* `GET /api/v1/auctions/{auctionId}/bids`: Lịch sử các lượt đặt giá của một phiên đấu giá.

---

## 2. NGUYÊN TẮC BẤT BIẾN

* **Envelope & Errors**: Phản hồi bọc trong phong bì `ApiResponse<T>` hoặc `ApiResponse<PagedData<T>>`.
* **Casing**: DTO dùng PascalCase trong C# và camelCase trong JSON serialization.
* **1-to-1 Controller Pairing**: Mỗi controller chỉ inject duy nhất feature service tương ứng (`IAuctionService`, `IBidService`).
