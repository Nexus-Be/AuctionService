namespace Auction.Api.Controllers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BidDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListByAuctionId([FromRoute] Guid auctionId, CancellationToken ct)
    {
        var bids = await _bidService.ListBidsByAuctionIdAsync(auctionId, ct);
        return Ok(bids);
    }
}
