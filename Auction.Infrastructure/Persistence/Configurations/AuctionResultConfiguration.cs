namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionResultConfiguration : IEntityTypeConfiguration<AuctionResult>
{
    public void Configure(EntityTypeBuilder<AuctionResult> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FinalPriceAmount).HasPrecision(18, 2);

        builder.HasOne(r => r.Auction)
            .WithOne(a => a.AuctionResult)
            .HasForeignKey<AuctionResult>(r => r.AuctionId);

        builder.HasOne(r => r.WinningBid)
            .WithMany()
            .HasForeignKey(r => r.WinningBidId);
    }
}
