namespace Auction.Domain.Enums;

public enum AuctionStatus
{
    Draft = 0,
    Scheduled = 1,
    Running = 2,
    Ended = 3,
    Settled = 4,
    Failed = 5,
    Cancelled = 6
}
