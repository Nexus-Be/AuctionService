using Auction.Domain.Base;
using Auction.Domain.Enums;

namespace Auction.Domain.Entities;

public class IdempotencyKey : BaseEntity<Guid>
{
    public required string Key { get; set; }
    public required IdempotencyScope Scope { get; set; }
    public required string RequestHash { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
}
