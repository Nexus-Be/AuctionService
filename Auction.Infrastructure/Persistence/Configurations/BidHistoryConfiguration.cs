namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class BidHistoryConfiguration : IEntityTypeConfiguration<BidHistory>
{
    public void Configure(EntityTypeBuilder<BidHistory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.DisplayName).HasMaxLength(150);
        builder.Property(x => x.Amount).HasPrecision(18, 2);

        builder.HasOne(h => h.Auction)
            .WithMany(a => a.BidHistories)
            .HasForeignKey(h => h.AuctionId);

        builder.HasOne(h => h.Bid)
            .WithMany(b => b.BidHistories)
            .HasForeignKey(h => h.BidId);
    }
}
