namespace Auction.Infrastructure.Clients;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Auction.Application.Interfaces;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Cấu hình gọi Commerce: Commerce:Enabled (mặc định tắt), Commerce:BaseUrl (mặc định http://localhost:8003), Commerce:TimeoutSeconds (mặc định 10).</summary>
public sealed record CommerceOptions(bool Enabled, string BaseUrl, int TimeoutSeconds)
{
    public static CommerceOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = bool.TryParse(configuration["Commerce:Enabled"], out var parsedEnabled) && parsedEnabled;
        var baseUrl = configuration["Commerce:BaseUrl"];
        var timeout = int.TryParse(configuration["Commerce:TimeoutSeconds"], out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 10;

        return new CommerceOptions(
            enabled,
            string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8003" : baseUrl.Trim(),
            timeout);
    }
}

/// <summary>Một settlement đang chờ đẩy sang Commerce.</summary>
public sealed record CommerceSyncItem(Guid AuctionId);

/// <summary>
/// Hàng đợi trong bộ nhớ cho việc đẩy settlement sang Commerce. PushSettlement trả về ngay và không bao giờ ném lỗi.
/// Tắt (Commerce:Enabled=false) thì bỏ qua; hàng đợi đầy thì bỏ việc mới và ghi log mức Error. Việc còn trong hàng đợi sẽ mất nếu service khởi động lại,
/// khi đó Commerce vẫn lấy được settlement qua GET /api/v1/auction-settlements.
/// </summary>
public sealed class CommerceSyncQueue : ICommerceSync
{
    private const int Capacity = 1000;

    private readonly Channel<CommerceSyncItem> _queue = Channel.CreateBounded<CommerceSyncItem>(
        new BoundedChannelOptions(Capacity) { SingleReader = true });
    private readonly CommerceOptions _options;
    private readonly ILogger<CommerceSyncQueue> _logger;

    public CommerceSyncQueue(CommerceOptions options, ILogger<CommerceSyncQueue> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Phía đọc của hàng đợi, chỉ dùng cho CommerceSyncWorker.</summary>
    public ChannelReader<CommerceSyncItem> Reader => _queue.Reader;

    public void PushSettlement(Guid auctionId)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Commerce:Enabled is false: the settlement of auction {AuctionId} was not pushed. Commerce can read it from the settlement list.", auctionId);
            return;
        }

        try
        {
            if (!_queue.Writer.TryWrite(new CommerceSyncItem(auctionId)))
            {
                _logger.LogError(
                    "The Commerce sync queue is full: the settlement of auction {AuctionId} was dropped. Commerce can still read it from the settlement list.", auctionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the settlement of auction {AuctionId} for Commerce.", auctionId);
        }
    }
}

/// <summary>
/// Đọc hàng đợi và gửi settlement sang Commerce ở nền, tối đa MaxParallel việc cùng lúc. Dữ liệu được đọc lại từ DB ngay lúc gửi nên luôn mới nhất.
/// Lỗi tạm thời của Commerce (503) thử lại với thời gian chờ tăng dần (2, 4, 8, 16, 32, 64, 120 giây, tối đa MaxAttempts lần);
/// Commerce từ chối (502) thì bỏ và ghi log Error. Gửi xong thì lưu mã đơn Commerce trả về vào settlement.
/// </summary>
public sealed class CommerceSyncWorker : BackgroundService
{
    private const int MaxAttempts = 8;
    private const int MaxParallel = 4;
    private const int MaxProductNameLength = 255;

    private readonly CommerceSyncQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CommerceSyncWorker> _logger;

    public CommerceSyncWorker(CommerceSyncQueue queue, IServiceScopeFactory scopes, ILogger<CommerceSyncWorker> logger)
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
                        _logger.LogError(ex, "Pushing the settlement of auction {AuctionId} to Commerce failed.", item.AuctionId);
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

    private async Task ProcessAsync(CommerceSyncItem item, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IAuctionDbContext>();

                var auction = await db.Auctions
                    .Include(a => a.AuctionResult)
                    .Include(a => a.AuctionSettlement)
                    .FirstOrDefaultAsync(a => a.Id == item.AuctionId && !a.IsDeleted, cancellationToken);

                var settlement = auction?.AuctionSettlement;
                var result = auction?.AuctionResult;
                if (auction is null || settlement is null || result is null
                    || result.WinnerId is not { } winnerId
                    || result.WinningBidId is not { } winningBidId
                    || result.FinalPriceAmount is not { } winningAmount
                    || settlement.PaymentDeadlineAt is not { } paymentDeadline
                    || auction.SkuId is not { } skuId)
                {
                    _logger.LogWarning(
                        "The settlement of auction {AuctionId} was not pushed to Commerce: the winner data is incomplete.", item.AuctionId);
                    return;
                }

                // Đã có đơn (đã gửi trước đó) hoặc settlement không còn chờ thanh toán: không gửi nữa.
                if (settlement.Status != AuctionSettlementStatus.PendingPayment || settlement.OrderId is not null)
                {
                    _logger.LogInformation(
                        "The settlement of auction {AuctionId} needs no push to Commerce (status {Status}, order {OrderId}).",
                        item.AuctionId, settlement.Status, settlement.OrderId);
                    return;
                }

                var imageUrl = await ReadImageUrlAsync(scope, skuId, cancellationToken);
                var title = auction.SnapshotProductTitle?.Trim();
                var productName = string.IsNullOrEmpty(title)
                    ? "Auction product"
                    : title.Length > MaxProductNameLength ? title[..MaxProductNameLength] : title;

                var payload = new CommerceSettlementPayload(
                    auction.Id, winningBidId, winnerId, auction.SellerId, auction.ProductId, skuId,
                    productName, imageUrl, winningAmount, paymentDeadline);

                var commerce = scope.ServiceProvider.GetRequiredService<ICommerceClient>();
                var orderId = await commerce.SendSettlementAsync(payload, cancellationToken);

                if (orderId is { } createdOrderId)
                {
                    settlement.OrderId = createdOrderId;
                    settlement.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                _logger.LogInformation(
                    "The settlement of auction {AuctionId} was pushed to Commerce (order {OrderId}).", item.AuctionId, orderId);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DomainException ex) when (ex.StatusCode == 503)
            {
                _logger.LogWarning(
                    "Pushing the settlement of auction {AuctionId} to Commerce failed, attempt {Attempt} of {Max}: {Message}",
                    item.AuctionId, attempt, MaxAttempts, ex.Message);
            }
            catch (DomainException ex)
            {
                _logger.LogError(
                    "Commerce rejected the settlement of auction {AuctionId} and it will not be retried: {Message}", item.AuctionId, ex.Message);
                return;
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(120, 2 * Math.Pow(2, attempt - 1))), cancellationToken);
        }

        _logger.LogError(
            "The settlement of auction {AuctionId} was dropped after {Max} attempts. Commerce can still read it from the settlement list.",
            item.AuctionId, MaxAttempts);
    }

    /// <summary>Ảnh chính của SKU lấy từ Catalog; không lấy được thì bỏ trường ảnh, không làm hỏng việc gửi settlement.</summary>
    private async Task<string?> ReadImageUrlAsync(IServiceScope scope, Guid skuId, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogClient>();
            var variants = await catalog.GetVariantsAsync(new[] { skuId }, cancellationToken);
            return variants.FirstOrDefault(v => v.VariantId == skuId)?.PrimaryImageUrl;
        }
        catch (DomainException ex)
        {
            _logger.LogWarning("The product image for SKU {SkuId} could not be read from Catalog: {Message}", skuId, ex.Message);
            return null;
        }
    }
}
