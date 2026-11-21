namespace Auction.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Services.Audit;
using global::Auction.Domain.Entities;
using Xunit;

public class AuditSyncSourceTests
{
    private static global::Auction.Infrastructure.Persistence.AppDbContext NewDb() => new(new DbContextOptionsBuilder<global::Auction.Infrastructure.Persistence.AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AuditLog Row(DateTimeOffset at, bool synced = false) => new()
    {
        Id = Guid.NewGuid(),
        ServiceName = "auction-service",
        Action = default,
        EntityName = "Thing",
        EntityId = "e1",
        CreatedAt = at,
        IsSynced = synced
    };

    [Fact]
    public async Task Unsynced_records_come_oldest_first_with_the_total_and_synced_ones_are_left_out()
    {
        var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var oldest = Row(now.AddMinutes(-3));
        var middle = Row(now.AddMinutes(-2));
        var newest = Row(now.AddMinutes(-1));
        db.AuditLogs.AddRange(newest, oldest, middle, Row(now.AddMinutes(-10), synced: true));
        await db.SaveChangesAsync();

        var page = await new AuditSyncSourceService(db).GetUnsyncedAsync(2);

        Assert.Equal(new[] { oldest.Id, middle.Id }, page.Items.Select(i => i.Id).ToArray());
        Assert.Equal(3, page.UnsyncedCount);
    }

    [Fact]
    public async Task Marking_flags_each_record_once_ignores_unknown_ids_and_is_safe_to_repeat()
    {
        var db = NewDb();
        var a = Row(DateTimeOffset.UtcNow.AddMinutes(-2));
        var b = Row(DateTimeOffset.UtcNow.AddMinutes(-1));
        db.AuditLogs.AddRange(a, b);
        await db.SaveChangesAsync();
        var service = new AuditSyncSourceService(db);

        var first = await service.MarkSyncedAsync(new[] { a.Id, a.Id, Guid.NewGuid() });
        var second = await service.MarkSyncedAsync(new[] { a.Id });
        var none = await service.MarkSyncedAsync(Array.Empty<Guid>());

        Assert.Equal(1, first.Updated);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, none.Updated);
        var page = await service.GetUnsyncedAsync(10);
        Assert.Equal(new[] { b.Id }, page.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task A_limit_outside_the_allowed_range_falls_back_to_the_default()
    {
        var db = NewDb();
        db.AuditLogs.Add(Row(DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var service = new AuditSyncSourceService(db);

        Assert.Single((await service.GetUnsyncedAsync(0)).Items);
        Assert.Single((await service.GetUnsyncedAsync(100000)).Items);
    }
}
