namespace Auction.Application.Services.Bid;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Interfaces;

public sealed class BidService : IBidService
{
    private readonly IAuctionDbContext _dbContext;

    public BidService(IAuctionDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var bids = await _dbContext.Bids
            .AsNoTracking()
            .Where(b => b.AuctionId == auctionId)
            .OrderByDescending(b => b.PlacedAt)
            .Select(b => new BidDto(
                b.Id,
                b.AuctionId,
                b.BidderId,
                b.BidSeq,
                b.Amount,
                b.PlacedAt))
            .ToListAsync(cancellationToken);

        return bids;
    }
}
