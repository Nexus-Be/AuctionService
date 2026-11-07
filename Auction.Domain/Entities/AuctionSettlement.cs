using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class AuctionSettlement : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }

    /// <summary>
    /// Gets or sets the Winner ID.
    /// </summary>
    public Guid? WinnerId { get; set; }

    /// <summary>
    /// Gets or sets the Winning Bid ID.
    /// </summary>
    public Guid? WinningBidId { get; set; }

    /// <summary>
    /// Gets or sets the Settlement Status.
    /// </summary>
    public required AuctionSettlementStatus Status { get; set; } = AuctionSettlementStatus.PendingPayment;

    /// <summary>
    /// Gets or sets the Order ID.
    /// </summary>
    public Guid? OrderId { get; set; }

    /// <summary>
    /// Gets or sets the Payment Deadline.
    /// </summary>
    public DateTimeOffset? PaymentDeadlineAt { get; set; }

    /// <summary>
    /// Gets or sets the Payment Completion Time.
    /// </summary>
    public DateTimeOffset? PaidAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
    public Bid? WinningBid { get; set; }
}
