namespace Auction.Domain.Enums;

/// <summary>Trạng thái đồng bộ việc nền không qua Outbox (Overview 4.1).</summary>
public enum BackgroundSyncState
{
    None = 0,
    Pending = 1,
    Done = 2
}
