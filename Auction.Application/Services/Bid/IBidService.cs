namespace Auction.Application.Services.Bid;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IBidService
{
    Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default);
}
