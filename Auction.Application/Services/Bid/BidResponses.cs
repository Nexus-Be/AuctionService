namespace Auction.Application.Services.Bid;

using System;

/// <summary>
/// Kết quả đặt giá thành công. Amount là giá người dùng vừa đặt; PreviousPriceAmount là giá hiện tại ngay trước lượt đặt này
/// (null nếu đây là bid đầu tiên); CurrentPriceAmount là giá hiện tại sau lượt đặt (bằng Amount vì bid này đang dẫn đầu).
/// RowVersion là phiên bản mới của phiên, dùng làm If-Match cho lần đặt giá kế tiếp.
/// </summary>
public sealed record PlaceBidResponse(
    Guid BidId,
    Guid AuctionId,
    int BidSeq,
    decimal Amount,
    DateTimeOffset PlacedAt,
    decimal CurrentPriceAmount,
    int BidCount,
    DateTimeOffset ScheduledEndAt,
    int ExtensionCount,
    decimal? PreviousPriceAmount,
    long RowVersion);