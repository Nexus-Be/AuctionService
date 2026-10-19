namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuctionStatusHistoryConfiguration : IEntityTypeConfiguration<AuctionStatusHistory>
{
    public void Configure(EntityTypeBuilder<AuctionStatusHistory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FromStatus).HasConversion<string>();
        builder.Property(x => x.ToStatus).HasConversion<string>();
        builder.Property(x => x.Reason).HasMaxLength(1000);

        builder.HasOne(h => h.Auction)
            .WithMany(a => a.StatusHistories)
            .HasForeignKey(h => h.AuctionId);
    }
}
