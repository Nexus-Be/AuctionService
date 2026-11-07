namespace Auction.Application.Services.AuctionConfig;

// Fields are nullable so that a field that is not sent is left unchanged.
public sealed record UpdateAuctionConfigRequest(
    int? AntiSnipingExtensionMinutes,
    int? MaxExtensionCount);
