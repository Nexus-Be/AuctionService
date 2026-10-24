namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IAuctionService
{
    Task<AuctionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuctionDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
}
