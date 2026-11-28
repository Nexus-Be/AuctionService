# Cập nhật quy tắc Giá đặt đầu tiên (First Bid = Giá khởi điểm + 1 Bước giá)

## 1. Vấn đề trước đây
- Trước đây, tại `BidRules.MinNextBid(...)` ([BidRules.cs](file:///d:/AuctionService-clean/Auction.Domain/Rules/BidRules.cs#L71-L74)), khi phiên đấu giá chưa có lượt đặt giá nào (`currentPriceAmount == null`), mức giá tối thiểu hợp lệ cho lượt đặt giá đầu tiên chỉ yêu cầu **bằng giá khởi điểm** (`startPriceAmount`, hoặc `bidStepAmount` nếu giá khởi điểm = 0).
- Điều này khiến người tham gia đầu tiên có thể đặt giá bằng đúng giá khởi điểm mà không cần cộng thêm bước giá.

## 2. Nội dung đã sửa
- **File nghiệp vụ:** [BidRules.cs](file:///d:/AuctionService-clean/Auction.Domain/Rules/BidRules.cs#L67-L72)
  - Cập nhật `MinNextBid(startPriceAmount, bidStepAmount, currentPriceAmount)` thành:
    ```csharp
    (currentPriceAmount ?? startPriceAmount) + bidStepAmount
    ```
  - **Kết quả:**
    - **Lượt đặt giá đầu tiên** (`currentPriceAmount == null`): bắt buộc phải đạt tối thiểu **`startPriceAmount + bidStepAmount`** (Giá khởi điểm + 1 bước giá).
    - **Các lượt đặt giá tiếp theo** (`currentPriceAmount != null`): bắt buộc phải đạt tối thiểu **`currentPriceAmount + bidStepAmount`** (Giá hiện tại + 1 bước giá).
- **File Unit Test đã cập nhật theo quy tắc mới:**
  - [AuctionRulesTests.cs](file:///d:/AuctionService-clean/Auction.Tests/AuctionRulesTests.cs)
  - [DepositRulesTests.cs](file:///d:/AuctionService-clean/Auction.Tests/DepositRulesTests.cs)
