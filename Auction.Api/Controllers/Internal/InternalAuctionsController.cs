namespace Auction.Api.Controllers.Internal;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Common;
using Auction.Api.Extensions;
using Auction.Application.Common.Models;
using Auction.Application.Services.Auction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[RequireInternalServiceKey]
[Route("api/v1/internal")]
public class InternalAuctionsController : ControllerBase
{
    private readonly IAuctionService _auctionService;

    public InternalAuctionsController(IAuctionService auctionService)
    {
        _auctionService = auctionService;
    }

    /// <summary>Catalog gets the latest auction status of a product.</summary>
    [HttpGet("auctions/status")]
    [HttpGet("~/internal/v1/auctions/status")]
    [ProducesResponseType(typeof(ProductAuctionStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProductAuctionStatus([FromQuery] Guid productId, CancellationToken ct)
    {
        if (productId == Guid.Empty)
            return this.BadRequestError("VAL_INVALID_INPUT", "Product ID is required.");

        var result = await _auctionService.GetProductAuctionStatusAsync(productId, ct);
        return Ok(result);
    }

    /// <summary>Catalog gets the price and leading bidder summary of an auction.</summary>
    [HttpGet("auctions/{auctionId}/summary")]
    [HttpGet("~/internal/v1/auctions/{auctionId}/summary")]
    [ProducesResponseType(typeof(AuctionInternalSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInternalSummary([FromRoute] Guid auctionId, CancellationToken ct)
    {
        var result = await _auctionService.GetInternalSummaryAsync(auctionId, ct);
        return Ok(result);
    }

    /// <summary>Commerce gets settlement info for an auction to create an order.</summary>
    [HttpGet("auctions/{id:guid}/settlement")]
    [ProducesResponseType(typeof(AuctionSettlementDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuctionSettlementDto>> GetSettlement([FromRoute] Guid id, CancellationToken ct)
    {
        var result = await _auctionService.GetSettlementAsync(id, ct);
        return Ok(result);
    }

    /// <summary>Commerce lists settled auctions (oldest first) for order creation sync.</summary>
    [HttpGet("auction-settlements")]
    [ProducesResponseType(typeof(PagedData<AuctionSettlementDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSettlements(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] DateTimeOffset? determinedFrom = null,
        CancellationToken ct = default)
    {
        var result = await _auctionService.ListSettlementsAsync(page, pageSize, status, determinedFrom, ct);
        return Ok(result);
    }

    /// <summary>Receives payment status and seller decision callbacks from Commerce.</summary>
    [HttpPost("auctions/{id:guid}/settlement/payment-status")]
    [HttpPost("~/api/v1/auctions/{id:guid}/settlement/payment-status")]
    [ProducesResponseType(typeof(SettlementPaymentStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SettlementPaymentStatusResponse>> HandlePaymentStatus(
        [FromRoute] Guid id, [FromBody] SettlementPaymentStatusRequest request, CancellationToken ct)
    {
        var result = await _auctionService.HandlePaymentStatusAsync(id, request, ct);
        return Ok(result);
    }
}
