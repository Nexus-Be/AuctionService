using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class Bid : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required int BidSeq { get; set; }
    public required Guid BidderId { get; set; }
    public required decimal Amount { get; set; }
    public required DateTimeOffset PlacedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
    public BidInvalidation? BidInvalidation { get; set; }
    public ICollection<BidHistory> BidHistories { get; set; } = new List<BidHistory>();
}
