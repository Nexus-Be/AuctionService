namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Scope).HasConversion<string>();
        builder.Property(x => x.RequestHash).HasMaxLength(500);
        builder.Property(x => x.ResponseBody).HasColumnType("jsonb");
    }
}
