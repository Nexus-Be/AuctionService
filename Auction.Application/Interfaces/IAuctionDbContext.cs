namespace Auction.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Auction.Domain.Entities;

public interface IAuctionDbContext
{
    DbSet<Auction> Auctions { get; }
    DbSet<AuctionState> AuctionStates { get; }
    DbSet<ProductOpenAuction> ProductOpenAuctions { get; }
    DbSet<Bid> Bids { get; }
    DbSet<BidInvalidation> BidInvalidations { get; }
    DbSet<BidHistory> BidHistories { get; }
    DbSet<AuctionExtension> AuctionExtensions { get; }
    DbSet<AuctionResult> AuctionResults { get; }
    DbSet<AuctionSettlement> AuctionSettlements { get; }
    DbSet<AuctionStatusHistory> AuctionStatusHistories { get; }
    DbSet<AuctionConfig> AuctionConfigs { get; }
    DbSet<IdempotencyKey> IdempotencyKeys { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
