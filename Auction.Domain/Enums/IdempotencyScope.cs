namespace Auction.Domain.Enums;

public enum IdempotencyScope
{
    Bid = 0,
    Settlement = 1,
    Deposit = 2,
    SecondChanceOffer = 3
}