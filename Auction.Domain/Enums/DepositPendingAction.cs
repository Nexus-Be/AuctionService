namespace Auction.Domain.Enums;

/// <summary>Việc nền đang chờ thực hiện trên cọc đấu giá (Overview 4.1 &amp; v0.4).</summary>
public enum DepositPendingAction
{
    None = 0,
    Release = 1,
    Capture = 2,
    Refund = 3
}
