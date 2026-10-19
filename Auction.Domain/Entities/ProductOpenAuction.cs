using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class ProductOpenAuction : BaseEntity<Guid>
{
    public required Guid ProductId { get; set; }
    public required Guid AuctionId { get; set; }

    // Navigation
    public Auction? Auction { get; set; }
}
