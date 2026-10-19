using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ServiceName { get; set; }
    public string? TraceId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public required AuditAction Action { get; set; }
    public required string EntityName { get; set; }
    public required string EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? ChangedColumns { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsSynced { get; set; }
}
