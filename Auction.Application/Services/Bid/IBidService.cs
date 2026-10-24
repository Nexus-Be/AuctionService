namespace Auction.Application.Services.Bid;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IBidService
{
    Task<IReadOnlyList<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, CancellationToken cancellationToken = default);
}
