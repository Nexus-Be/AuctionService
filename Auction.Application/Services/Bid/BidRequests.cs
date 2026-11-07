namespace Auction.Application.Services.Bid;

/// <summary>Amount is nullable so that a missing value is reported with a clear message.</summary>
public sealed record PlaceBidRequest(decimal? Amount);