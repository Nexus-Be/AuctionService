namespace Auction.Api.Controllers.Internal;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using global::Auction.Application.Interfaces;

/// <summary>Read-only deposit data for the wallet service's reconciliation. It never changes anything.</summary>
[ApiController]
[Route("api/v1/internal/reconciliation")]
public sealed class InternalReconciliationController : ControllerBase
{
    private readonly IAuctionDbContext _db;
    private readonly IConfiguration _configuration;

    public InternalReconciliationController(IAuctionDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public sealed record DepositRecon(Guid DepositId, Guid AuctionId, Guid BidderId, decimal Amount, string Status, string? HoldId, DateTimeOffset UpdatedAt);

    public sealed record DepositReconPage(DepositRecon[] Items, Guid? NextCursor);

    /// <summary>Lists deposits ordered by id, starting after the cursor.</summary>
    [HttpGet("deposits")]
    public async Task<IActionResult> GetDeposits(
        [FromQuery] DateTimeOffset? updatedFrom = null,
        [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (Denied() is { } denied) return denied;
        limit = limit is > 0 and <= 500 ? limit : 200;

        var query = _db.AuctionDeposits.AsNoTracking().Where(d => !d.IsDeleted);
        if (updatedFrom.HasValue) query = query.Where(d => d.UpdatedAt >= updatedFrom.Value);
        if (cursor.HasValue) query = query.Where(d => d.Id.CompareTo(cursor.Value) > 0);

        var rows = await query.OrderBy(d => d.Id).Take(limit).ToListAsync(ct);
        var items = rows.Select(d => new DepositRecon(d.Id, d.AuctionId, d.BidderId, d.Amount, d.Status.ToString(), d.GatewayReference, d.UpdatedAt)).ToArray();
        var next = items.Length == limit ? items[^1].DepositId : (Guid?)null;
        return Ok(new DepositReconPage(items, next));
    }

    private IActionResult? Denied()
    {
        var expected = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_SECRET")
            ?? _configuration["INTERNAL_SERVICE_SECRET"]
            ?? _configuration["InternalSecurity:ServiceKey"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return StatusCode(500, new { errorCode = "INTERNAL_CONFIGURATION_ERROR", message = "Internal service authentication is not configured." });
        }

        var provided = Request.Headers["X-Internal-Service-Key"].ToString();
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length == 0 || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return StatusCode(403, new { errorCode = "FORBIDDEN", message = "Access denied to internal service resource." });
        }

        return null;
    }
}
