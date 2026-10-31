namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

public sealed class AuctionService : IAuctionService
{
    private readonly IAuctionDbContext _dbContext;

    public AuctionService(IAuctionDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AuctionDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", id);

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

    public async Task<PagedData<AuctionDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var totalCount = await _dbContext.Auctions.CountAsync(cancellationToken);

        var auctions = await _dbContext.Auctions
            .AsNoTracking()
            .OrderByDescending(a => a.ScheduledStartAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
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

        return new PagedData<AuctionDto>(auctions, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }
}
