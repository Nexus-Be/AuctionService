using Auction.Domain.Base;
using Auction.Domain.Enums;

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

    // Background sync states (Overview 4.1)
    public BackgroundSyncState ViolationState { get; set; } = BackgroundSyncState.None;

    // The violation waiting to be sent to the User service, captured when it is decided. The settlement is reset when a
    // runner-up accepts an offer, so the retry must not read the buyer, order or time from it.
    public Guid? PendingViolationBuyerId { get; set; }
    public Guid? PendingViolationOrderId { get; set; }
    public DateTimeOffset? PendingViolationOccurredAt { get; set; }
    public bool PendingViolationIsSecondChance { get; set; }
    public BackgroundSyncState StreakResetState { get; set; } = BackgroundSyncState.None;
    public BackgroundSyncState StockReleaseState { get; set; } = BackgroundSyncState.None;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? LastError { get; set; }
    public bool NeedsAttention { get; set; }

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
    public ICollection<AuctionWinnerOffer> WinnerOffers { get; set; } = new List<AuctionWinnerOffer>();
    public ICollection<AuctionStatusHistory> StatusHistories { get; set; } = new List<AuctionStatusHistory>();
}
