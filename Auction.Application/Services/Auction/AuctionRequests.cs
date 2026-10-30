namespace Auction.Application.Services.Auction;

using System;

public sealed record CreateAuctionRequest(
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? Title,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset OriginalEndAt);

public sealed record CloseAuctionRequest(
    Guid AuctionId);
