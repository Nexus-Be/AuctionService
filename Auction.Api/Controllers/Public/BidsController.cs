namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Extensions;
using Auction.Application.Common.Models;
using Auction.Application.Services.Bid;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/auctions/{auctionId:guid}/bids")]
public class BidsController : ControllerBase
{
    private readonly IBidService _bidService;

    public BidsController(IBidService bidService)
    {
        _bidService = bidService;
    }

    /// <summary>Lists bids for a specific auction (newest first, bidder names are masked).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedData<BidDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedData<BidDto>>> ListByAuctionId(
        [FromRoute] Guid auctionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var bids = await _bidService.ListBidsByAuctionIdAsync(auctionId, page, pageSize, ct);
        return Ok(bids);
    }

    /// <summary>Buyer lists their own bids across auctions (newest first).</summary>
    [HttpGet("~/api/v1/me/bids")]
    [ProducesResponseType(typeof(PagedData<MyBidDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyBids(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can use bids.");

        var result = await _bidService.ListMyBidsAsync(userId, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Buyer places a bid. Needs the Idempotency-Key and If-Match (auction row version) headers.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PlaceBidResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> PlaceBid([FromRoute] Guid auctionId, [FromBody] PlaceBidRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can use bids.");

        var actor = new ActorContext(userId, this.GetTraceId(), this.GetClientIp());
        var result = await _bidService.PlaceBidAsync(
            actor, auctionId, this.GetIdempotencyKey(), Request.Headers["If-Match"].ToString(), request, ct);

        return StatusCode(StatusCodes.Status201Created, result);
    }
}