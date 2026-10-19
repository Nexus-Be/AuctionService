namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class BidConfiguration : IEntityTypeConfiguration<Bid>
{
    public void Configure(EntityTypeBuilder<Bid> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasPrecision(18, 2);

        builder.HasOne(b => b.Auction)
            .WithMany(a => a.Bids)
            .HasForeignKey(b => b.AuctionId);

        builder.HasOne(b => b.BidInvalidation)
            .WithOne(i => i.Bid)
            .HasForeignKey<BidInvalidation>(i => i.BidId);

        builder.HasMany(b => b.BidHistories)
            .WithOne(h => h.Bid)
            .HasForeignKey(h => h.BidId);
    }
}
