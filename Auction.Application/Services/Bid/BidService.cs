namespace Auction.Application.Services.Bid;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;

public sealed class BidService : IBidService
{
    private readonly IAuctionDbContext _dbContext;

    public BidService(IAuctionDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.Bids
            .AsNoTracking()
            .Where(b => b.AuctionId == auctionId);

        var totalCount = await query.CountAsync(cancellationToken);

        var bids = await query
            .OrderByDescending(b => b.PlacedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(b => new BidDto(
                b.Id,
                b.AuctionId,
                b.BidderId,
                b.BidSeq,
                b.Amount,
                b.PlacedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<BidDto>(
            bids,
            PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }
}
