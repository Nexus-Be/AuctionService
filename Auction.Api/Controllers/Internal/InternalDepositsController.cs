namespace Auction.Api.Controllers.Internal;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Common;
using Auction.Application.Services.Deposit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[RequireInternalServiceKey]
[Route("api/v1/internal/deposits")]
public class InternalDepositsController : ControllerBase
{
    private readonly IDepositService _depositService;

    public InternalDepositsController(IDepositService depositService)
    {
        _depositService = depositService;
    }

    /// <summary>Receives asynchronous authorization result callbacks from WalletService.</summary>
    [HttpPost("{depositId:guid}/authorization-result")]
    [ProducesResponseType(typeof(DepositAuthorizationResultResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DepositAuthorizationResultResponse>> ApplyAuthorizationResult(
        [FromRoute] Guid depositId, [FromBody] DepositAuthorizationResultRequest request, CancellationToken ct)
    {
        var result = await _depositService.ApplyAuthorizationResultAsync(depositId, request, ct);
        return Ok(result);
    }
}
