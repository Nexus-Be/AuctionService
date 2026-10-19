namespace Auction.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auction.Domain.Entities;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ServiceName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TraceId).HasMaxLength(100);
        builder.Property(x => x.UserId).HasMaxLength(100);
        builder.Property(x => x.UserName).HasMaxLength(255);
        builder.Property(x => x.Action).HasConversion<string>();
        builder.Property(x => x.EntityName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.EntityId).HasMaxLength(150).IsRequired();
        builder.Property(x => x.OldValues).HasColumnType("jsonb");
        builder.Property(x => x.NewValues).HasColumnType("jsonb");
        builder.Property(x => x.ChangedColumns).HasColumnType("jsonb");
        builder.Property(x => x.IpAddress).HasMaxLength(50);
        builder.Property(x => x.UserAgent).HasMaxLength(500);
    }
}
