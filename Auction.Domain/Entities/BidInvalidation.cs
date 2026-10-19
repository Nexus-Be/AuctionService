using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class BidInvalidation : BaseEntity<Guid>
{
    public required Guid BidId { get; set; }
    public required BidInvalidationReason Reason { get; set; } = BidInvalidationReason.BidderBanned;
    public required DateTimeOffset InvalidatedAt { get; set; }

    // Navigation
    public Bid? Bid { get; set; }
}
