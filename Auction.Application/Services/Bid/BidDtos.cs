namespace Auction.Application.Services.Bid;

using System;

public sealed record BidDto(
    Guid Id,
    Guid AuctionId,
    Guid BidderId,
    int BidSeq,
    decimal Amount,
    DateTimeOffset PlacedAt);

public sealed record PlaceBidDto(
    Guid AuctionId,
    Guid BidderId,
    decimal Amount);
