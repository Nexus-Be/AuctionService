using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class Auction : AuditableEntity<Guid>
{
    public required Guid ProductId { get; set; }
    public Guid? SkuId { get; set; }
    public required Guid SellerId { get; set; }
    public string? SnapshotProductTitle { get; set; }
    public Guid? RecreatedFromAuctionId { get; set; }
    public required decimal StartPriceAmount { get; set; }
    public required decimal BidStepAmount { get; set; }
    public required DateTimeOffset ScheduledStartAt { get; set; }
    public required DateTimeOffset OriginalEndAt { get; set; }
    public int MinReputationToBid { get; set; }
    public int MinReputationToSell { get; set; }
    public int AntiSnipingExtensionMinutes { get; set; }
    public int MaxExtensionCount { get; set; }

    // Navigation
    public Auction? RecreatedFromAuction { get; set; }
    public ICollection<Auction> RecreatedAuctions { get; set; } = new List<Auction>();
    public AuctionState? AuctionState { get; set; }
    public ProductOpenAuction? ProductOpenAuction { get; set; }
    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
    public ICollection<BidHistory> BidHistories { get; set; } = new List<BidHistory>();
    public ICollection<AuctionExtension> Extensions { get; set; } = new List<AuctionExtension>();
    public AuctionResult? AuctionResult { get; set; }
    public AuctionSettlement? AuctionSettlement { get; set; }
    public ICollection<AuctionStatusHistory> StatusHistories { get; set; } = new List<AuctionStatusHistory>();
}
