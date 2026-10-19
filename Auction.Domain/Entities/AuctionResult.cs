using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class AuctionResult : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public Guid? WinningBidId { get; set; }
    public Guid? WinnerId { get; set; }
    public decimal? FinalPriceAmount { get; set; }
    public required DateTimeOffset DeterminedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
    public Bid? WinningBid { get; set; }
}
