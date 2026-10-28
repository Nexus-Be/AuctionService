namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class BidInvalidationConfiguration : IEntityTypeConfiguration<BidInvalidation>
{
    public void Configure(EntityTypeBuilder<BidInvalidation> builder)
    {
        builder.HasKey(x => x.Id);

        builder.HasOne(i => i.Bid)
            .WithOne(b => b.BidInvalidation)
            .HasForeignKey<BidInvalidation>(i => i.BidId);
    }
}
