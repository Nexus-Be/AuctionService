namespace Auction.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Auction.Application.Interfaces;
using Auction.Domain.Entities;

public class AppDbContext : DbContext, IAuctionDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<AuctionState> AuctionStates => Set<AuctionState>();
    public DbSet<ProductOpenAuction> ProductOpenAuctions => Set<ProductOpenAuction>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<BidInvalidation> BidInvalidations => Set<BidInvalidation>();
    public DbSet<BidHistory> BidHistories => Set<BidHistory>();
    public DbSet<AuctionExtension> AuctionExtensions => Set<AuctionExtension>();
    public DbSet<AuctionResult> AuctionResults => Set<AuctionResult>();
    public DbSet<AuctionSettlement> AuctionSettlements => Set<AuctionSettlement>();
    public DbSet<AuctionStatusHistory> AuctionStatusHistories => Set<AuctionStatusHistory>();
    public DbSet<AuctionConfig> AuctionConfigs => Set<AuctionConfig>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        Seeds.AuctionSeedData.SeedAll(modelBuilder);
    }
}
