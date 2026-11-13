namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Common;
using Auction.Api.Extensions;
using Auction.Application.Common.Models;
using Auction.Application.Services.Deposit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/auctions/{auctionId:guid}/deposits")]
public class DepositsController : ControllerBase
{
    private readonly IDepositService _depositService;

    public DepositsController(IDepositService depositService)
    {
        _depositService = depositService;
    }

    /// <summary>Buyer sees the deposit amount, rate, forfeiture policy and current deposit for an auction before paying.</summary>
    [HttpGet("~/api/v1/auctions/{auctionId:guid}/deposit-requirement")]
    [ProducesResponseType(typeof(DepositRequirementResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequirement([FromRoute] Guid auctionId, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!CanUseDeposits()) return RoleNotAllowed();

        var result = await _depositService.GetRequirementAsync(CreateActor(userId), auctionId, ct);
        return Ok(result);
    }

    /// <summary>Buyer creates a deposit by locking available Nexus Wallet balance synchronously. Needs the Idempotency-Key header. Returns 200 when an existing deposit is returned.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateDepositResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateDepositResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromRoute] Guid auctionId, [FromBody] CreateDepositRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!CanUseDeposits()) return RoleNotAllowed();

        var result = await _depositService.CreateAsync(CreateActor(userId), auctionId, this.GetIdempotencyKey(), request, ct);
        return StatusCode(result.StatusCode, result.Body);
    }

    /// <summary>Buyer views their own deposit status in an auction and whether they can place a bid (API 03).</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(MyDepositResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDeposit([FromRoute] Guid auctionId, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!CanUseDeposits()) return RoleNotAllowed();

        var result = await _depositService.GetMyDepositAsync(CreateActor(userId), auctionId, ct);
        return Ok(result);
    }

    // [Deprecated in v0.4 / Direction C — Synchronous Wallet balance hold replaces async callback]
    // [HttpPost("~/api/v1/deposits/{depositId:guid}/authorization-result")]
    // [RequireInternalServiceKey]
    // [ProducesResponseType(typeof(DepositAuthorizationResultResponse), StatusCodes.Status200OK)]
    // public async Task<ActionResult<DepositAuthorizationResultResponse>> HandleAuthorizationResult(
    //     [FromRoute] Guid depositId, [FromBody] DepositAuthorizationResultRequest request, CancellationToken ct)
    // {
    //     var result = await _depositService.ApplyAuthorizationResultAsync(depositId, request, ct);
    //     return Ok(result);
    // }

    /// <summary>Admin or Staff reviews all deposits for an auction (for reconciliation, complaints and stuck operation monitoring).</summary>
    [HttpGet("~/api/v1/admin/auctions/{auctionId:guid}/deposits")]
    [ProducesResponseType(typeof(AdminAuctionDepositsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAdminAuctionDeposits(
        [FromRoute] Guid auctionId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!CanAdminReviewDeposits()) return AdminOrStaffRoleRequired();

        var result = await _depositService.GetAdminAuctionDepositsAsync(
            CreateActor(userId), auctionId, status, page, pageSize, ct);
        return Ok(result);
    }

    // Same rule as bidding: buyer or seller accounts (a seller may bid on other sellers' auctions). Admin and staff may not.
    private bool CanUseDeposits() => this.HasRole("buyer") || this.HasRole("seller");

    private bool CanAdminReviewDeposits() => this.HasRole("admin") || this.HasRole("staff");

    private IActionResult RoleNotAllowed() =>
        this.ForbiddenError("Only buyer or seller accounts can use auction deposits.", "ROLE_NOT_ALLOWED");

    private IActionResult AdminOrStaffRoleRequired() =>
        this.ForbiddenError("Access denied: Administrator or Staff privileges are required to review auction deposits.");

    private ActorContext CreateActor(Guid userId) => new(userId, this.GetTraceId(), this.GetClientIp());
}
