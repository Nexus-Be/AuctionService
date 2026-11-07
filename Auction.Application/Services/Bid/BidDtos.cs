namespace Auction.Application.Services.Bid;

using System;
using global::Auction.Domain.Enums;

public sealed record BidDto(
    Guid BidId,
    int SequenceNo,
    Guid BidderId,
    string BidderDisplayName,
    decimal Amount,
    DateTimeOffset PlacedAt,
    bool IsOutbid,
    bool IsWinning,
    bool IsInvalidated);

public sealed record MyBidDto(
    Guid BidId,
    Guid AuctionId,
    string AuctionTitle,
    AuctionStatus? Status,
    int SequenceNo,
    decimal Amount,
    DateTimeOffset PlacedAt,
    bool IsOutbid,
    bool IsWinning,
    bool IsInvalidated);

/// <summary>
/// Nội dung sự kiện BidPlaced đẩy tới mọi người đang xem phiên sau khi một lượt đặt giá được lưu.
/// Chỉ chứa dữ liệu công khai, không có danh tính người đặt giá. Client chỉ áp dụng tin có BidSeq
/// lớn hơn BidSeq đang hiển thị, để bỏ qua tin đến muộn hoặc sai thứ tự. RowVersion là phiên bản mới của phiên:
/// client giữ nó để gửi If-Match khi đặt giá, nhờ đó ít bị 409 hơn.
/// </summary>
public sealed record BidPlacedMessage(
    Guid AuctionId,
    Guid BidId,
    int BidSeq,
    decimal Amount,
    decimal CurrentPriceAmount,
    int BidCount,
    DateTimeOffset PlacedAt,
    DateTimeOffset ScheduledEndAt,
    int ExtensionCount,
    decimal? PreviousPriceAmount,
    long RowVersion);