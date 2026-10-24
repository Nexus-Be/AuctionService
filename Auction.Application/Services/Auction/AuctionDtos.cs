namespace Auction.Application.Services.Auction;

using System;

public sealed record AuctionDto(
    Guid Id,
    Guid ProductId,
    Guid SellerId,
    string? Title,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset OriginalEndAt);

public sealed record CreateAuctionDto(
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? Title,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset OriginalEndAt);
