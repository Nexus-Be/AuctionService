namespace Auction.Application.Services.Auction;

using System;

public sealed record AuctionResponse(
    Guid Id,
    Guid ProductId,
    Guid SellerId,
    string? Title,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset OriginalEndAt);
