using Auction.Domain.Base;

namespace Auction.Domain.Entities;

/// <summary>
/// Tracks consecutive below-reserve rejections by a seller.
/// After 3 consecutive rejections, ReservePrice is restricted for 14 days; accepting a below-reserve sale resets the count to 0.
/// </summary>
public class SellerReservePenalty : BaseEntity<Guid>
{
    public required Guid SellerId { get; set; }
    public int ConsecutiveDeclineCount { get; set; }
    public DateTimeOffset? ReserveRestrictedUntil { get; set; }
}
