using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class AuctionSettlement : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset? PaymentDeadlineAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
