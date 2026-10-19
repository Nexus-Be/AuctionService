namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionConfiguration : IEntityTypeConfiguration<Auction>
{
    public void Configure(EntityTypeBuilder<Auction> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SnapshotProductTitle).HasMaxLength(500);
        builder.Property(x => x.StartPriceAmount).HasPrecision(18, 2);
        builder.Property(x => x.BidStepAmount).HasPrecision(18, 2);

        builder.HasOne(a => a.RecreatedFromAuction)
            .WithMany(a => a.RecreatedAuctions)
            .HasForeignKey(a => a.RecreatedFromAuctionId);

        builder.HasOne(a => a.AuctionState)
            .WithOne(s => s.Auction)
            .HasForeignKey<AuctionState>(s => s.AuctionId);

        builder.HasOne(a => a.ProductOpenAuction)
            .WithOne(p => p.Auction)
            .HasForeignKey<ProductOpenAuction>(p => p.AuctionId);

        builder.HasMany(a => a.Bids)
            .WithOne(b => b.Auction)
            .HasForeignKey(b => b.AuctionId);

        builder.HasMany(a => a.BidHistories)
            .WithOne(h => h.Auction)
            .HasForeignKey(h => h.AuctionId);

        builder.HasMany(a => a.Extensions)
            .WithOne(e => e.Auction)
            .HasForeignKey(e => e.AuctionId);

        builder.HasOne(a => a.AuctionResult)
            .WithOne(r => r.Auction)
            .HasForeignKey<AuctionResult>(r => r.AuctionId);

        builder.HasOne(a => a.AuctionSettlement)
            .WithOne(s => s.Auction)
            .HasForeignKey<AuctionSettlement>(s => s.AuctionId);

        builder.HasMany(a => a.StatusHistories)
            .WithOne(h => h.Auction)
            .HasForeignKey(h => h.AuctionId);
    }
}
