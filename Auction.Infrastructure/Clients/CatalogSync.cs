namespace Auction.Infrastructure.Clients;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Auction.Application.Interfaces;
using Auction.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Việc cần làm ở Catalog sau khi phiên thay đổi.</summary>
public enum CatalogSyncAction
{
    UnlockProduct,
    MarkAuctioned
}

/// <summary>Một việc đồng bộ đang chờ trong hàng đợi.</summary>
public sealed record CatalogSyncItem(CatalogSyncAction Action, Guid ProductId, Guid AuctionId);

/// <summary>
/// Hàng đợi trong bộ nhớ cho việc đồng bộ trạng thái sản phẩm sang Catalog. Hàm UnlockProduct và MarkProductAuctioned trả về ngay và không bao giờ ném lỗi.
/// Hàng đợi đầy thì bỏ việc mới và ghi log mức Error vì sản phẩm có thể còn bị khoá. Việc còn trong hàng đợi sẽ mất nếu service khởi động lại.
/// </summary>
public sealed class CatalogSyncQueue : ICatalogSync
{
    private const int Capacity = 1000;

    private readonly Channel<CatalogSyncItem> _queue = Channel.CreateBounded<CatalogSyncItem>(
        new BoundedChannelOptions(Capacity) { SingleReader = true });
    private readonly ILogger<CatalogSyncQueue> _logger;

    public CatalogSyncQueue(ILogger<CatalogSyncQueue> logger)
    {
        _logger = logger;
    }

    /// <summary>Phía đọc của hàng đợi, chỉ dùng cho CatalogSyncWorker.</summary>
    public ChannelReader<CatalogSyncItem> Reader => _queue.Reader;

    public void UnlockProduct(Guid productId, Guid auctionId) =>
        Enqueue(new CatalogSyncItem(CatalogSyncAction.UnlockProduct, productId, auctionId));

    public void MarkProductAuctioned(Guid productId, Guid auctionId) =>
        Enqueue(new CatalogSyncItem(CatalogSyncAction.MarkAuctioned, productId, auctionId));

    private void Enqueue(CatalogSyncItem item)
    {
        try
        {
            if (!_queue.Writer.TryWrite(item))
            {
                _logger.LogError(
                    "The Catalog sync queue is full: {Action} of product {ProductId} (auction {AuctionId}) was dropped. The product may stay locked.",
                    item.Action, item.ProductId, item.AuctionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue {Action} of product {ProductId} (auction {AuctionId}).", item.Action, item.ProductId, item.AuctionId);
        }
    }
}

/// <summary>
/// Đọc hàng đợi và gọi Catalog ở nền, tối đa MaxParallel việc cùng lúc. Lỗi tạm thời của Catalog (503) thử lại với thời gian chờ tăng dần
/// (2, 4, 8, 16, 32, 64, 120 giây, tối đa MaxAttempts lần); lỗi không đổi khi thử lại (502) thì bỏ và ghi log Error.
/// Trước khi mở khoá, kiểm tra sản phẩm chưa được phiên khác giữ (ví dụ seller đã tạo lại phiên) để không mở khoá nhầm khoá của phiên mới.
/// </summary>
public sealed class CatalogSyncWorker : BackgroundService
{
    private const int MaxAttempts = 8;
    private const int MaxParallel = 8;

    private readonly CatalogSyncQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CatalogSyncWorker> _logger;

    public CatalogSyncWorker(CatalogSyncQueue queue, IServiceScopeFactory scopes, ILogger<CatalogSyncWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var limiter = new SemaphoreSlim(MaxParallel);

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await limiter.WaitAsync(stoppingToken);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessAsync(item, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Service đang tắt.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Catalog sync {Action} of product {ProductId} (auction {AuctionId}) failed.", item.Action, item.ProductId, item.AuctionId);
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service đang tắt.
        }
    }

    private async Task ProcessAsync(CatalogSyncItem item, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var scope = _scopes.CreateScope();

                if (item.Action == CatalogSyncAction.UnlockProduct)
                {
                    var db = scope.ServiceProvider.GetRequiredService<IAuctionDbContext>();
                    var heldByAnotherAuction = await db.ProductOpenAuctions
                        .AsNoTracking()
                        .AnyAsync(p => p.ProductId == item.ProductId, cancellationToken);
                    if (heldByAnotherAuction)
                    {
                        _logger.LogInformation(
                            "Product {ProductId} is held by another auction now: the unlock of auction {AuctionId} was skipped.",
                            item.ProductId, item.AuctionId);
                        return;
                    }
                }

                var catalog = scope.ServiceProvider.GetRequiredService<ICatalogClient>();
                if (item.Action == CatalogSyncAction.UnlockProduct)
                    await catalog.UnlockProductFromAuctionAsync(item.ProductId, cancellationToken);
                else
                    await catalog.MarkProductAuctionedAsync(item.ProductId, cancellationToken);

                _logger.LogInformation(
                    "Catalog {Action} done for product {ProductId} (auction {AuctionId}).", item.Action, item.ProductId, item.AuctionId);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DomainException ex) when (ex.StatusCode == 503)
            {
                _logger.LogWarning(
                    "Catalog {Action} of product {ProductId} (auction {AuctionId}) failed, attempt {Attempt} of {Max}: {Message}",
                    item.Action, item.ProductId, item.AuctionId, attempt, MaxAttempts, ex.Message);
            }
            catch (DomainException ex)
            {
                _logger.LogError(
                    "Catalog {Action} of product {ProductId} (auction {AuctionId}) was rejected and will not be retried: {Message}",
                    item.Action, item.ProductId, item.AuctionId, ex.Message);
                return;
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(120, 2 * Math.Pow(2, attempt - 1))), cancellationToken);
        }

        _logger.LogError(
            "Catalog {Action} of product {ProductId} (auction {AuctionId}) was dropped after {Max} attempts. The product state in Catalog needs a manual check.",
            item.Action, item.ProductId, item.AuctionId, MaxAttempts);
    }
}
