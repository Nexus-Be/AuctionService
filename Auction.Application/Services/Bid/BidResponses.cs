namespace Auction.Application.Services.Bid;

using System;

public sealed record BidResponse(
    Guid Id,
    Guid AuctionId,
    Guid BidderId,
    int BidSeq,
    decimal Amount,
    DateTimeOffset PlacedAt);
