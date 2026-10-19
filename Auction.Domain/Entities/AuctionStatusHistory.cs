using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class AuctionStatusHistory : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public AuctionStatus? FromStatus { get; set; }
    public required AuctionStatus ToStatus { get; set; }
    public string? Reason { get; set; }
    public Guid? ChangedBy { get; set; }
    public required DateTimeOffset ChangedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
