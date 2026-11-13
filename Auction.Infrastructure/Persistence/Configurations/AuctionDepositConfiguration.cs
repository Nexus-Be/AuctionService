namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

/// <summary>Cấu hình bảng cọc đấu giá: mỗi bidder chỉ có một cọc trong một phiên.</summary>
public class AuctionDepositConfiguration : IEntityTypeConfiguration<AuctionDeposit>
{
    public void Configure(EntityTypeBuilder<AuctionDeposit> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.RatePercent).HasPrecision(5, 2);
        builder.Property(x => x.GatewayReference).HasMaxLength(200);
        builder.Property(x => x.GatewayMode).HasMaxLength(50);
        builder.Property(x => x.PaymentRedirectUrl).HasMaxLength(2000);
        builder.Property(x => x.FailureCode).HasMaxLength(100);
        builder.Property(x => x.ReleaseReason).HasMaxLength(50);
        builder.Property(x => x.RowVersion).IsConcurrencyToken();

        builder.HasIndex(x => new { x.AuctionId, x.BidderId }).IsUnique();
        builder.HasIndex(x => x.BidderId);

        builder.HasOne(x => x.Auction)
            .WithMany()
            .HasForeignKey(x => x.AuctionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
