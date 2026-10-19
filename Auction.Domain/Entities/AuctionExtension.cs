using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class AuctionExtension : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public Guid? TriggerBidId { get; set; }
    public required int SequenceNo { get; set; }
    public required DateTimeOffset PreviousEndAt { get; set; }
    public required DateTimeOffset NewEndAt { get; set; }
    public required DateTimeOffset ExtendedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
    public Bid? TriggerBid { get; set; }
}
