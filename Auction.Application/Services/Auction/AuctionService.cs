namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Interfaces;

public sealed class AuctionService : IAuctionService
{
    private readonly IAuctionDbContext _dbContext;

    public AuctionService(IAuctionDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AuctionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (auction == null) return null;

        return new AuctionDto(
            auction.Id,
            auction.ProductId,
            auction.SellerId,
            auction.SnapshotProductTitle,
            auction.StartPriceAmount,
            auction.BidStepAmount,
            auction.ScheduledStartAt,
            auction.OriginalEndAt);
    }

    public async Task<IReadOnlyList<AuctionDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var auctions = await _dbContext.Auctions
            .AsNoTracking()
            .OrderByDescending(a => a.ScheduledStartAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuctionDto(
                a.Id,
                a.ProductId,
                a.SellerId,
                a.SnapshotProductTitle,
                a.StartPriceAmount,
                a.BidStepAmount,
                a.ScheduledStartAt,
                a.OriginalEndAt))
            .ToListAsync(cancellationToken);

        return auctions;
    }
}
