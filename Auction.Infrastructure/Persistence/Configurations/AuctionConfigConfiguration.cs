namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionConfigConfiguration : IEntityTypeConfiguration<AuctionConfig>
{
    public void Configure(EntityTypeBuilder<AuctionConfig> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ConfigKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ConfigValue).HasColumnType("jsonb");
        builder.Property(x => x.Description).HasMaxLength(500);
        // Publishing a new version flips IsActive on the old row. Two admins doing it at once cannot both succeed.
        builder.Property(x => x.IsActive).IsConcurrencyToken();
    }
}