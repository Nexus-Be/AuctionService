namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Extensions;
using Auction.Application.Common.Models;
using Auction.Application.Services.Auction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

[ApiController]
[Route("api/v1/auctions")]
public class AuctionsController : ControllerBase
{
    private readonly IAuctionService _auctionService;

    public AuctionsController(IAuctionService auctionService)
    {
        _auctionService = auctionService;
    }

    /// <summary>Lists auctions (newest start time first) with pagination and an optional status filter.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedData<AuctionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedData<AuctionSummaryDto>>> ListActive(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var auctions = await _auctionService.ListActiveAuctionsAsync(page, pageSize, status, ct);
        return Ok(auctions);
    }

    /// <summary>Gets auction details by ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AuctionDetailDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuctionDetailDto>> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var auction = await _auctionService.GetByIdAsync(id, ct);
        return Ok(auction);
    }

    /// <summary>Seller creates an auction. The new auction is Scheduled.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AuctionCreatedResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateAuctionRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureRole("seller", out var roleError)) return roleError;

        var result = await _auctionService.CreateAsync(CreateActor(userId), request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.AuctionId }, result);
    }

    /// <summary>Seller edits their own auction before it starts.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(AuctionUpdatedResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdateAuctionRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureRole("seller", out var roleError)) return roleError;

        var result = await _auctionService.UpdateAsync(CreateActor(userId), id, request, ct);
        return Ok(result);
    }

    /// <summary>Seller cancels their own auction before it starts. The reason is optional.</summary>
    [HttpDelete("{id:guid}/cancel")]
    [ProducesResponseType(typeof(AuctionCancelledResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(
        [FromRoute] Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelAuctionRequest? request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureRole("seller", out var roleError)) return roleError;

        var result = await _auctionService.CancelAsync(CreateActor(userId), id, request, ct);
        return Ok(result);
    }

    /// <summary>Seller creates a new auction for the product of one of their Failed auctions.</summary>
    [HttpPost("{id:guid}/recreate")]
    [ProducesResponseType(typeof(AuctionRecreatedResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Recreate([FromRoute] Guid id, [FromBody] RecreateAuctionRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureRole("seller", out var roleError)) return roleError;

        var result = await _auctionService.RecreateAsync(CreateActor(userId), id, request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.AuctionId }, result);
    }

    private ActorContext CreateActor(Guid userId) => new(userId, this.GetTraceId(), this.GetClientIp());
}