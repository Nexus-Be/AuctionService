namespace Auction.Api.Controllers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Auction.Application.Services.Auction;
using Microsoft.AspNetCore.Http;
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

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AuctionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListActive([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var auctions = await _auctionService.ListActiveAuctionsAsync(page, pageSize, ct);
        return Ok(auctions);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AuctionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var auction = await _auctionService.GetByIdAsync(id, ct);
        if (auction == null)
        {
            return NotFound(new { message = "Auction not found with the specified ID." });
        }

        return Ok(auction);
    }
}
