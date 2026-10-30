namespace Auction.Application.Services.Bid;

using System;

public sealed record PlaceBidRequest(
    Guid AuctionId,
    Guid BidderId,
    decimal Amount);
