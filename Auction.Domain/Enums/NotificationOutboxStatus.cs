namespace Auction.Domain.Enums;

/// <summary>State of a notification waiting in the outbox.</summary>
public enum NotificationOutboxStatus
{
    /// <summary>The payload is final and the row is waiting to be sent (or to be sent again).</summary>
    Pending = 0,

    /// <summary>Data the payload needs (an email address, a shop id, order details) has to be fetched first; nothing may be sent yet.</summary>
    AwaitingData = 1,

    /// <summary>Notification accepted the event.</summary>
    Sent = 2,

    /// <summary>Permanent failure or retries used up: a person has to look at it. Never sent again on its own.</summary>
    NeedsAttention = 3
}
