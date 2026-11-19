  using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

/// <summary>
/// One notification for the Notification service. The row is added in the same unit of work (the same SaveChanges) as the business change that
/// causes it, so a process that stops right after the commit cannot lose the event. A worker sends it afterwards and retries with the same
/// <see cref="EventId"/> and the same frozen <see cref="PayloadJson"/>.
/// </summary>
public class NotificationOutboxItem : BaseEntity<Guid>
{
    /// <summary>Idempotency key sent to Notification as sourceEventId. Unique: the same event can never be queued twice.</summary>
    public required Guid EventId { get; set; }

    /// <summary>auction-outbids, auction-winners, auction-failed, auction-cancelled, auction-winner-offers or auction-order-payments.</summary>
    public required string Kind { get; set; }

    public required Guid AuctionId { get; set; }

    /// <summary>The request body, frozen once complete. Null while <see cref="Status"/> is AwaitingData.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>What a worker needs to fetch the missing data of an AwaitingData row (user id, order id, result id ...), as JSON.</summary>
    public string? ContextJson { get; set; }

    public required NotificationOutboxStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextRetryAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Lease so that two instances never send the same row at the same time.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>After this moment an AwaitingData or Pending row that still has not worked becomes NeedsAttention.</summary>
    public DateTimeOffset GiveUpAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
}
