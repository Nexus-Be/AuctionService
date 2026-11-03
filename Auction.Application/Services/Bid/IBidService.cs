namespace Auction.Application.Services.Bid;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IBidService
{
    /// <summary>Lists the bid history of an auction, newest first, with masked bidder names.</summary>
    Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places a bid. The row version of the auction must match, and the same idempotency key with the
    /// same request returns the first result instead of creating a second bid.
    /// </summary>
    Task<PlaceBidResponse> PlaceBidAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, string? ifMatch,
        PlaceBidRequest request, CancellationToken cancellationToken = default);
}