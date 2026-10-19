using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class BidHistory : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required Guid BidId { get; set; }
    public required int SequenceNo { get; set; }
    public required Guid BidderId { get; set; }
    public string? DisplayName { get; set; }
    public required decimal Amount { get; set; }
    public required DateTimeOffset PlacedAt { get; set; }
    public bool IsOutbid { get; set; }
    public bool IsWinning { get; set; }
    public bool IsInvalidated { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
    public Bid? Bid { get; set; }
}
