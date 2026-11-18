using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class AuctionWatch : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required Guid UserId { get; set; }
    public bool NotifyOnNewBid { get; set; } = true;
    public DateTimeOffset? LastNotifiedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
