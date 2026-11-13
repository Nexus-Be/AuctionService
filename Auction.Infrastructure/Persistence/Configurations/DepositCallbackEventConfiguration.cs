namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

/// <summary>Cấu hình Inbox của callback Wallet: EventId là duy nhất nên mỗi sự kiện chỉ được xử lý một lần.</summary>
public class DepositCallbackEventConfiguration : IEntityTypeConfiguration<DepositCallbackEvent>
{
    public void Configure(EntityTypeBuilder<DepositCallbackEvent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.EventId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Result).HasMaxLength(30);

        builder.HasIndex(x => x.EventId).IsUnique();
        builder.HasIndex(x => x.DepositId);

        builder.HasOne(x => x.Deposit)
            .WithMany()
            .HasForeignKey(x => x.DepositId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
