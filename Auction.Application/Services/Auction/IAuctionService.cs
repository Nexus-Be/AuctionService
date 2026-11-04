namespace Auction.Application.Services.Auction;

using System;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IAuctionService
{
    /// <summary>Lists auctions (newest start time first) with an optional status filter.</summary>
    Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default);

    /// <summary>Gets the detail of one auction.</summary>
    Task<AuctionDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Seller creates an auction. The new auction is Scheduled.</summary>
    Task<AuctionCreatedResponse> CreateAsync(ActorContext actor, CreateAuctionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Seller edits their own auction before it starts.</summary>
    Task<AuctionUpdatedResponse> UpdateAsync(ActorContext actor, Guid auctionId, UpdateAuctionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Seller cancels their own auction before it starts.</summary>
    Task<AuctionCancelledResponse> CancelAsync(ActorContext actor, Guid auctionId, CancelAuctionRequest? request, CancellationToken cancellationToken = default);

    /// <summary>Seller creates a new auction for the product of one of their Failed auctions.</summary>
    Task<AuctionRecreatedResponse> RecreateAsync(ActorContext actor, Guid auctionId, RecreateAuctionRequest request, CancellationToken cancellationToken = default);
}