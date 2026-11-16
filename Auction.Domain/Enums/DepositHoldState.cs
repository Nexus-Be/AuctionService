namespace Auction.Domain.Enums;

/// <summary>Trạng thái của một khoản hold do Wallet/Payment báo về. Không lưu trong database của Auction.</summary>
public enum DepositHoldState
{
    PendingAuthorization = 0,
    Authorized = 1,
    Captured = 2,
    Released = 3,
    Voided = 4,
    Expired = 5,
    Failed = 6
}
