namespace Auction.Api.Controllers;

using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Extensions;
using Auction.Application.Common.Models;
using Auction.Application.Services.AuctionConfig;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/admin/auction-configs")]
public class AuctionConfigsController : ControllerBase
{
    private readonly IAuctionConfigService _configService;

    public AuctionConfigsController(IAuctionConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>Admin lists the active auction configurations.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedData<AuctionConfigDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.EnsureAdmin(out var adminError)) return adminError;

        var result = await _configService.ListAsync(page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>
    /// Admin xem lịch sử các phiên bản của một cấu hình đấu giá (ví dụ DEFAULT_AUCTION_POLICY), mới nhất trước.
    /// Mỗi phiên bản cho biết giá trị, trạng thái đang dùng, người cập nhật, thời điểm và các trường đã thay đổi so với phiên bản trước.
    /// </summary>
    [HttpGet("{configKey}/history")]
    [ProducesResponseType(typeof(PagedData<AuctionConfigHistoryItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromRoute] string configKey,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.EnsureAdmin(out var adminError)) return adminError;

        var result = await _configService.GetHistoryAsync(configKey, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Admin publishes a new version of the auction policy (anti-sniping settings and the default bid step percent). Only auctions created afterwards use the new values.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(AuctionConfigDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateAuctionConfigRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureAdmin(out var adminError)) return adminError;

        var actor = new ActorContext(userId, this.GetTraceId(), this.GetClientIp());
        var result = await _configService.UpdateAsync(actor, request, ct);
        return Ok(result);
    }

    /// <summary>Admin views current deposit policy and version history.</summary>
    [HttpGet("deposit-policy")]
    [ProducesResponseType(typeof(DepositPolicyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDepositPolicy([FromQuery] int? version, CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.EnsureAdmin(out var adminError)) return adminError;

        var result = await _configService.GetDepositPolicyAsync(version, ct);
        return Ok(result);
    }

    /// <summary>Admin updates deposit policy, creating a new version. Requires If-Match header with current version.</summary>
    [HttpPut("deposit-policy")]
    [ProducesResponseType(typeof(UpdateDepositPolicyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateDepositPolicy([FromBody] UpdateDepositPolicyRequest request, CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureAdmin(out var adminError)) return adminError;

        var ifMatch = Request.Headers["If-Match"].FirstOrDefault();
        var actor = new ActorContext(userId, this.GetTraceId(), this.GetClientIp());
        var result = await _configService.UpdateDepositPolicyAsync(actor, ifMatch, request, ct);
        return Ok(result);
    }
}