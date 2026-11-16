namespace Auction.Domain.Enums;

public enum AuditAction
{
    AuctionCreate = 0,
    AuctionUpdate = 1,
    AuctionCancel = 2,
    BidPlace = 3,
    BidInvalidate = 4,
    AuctionSettle = 5,
    ConfigUpdate = 6,
    WinnerOfferAccept = 7,
    WinnerOfferDecline = 8,
    AuctionStart = 9,
    AuctionEnd = 10,
    WinnerOfferCreate = 11,
    WinnerOfferExpire = 12,
    SellerAccept = 13,
    DepositCreate = 14,
    DepositAuthorize = 15,
    DepositFail = 16,
    DepositExpire = 17,
    DepositRelease = 18
}