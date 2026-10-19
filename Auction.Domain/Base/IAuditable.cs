namespace Auction.Domain.Base;

public interface IAuditable
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IUserAuditable<TKey>
{
    public TKey? CreatedBy { get; set; }
    public TKey? UpdatedBy { get; set; }
}

public abstract class AuditableEntity<TKey> : BaseEntity<TKey>, IUserAuditable<TKey>
{
    public TKey? CreatedBy { get; set; }
    public TKey? UpdatedBy { get; set; }
}

public abstract class AuditableEntity<TKey, TUserKey> : BaseEntity<TKey>, IUserAuditable<TUserKey>
{
    public TUserKey? CreatedBy { get; set; }
    public TUserKey? UpdatedBy { get; set; }
}
