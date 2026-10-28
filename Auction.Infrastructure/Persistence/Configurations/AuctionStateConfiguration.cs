namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionStateConfiguration : IEntityTypeConfiguration<AuctionState>
{
    public void Configure(EntityTypeBuilder<AuctionState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CurrentPriceAmount).HasPrecision(18, 2);
        builder.Property(x => x.RowVersion).IsConcurrencyToken();

        builder.HasOne(s => s.Auction)
            .WithOne(a => a.AuctionState)
            .HasForeignKey<AuctionState>(s => s.AuctionId);
    }
}
