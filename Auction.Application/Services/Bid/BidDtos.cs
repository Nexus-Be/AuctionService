namespace Auction.Application.Services.Bid;

using System;

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