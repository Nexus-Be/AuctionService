namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class ProductOpenAuctionConfiguration : IEntityTypeConfiguration<ProductOpenAuction>
{
    public void Configure(EntityTypeBuilder<ProductOpenAuction> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.ProductId).IsUnique();

        builder.HasOne(p => p.Auction)
            .WithOne(a => a.ProductOpenAuction)
            .HasForeignKey<ProductOpenAuction>(p => p.AuctionId);
    }
}
