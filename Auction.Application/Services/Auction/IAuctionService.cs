namespace Auction.Application.Services.Auction;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IAuctionService
{
    Task<AuctionDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedData<AuctionDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
}
