using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class AuctionState : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required AuctionStatus Status { get; set; } = AuctionStatus.Draft;
    public decimal? CurrentPriceAmount { get; set; }
    public Guid? CurrentWinnerId { get; set; }
    public int BidCount { get; set; }
    public int ExtensionCount { get; set; }
    public required DateTimeOffset ScheduledEndAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long RowVersion { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
