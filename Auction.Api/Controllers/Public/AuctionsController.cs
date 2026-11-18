namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Common;
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

    /// <summary>Lists auctions with pagination, search, filters and sorting.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedData<AuctionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedData<AuctionSummaryDto>>> ListActive(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? keyword = null,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] int? endingWithinMinutes = null,
        [FromQuery] string? sortBy = null,
        CancellationToken ct = default)
    {
        var filter = new AuctionListFilter(status, keyword, sellerId, productId, minPrice, maxPrice, endingWithinMinutes, sortBy);
        var auctions = await _auctionService.ListActiveAuctionsAsync(page, pageSize, filter, ct);
        return Ok(auctions);
    }

    /// <summary>Buyer views all auctions they participated in, grouped one row per auction.</summary>
    [HttpGet("~/api/v1/me/auctions")]
    [ProducesResponseType(typeof(PagedData<MyParticipatedAuctionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyAuctions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? participationState = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can view participated auctions.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListMyAuctionsAsync(userId, page, pageSize, participationState, status, ct);
        return Ok(result);
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
    [HttpPost("{id:guid}/cancel")]
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

    /// <summary>Lists the status changes of an auction (newest first). Only its seller, an admin or a staff member can view it.</summary>
    [HttpGet("{id:guid}/status-history")]
    [ProducesResponseType(typeof(PagedData<AuctionStatusHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusHistory(
        [FromRoute] Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;

        var isPrivileged = this.IsAdmin() || this.HasRole("staff");
        var result = await _auctionService.GetStatusHistoryAsync(CreateActor(userId), isPrivileged, id, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Gets the post-auction result and phase (winner, final price, settlement, myDeposit and secondChance) filtered by caller role (API 06).</summary>
    [HttpGet("{id:guid}/result")]
    [ProducesResponseType(typeof(AuctionResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuctionResultDto>> GetResult([FromRoute] Guid id, CancellationToken ct)
    {
        var callerId = this.GetUserId();
        var isPrivileged = this.IsAdmin() || this.HasRole("staff");
        var result = await _auctionService.GetResultAsync(id, callerId, isPrivileged, ct);
        return Ok(result);
    }

    /// <summary>Runner-up bidder views their own second-chance offer for a specific auction (API 09).</summary>
    [HttpGet("{id:guid}/second-chance/me")]
    [ProducesResponseType(typeof(MySecondChanceOfferDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMySecondChanceOffer([FromRoute] Guid id, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can view second-chance offers.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.GetMySecondChanceOfferAsync(id, userId, ct);
        return Ok(result);
    }

    /// <summary>Seller lists their own auctions (newest start time first) with an optional status filter.</summary>
    [HttpGet("~/api/v1/seller/auctions")]
    [ProducesResponseType(typeof(PagedData<AuctionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSellerAuctions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.EnsureRole("seller", out var roleError)) return roleError;

        var result = await _auctionService.ListSellerAuctionsAsync(userId, page, pageSize, status, ct);
        return Ok(result);
    }

    /// <summary>Gets successful and failed auction statistics for a seller profile.</summary>
    [HttpGet("~/api/v1/sellers/{sellerId:guid}/auction-statistics")]
    [ProducesResponseType(typeof(SellerAuctionStatisticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSellerAuctionStatistics(
        [FromRoute] Guid sellerId, CancellationToken ct)
    {
        if (sellerId == Guid.Empty)
            return this.BadRequestError("VAL_INVALID_INPUT", "Seller ID is required.");

        var result = await _auctionService.GetSellerAuctionStatisticsAsync(sellerId, ct);
        return Ok(result);
    }

    /// <summary>Bidder được mời chấp nhận trở thành người thắng thay người thắng trước. Chỉ đúng bidder được mời mới gọi được.</summary>
    [HttpPost("{auctionId:guid}/winner-offers/{offerId:guid}/accept")]
    [ProducesResponseType(typeof(WinnerOfferResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptWinnerOffer(
        [FromRoute] Guid auctionId, [FromRoute] Guid offerId, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can respond to winner offers.");

        var result = await _auctionService.AcceptWinnerOfferAsync(CreateActor(userId), auctionId, offerId, ct);
        return Ok(result);
    }

    /// <summary>Bidder được mời từ chối lời mời. Phiên chuyển sang Failed. Body là tuỳ chọn (lý do).</summary>
    [HttpPost("{auctionId:guid}/winner-offers/{offerId:guid}/decline")]
    [ProducesResponseType(typeof(WinnerOfferResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineWinnerOffer(
        [FromRoute] Guid auctionId, [FromRoute] Guid offerId,
        [FromBody] DeclineWinnerOfferRequest? request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can respond to winner offers.");

        var result = await _auctionService.DeclineWinnerOfferAsync(
            CreateActor(userId), auctionId, offerId, request ?? new DeclineWinnerOfferRequest(null), ct);
        return Ok(result);
    }

    /// <summary>Người về nhì chấp nhận mua theo giá bid của chính mình sau khi người thắng trước không thanh toán (API 07).</summary>
    [HttpPost("{id:guid}/second-chance/accept")]
    [ProducesResponseType(typeof(SecondChanceAcceptResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptSecondChance(
        [FromRoute] Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] AcceptSecondChanceRequest request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can respond to second-chance offers.");

        var result = await _auctionService.AcceptSecondChanceOfferAsync(
            CreateActor(userId), id, idempotencyKey, request, ct);
        return Ok(result);
    }

    /// <summary>Người về nhì từ chối lời mời mua lại (API 08). Phiên chuyển sang Failed, không tính vi phạm Streak.</summary>
    [HttpPost("{id:guid}/second-chance/decline")]
    [ProducesResponseType(typeof(SecondChanceDeclineResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineSecondChance(
        [FromRoute] Guid id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] DeclineWinnerOfferRequest? request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can respond to second-chance offers.");

        var result = await _auctionService.DeclineSecondChanceOfferAsync(
            CreateActor(userId), id, idempotencyKey, request ?? new DeclineWinnerOfferRequest(null), ct);
        return Ok(result);
    }

    /// <summary>Bidder lấy các lời mời trở thành người thắng của mình (mới nhất trước), lọc theo trạng thái lời mời.</summary>
    [HttpGet("~/api/v1/me/auction-offers")]
    [ProducesResponseType(typeof(PagedData<WinnerOfferDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyWinnerOffers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can list winner offers.");

        var result = await _auctionService.ListMyWinnerOffersAsync(userId, page, pageSize, status, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff lists all auctions with operational filters.</summary>
    [HttpGet("~/api/v1/admin/auctions")]
    [ProducesResponseType(typeof(PagedData<AdminAuctionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAdminAuctions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] Guid? productId = null,
        [FromQuery] bool? hasAttentionDeposit = null,
        [FromQuery] bool? overdueSettlement = null,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListAdminAuctionsAsync(
            page, pageSize, status, sellerId, productId, hasAttentionDeposit, overdueSettlement, fromDate, toDate, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff lists auctions that require attention (stuck deposits or overdue settlements).</summary>
    [HttpGet("~/api/v1/admin/auctions/attention-required")]
    [ProducesResponseType(typeof(PagedData<AdminAuctionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAttentionRequired(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListAttentionRequiredAuctionsAsync(page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff manually retries background Catalog/Commerce/User sync for a stuck auction.</summary>
    [HttpPost("~/api/v1/admin/auctions/{id:guid}/sync/retry")]
    [ProducesResponseType(typeof(AdminSyncRetryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RetrySync([FromRoute] Guid id, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.RetrySyncAsync(CreateActor(userId), id, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff invalidates a fraudulent bid in a running auction and recalculates the leading price.</summary>
    [HttpPost("~/api/v1/admin/auctions/{auctionId:guid}/bids/{bidId:guid}/invalidate")]
    [ProducesResponseType(typeof(InvalidateBidResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> InvalidateBid(
        [FromRoute] Guid auctionId,
        [FromRoute] Guid bidId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] InvalidateBidRequest? request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.InvalidateBidAsync(CreateActor(userId), auctionId, bidId, request, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff force-cancels an auction that has not ended. Reason is required.</summary>
    [HttpPost("~/api/v1/admin/auctions/{id:guid}/force-cancel")]
    [ProducesResponseType(typeof(AuctionCancelledResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ForceCancel(
        [FromRoute] Guid id, [FromBody] ForceCancelAuctionRequest request, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ForceCancelAsync(CreateActor(userId), id, request, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff views the post-auction settlement saga of an auction.</summary>
    [HttpGet("~/api/v1/admin/auctions/{id:guid}/saga")]
    [ProducesResponseType(typeof(AuctionSagaDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSaga([FromRoute] Guid id, CancellationToken ct)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.GetSagaAsync(id, ct);
        return Ok(result);
    }

    /// <summary>Buyer adds an auction to watchlist or toggles new-bid notifications.</summary>
    [HttpPost("{id:guid}/watch")]
    [ProducesResponseType(typeof(WatchAuctionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Watch(
        [FromRoute] Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WatchAuctionRequest? request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can watch auctions.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.WatchAuctionAsync(CreateActor(userId), id, request, ct);
        return Ok(result);
    }

    /// <summary>Buyer removes an auction from watchlist.</summary>
    [HttpDelete("{id:guid}/watch")]
    [ProducesResponseType(typeof(WatchAuctionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unwatch([FromRoute] Guid id, CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can watch auctions.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.UnwatchAuctionAsync(CreateActor(userId), id, ct);
        return Ok(result);
    }

    /// <summary>Buyer lists their watched auctions.</summary>
    [HttpGet("~/api/v1/me/watchlist")]
    [ProducesResponseType(typeof(PagedData<MyWatchlistItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyWatchlist(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can view watchlist.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListMyWatchlistAsync(userId, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Buyer reports a suspicious scheduled or running auction to admin/staff.</summary>
    [HttpPost("{id:guid}/reports")]
    [ProducesResponseType(typeof(CreateAuctionReportResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateReport(
        [FromRoute] Guid id,
        [FromBody] CreateAuctionReportRequest request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can report auctions.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.CreateReportAsync(CreateActor(userId), id, request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Buyer views reports they submitted and admin responses.</summary>
    [HttpGet("~/api/v1/me/reports")]
    [ProducesResponseType(typeof(PagedData<MyAuctionReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyReports(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("buyer") && !this.HasRole("seller"))
            return this.ForbiddenError("Only buyer or seller accounts can view reports.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListMyReportsAsync(userId, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff lists auction reports across the platform.</summary>
    [HttpGet("~/api/v1/admin/auction-reports")]
    [ProducesResponseType(typeof(PagedData<AdminAuctionReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAdminReports(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] Guid? auctionId = null,
        CancellationToken ct = default)
    {
        if (!this.TryGetUserId(out _, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.ListAdminReportsAsync(page, pageSize, status, auctionId, ct);
        return Ok(result);
    }

    /// <summary>Admin or Staff dismisses pending reports on an auction when no violation is found and notifies reporters.</summary>
    [HttpPost("~/api/v1/admin/auctions/{id:guid}/reports/dismiss")]
    [ProducesResponseType(typeof(DismissAuctionReportsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DismissReports(
        [FromRoute] Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DismissAuctionReportsRequest? request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var errorResult)) return errorResult;
        if (!this.HasRole("admin") && !this.HasRole("staff"))
            return this.ForbiddenError("Only admin or staff accounts can access this resource.", "ROLE_NOT_ALLOWED");

        var result = await _auctionService.DismissReportsAsync(CreateActor(userId), id, request, ct);
        return Ok(result);
    }

    private ActorContext CreateActor(Guid userId) => new(userId, this.GetTraceId(), this.GetClientIp());
}