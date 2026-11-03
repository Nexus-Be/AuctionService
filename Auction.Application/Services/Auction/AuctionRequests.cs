namespace Auction.Application.Services.Auction;

using System;

// Prices and schedule are always chosen by the seller. Fields are nullable so that a missing value
// is reported with a clear message instead of being replaced by a default.

public sealed record CreateAuctionRequest(
    Guid? ProductId,
    Guid? SkuId,
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

public sealed record UpdateAuctionRequest(
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

public sealed record CancelAuctionRequest(string? Reason);

public sealed record RecreateAuctionRequest(
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);