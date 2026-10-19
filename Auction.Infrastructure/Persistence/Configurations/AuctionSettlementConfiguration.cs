namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionSettlementConfiguration : IEntityTypeConfiguration<AuctionSettlement>
{
    public void Configure(EntityTypeBuilder<AuctionSettlement> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasOne(s => s.Auction)
            .WithOne(a => a.AuctionSettlement)
            .HasForeignKey<AuctionSettlement>(s => s.AuctionId);
    }
}
