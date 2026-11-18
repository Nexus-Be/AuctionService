namespace Auction.Infrastructure.Persistence.Configurations;

using Auction.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AuctionReportConfiguration : IEntityTypeConfiguration<AuctionReport>
{
    public void Configure(EntityTypeBuilder<AuctionReport> builder)
    {
        builder.ToTable("AuctionReports");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AdminNote).HasMaxLength(1000);

        builder.HasIndex(x => new { x.AuctionId, x.ReporterId }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
        builder.HasIndex(x => new { x.ReporterId, x.CreatedAt });

        builder.HasOne(x => x.Auction)
            .WithMany()
            .HasForeignKey(x => x.AuctionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
