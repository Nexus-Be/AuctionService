namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;
using Auction.Domain.Enums;

/// <summary>Cấu hình bảng settlement và quan hệ với phiên đấu giá, bid thắng.</summary>
public class AuctionSettlementConfiguration : IEntityTypeConfiguration<AuctionSettlement>
{
    /// <summary>Cấu hình trạng thái, chỉ mục và quan hệ khóa ngoại của settlement.</summary>
    public void Configure(EntityTypeBuilder<AuctionSettlement> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasDefaultValue(AuctionSettlementStatus.PendingPayment);
        builder.HasIndex(x => new { x.Status, x.PaymentDeadlineAt });

        builder.HasOne(s => s.Auction)
            .WithOne(a => a.AuctionSettlement)
            .HasForeignKey<AuctionSettlement>(s => s.AuctionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.WinningBid)
            .WithMany()
            .HasForeignKey(s => s.WinningBidId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
