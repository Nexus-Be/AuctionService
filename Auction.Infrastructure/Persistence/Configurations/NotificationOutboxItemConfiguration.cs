namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class NotificationOutboxItemConfiguration : IEntityTypeConfiguration<NotificationOutboxItem>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxItem> builder)
    {
        builder.ToTable("NotificationOutbox");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(60).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("text");
        builder.Property(x => x.ContextJson).HasColumnType("text");
        builder.Property(x => x.LastError).HasMaxLength(500);

        builder.HasIndex(x => x.EventId).IsUnique();
        // The worker looks for due rows by status and time.
        builder.HasIndex(x => new { x.Status, x.NextRetryAt });
        builder.HasIndex(x => x.AuctionId);
    }
}
