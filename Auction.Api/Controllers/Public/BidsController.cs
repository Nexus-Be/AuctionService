namespace Auction.Api.Controllers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>Lists bids for a specific auction.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedData<BidDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedData<BidDto>>> ListByAuctionId(
        [FromRoute] Guid auctionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var bids = await _bidService.ListBidsByAuctionIdAsync(auctionId, page, pageSize, ct);
        return Ok(bids);
    }
}
