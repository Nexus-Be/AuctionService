using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

/// <summary>Lời mời duy nhất dành cho bidder hợp lệ tiếp theo sau khi người thắng ban đầu không hoàn tất giao dịch.</summary>
public class AuctionWinnerOffer : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required Guid BidId { get; set; }
    public required Guid BidderId { get; set; }
    public required decimal Amount { get; set; }
    public required AuctionWinnerOfferStatus Status { get; set; } = AuctionWinnerOfferStatus.Pending;
    public required DateTimeOffset OfferedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? ResponseReason { get; set; }

    public Auction? Auction { get; set; }
    public Bid? Bid { get; set; }
}
