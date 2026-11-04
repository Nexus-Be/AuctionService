namespace Auction.Application.Services.Bid;

using System;

public sealed record PlaceBidResponse(
    Guid BidId,
    Guid AuctionId,
    int BidSeq,
    decimal Amount,
    DateTimeOffset PlacedAt,
    decimal CurrentPriceAmount,
    int BidCount,
    DateTimeOffset ScheduledEndAt,
    int ExtensionCount);