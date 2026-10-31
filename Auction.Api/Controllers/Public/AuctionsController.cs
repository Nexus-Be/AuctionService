namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Application.Common.Models;
using Auction.Application.Services.Auction;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/auctions")]
public class AuctionsController : ControllerBase
{
    private readonly IAuctionService _auctionService;

    public AuctionsController(IAuctionService auctionService)
    {
        _auctionService = auctionService;
    }

    /// <summary>Lists active auctions with pagination.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedData<AuctionDto>>> ListActive(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var auctions = await _auctionService.ListActiveAuctionsAsync(page, pageSize, ct);
        return Ok(auctions);
    }

    /// <summary>Gets auction details by ID.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuctionDto>> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var auction = await _auctionService.GetByIdAsync(id, ct);
        return Ok(auction);
    }
}
