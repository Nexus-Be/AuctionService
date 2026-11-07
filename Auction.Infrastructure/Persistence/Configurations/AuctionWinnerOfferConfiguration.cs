namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

/// <summary>Cấu hình bảng lưu lời mời bidder thay thế.</summary>
public class AuctionWinnerOfferConfiguration : IEntityTypeConfiguration<AuctionWinnerOffer>
{
    /// <summary>Cấu hình khóa, chỉ mục, độ chính xác tiền và quan hệ của lời mời.</summary>
    public void Configure(EntityTypeBuilder<AuctionWinnerOffer> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.ResponseReason).HasMaxLength(1000);
        builder.HasIndex(x => x.AuctionId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.ExpiresAt });

        builder.HasOne(x => x.Auction)
            .WithMany(a => a.WinnerOffers)
            .HasForeignKey(x => x.AuctionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Bid)
            .WithMany()
            .HasForeignKey(x => x.BidId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
