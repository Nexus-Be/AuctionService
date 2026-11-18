using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class AuctionReport : BaseEntity<Guid>
{
    public required Guid AuctionId { get; set; }
    public required Guid ReporterId { get; set; }
    public required AuctionReportReason Reason { get; set; }
    public required string Description { get; set; }
    public AuctionReportStatus Status { get; set; } = AuctionReportStatus.Pending;
    public string? AdminNote { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
