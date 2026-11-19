namespace Auction.Infrastructure.Persistence.Configurations;

using Auction.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SellerReservePenaltyConfiguration : IEntityTypeConfiguration<SellerReservePenalty>
{
    public void Configure(EntityTypeBuilder<SellerReservePenalty> builder)
    {
        builder.ToTable("SellerReservePenalties");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.SellerId).IsUnique();
    }
}
