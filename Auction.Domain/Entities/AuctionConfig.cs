using Auction.Domain.Base;

namespace Auction.Domain.Entities;

public class AuctionConfig : BaseEntity<Guid>
{
    public required string ConfigKey { get; set; }
    public required string ConfigValue { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? UpdatedBy { get; set; }
}
