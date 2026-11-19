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

    /// <summary>Deadline for seller to accept or decline a below-reserve winning bid (12 hours).</summary>
    public DateTimeOffset? SellerApprovalDeadlineAt { get; set; }

    /// <summary>True when the highest bid at auction end was below ReservePriceAmount (no second-chance on non-payment).</summary>
    public bool IsBelowReserve { get; set; }

    /// <summary>True when the auction was closed via Buy Now.</summary>
    public bool IsBuyNow { get; set; }

    /// <summary>Reference ID sent to WalletService for the remaining goods hold (BuyNowPrice - DepositAmount).</summary>
    public Guid? BuyNowGoodsDepositId { get; set; }

    /// <summary>Wallet Hold ID for the remaining goods amount when closed via Buy Now.</summary>
    public string? BuyNowGoodsHoldId { get; set; }

    /// <summary>Remaining goods amount held in Wallet for Buy Now (BuyNowPrice - DepositAmount).</summary>
    public decimal? BuyNowGoodsAmount { get; set; }

    /// <summary>Background action for the Buy Now goods hold (Capture or Release when Buy Now fails/expires).</summary>
    public DepositPendingAction BuyNowGoodsPendingAction { get; set; } = DepositPendingAction.None;

    // Navigation
    public Auction? Auction { get; set; }
    public Bid? WinningBid { get; set; }
}
