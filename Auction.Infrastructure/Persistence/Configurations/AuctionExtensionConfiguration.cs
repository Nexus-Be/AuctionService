namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionExtensionConfiguration : IEntityTypeConfiguration<AuctionExtension>
{
    public void Configure(EntityTypeBuilder<AuctionExtension> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasOne(e => e.Auction)
            .WithMany(a => a.Extensions)
            .HasForeignKey(e => e.AuctionId);

        builder.HasOne(e => e.TriggerBid)
            .WithMany()
            .HasForeignKey(e => e.TriggerBidId);
    }
}
