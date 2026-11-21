namespace Auction.Application.Services.Audit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Interfaces;

/// <summary>One audit record handed to the central log service.</summary>
public sealed record AuditSyncItem(
    Guid Id,
    string ServiceName,
    string? TraceId,
    string? UserId,
    string? UserName,
    string Action,
    string EntityName,
    string EntityId,
    string? OldValues,
    string? NewValues,
    string? ChangedColumns,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset CreatedAt);

public sealed record AuditSyncPage(IReadOnlyList<AuditSyncItem> Items, int UnsyncedCount);

public sealed record MarkAuditSyncedRequest(IReadOnlyList<Guid> Ids);

public sealed record MarkAuditSyncedResponse(int Updated);

public interface IAuditSyncSourceService
{
    Task<AuditSyncPage> GetUnsyncedAsync(int limit, CancellationToken ct = default);

    Task<MarkAuditSyncedResponse> MarkSyncedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
}

public sealed class AuditSyncSourceService : IAuditSyncSourceService
{
    public const int MaxLimit = 500;
    private readonly IAuctionDbContext _db;

    public AuditSyncSourceService(IAuctionDbContext db)
    {
        _db = db;
    }

    public async Task<AuditSyncPage> GetUnsyncedAsync(int limit, CancellationToken ct = default)
    {
        limit = limit is > 0 and <= MaxLimit ? limit : 200;

        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => !a.IsSynced)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Take(limit)
            .ToListAsync(ct);

        var unsynced = await _db.AuditLogs.CountAsync(a => !a.IsSynced, ct);

        var items = rows.Select(a => new AuditSyncItem(
            a.Id,
            a.ServiceName,
            a.TraceId,
            a.UserId,
            a.UserName,
            a.Action.ToString(),
            a.EntityName,
            a.EntityId,
            a.OldValues,
            a.NewValues,
            a.ChangedColumns,
            a.IpAddress,
            a.UserAgent,
            a.CreatedAt)).ToList();

        return new AuditSyncPage(items, unsynced);
    }

    public async Task<MarkAuditSyncedResponse> MarkSyncedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids == null || ids.Count == 0)
        {
            return new MarkAuditSyncedResponse(0);
        }

        var distinct = ids.Distinct().Take(MaxLimit).ToList();
        var rows = await _db.AuditLogs.Where(a => distinct.Contains(a.Id) && !a.IsSynced).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.IsSynced = true;
        }

        await _db.SaveChangesAsync(ct);
        return new MarkAuditSyncedResponse(rows.Count);
    }
}
