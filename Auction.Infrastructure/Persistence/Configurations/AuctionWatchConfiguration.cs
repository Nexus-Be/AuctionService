namespace Auction.Infrastructure.Persistence.Configurations;

using Auction.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AuctionWatchConfiguration : IEntityTypeConfiguration<AuctionWatch>
{
    public void Configure(EntityTypeBuilder<AuctionWatch> builder)
    {
        builder.ToTable("AuctionWatches");
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.AuctionId, x.UserId }).IsUnique();
        builder.HasIndex(x => new { x.UserId, x.CreatedAt });

        builder.HasOne(x => x.Auction)
            .WithMany()
            .HasForeignKey(x => x.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
