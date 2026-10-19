namespace Auction.Domain.Enums;

public enum AuditAction
{
    AuctionCreate = 0,
    AuctionUpdate = 1,
    AuctionCancel = 2,
    BidPlace = 3,
    BidInvalidate = 4,
    AuctionSettle = 5,
    ConfigUpdate = 6
}
