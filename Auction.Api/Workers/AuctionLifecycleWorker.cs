namespace Auction.Api.Workers;

using Auction.Application.Services.AuctionLifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tiến trình nền định kỳ quét các phiên đấu giá và tự chuyển trạng thái theo thời gian:
/// Scheduled sang Running khi tới giờ bắt đầu, Running sang Ended hoặc Failed khi hết giờ kết thúc,
/// và đóng các lời mời mua thay (winner offer) quá hạn phản hồi (phiên Ended sang Failed).
/// Mỗi phiên được xử lý trong một scope riêng (DbContext riêng) nên lỗi hoặc xung đột ở một phiên không ảnh hưởng phiên khác.
/// Cấu hình: AuctionLifecycle:Enabled (mặc định true), AuctionLifecycle:IntervalSeconds (mặc định 5, từ 1 đến 300).
/// </summary>
public sealed class AuctionLifecycleWorker : BackgroundService
{
    private const int DefaultIntervalSeconds = 5;
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuctionLifecycleWorker> _logger;

    /// <summary>Khởi tạo worker với bộ tạo scope, cấu hình và logger.</summary>
    public AuctionLifecycleWorker(
        IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<AuctionLifecycleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Vòng lặp chính: chạy một lượt quét rồi chờ hết chu kỳ, lặp lại cho tới khi ứng dụng dừng. Tắt hẳn nếu Enabled = false.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("AuctionLifecycle:Enabled", true))
        {
            _logger.LogInformation("Auction lifecycle worker is disabled.");
            return;
        }

        var seconds = Math.Clamp(_configuration.GetValue("AuctionLifecycle:IntervalSeconds", DefaultIntervalSeconds), 1, 300);
        _logger.LogInformation("Auction lifecycle worker started. Interval: {Seconds}s.", seconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Ứng dụng đang dừng.
        }
    }

    /// <summary>Một lượt quét: bắt đầu các phiên tới giờ trước, rồi kết thúc các phiên hết giờ, cuối cùng đóng các lời mời quá hạn. Lỗi của cả lượt chỉ được ghi log để lượt sau chạy lại.</summary>
    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var toStart = await InScopeAsync(s => s.FindAuctionsToStartAsync(BatchSize, cancellationToken));
            foreach (var auctionId in toStart)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProcessAsync(auctionId, "start", s => s.StartAuctionAsync(auctionId, cancellationToken)))
                    _logger.LogInformation("Auction {AuctionId} started.", auctionId);
            }

            var toEnd = await InScopeAsync(s => s.FindAuctionsToEndAsync(BatchSize, cancellationToken));
            foreach (var auctionId in toEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProcessAsync(auctionId, "end", s => s.EndAuctionAsync(auctionId, cancellationToken)))
                    _logger.LogInformation("Auction {AuctionId} ended.", auctionId);
            }

            var offersToExpire = await InScopeAsync(s => s.FindWinnerOffersToExpireAsync(BatchSize, cancellationToken));
            foreach (var offerId in offersToExpire)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProcessAsync(offerId, "expire winner offer", s => s.ExpireWinnerOfferAsync(offerId, cancellationToken)))
                    _logger.LogInformation("Winner offer {OfferId} expired.", offerId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auction lifecycle scan failed. It will run again at the next interval.");
        }
    }

    /// <summary>Xử lý một phiên hoặc một lời mời trong scope riêng. Lỗi của mục này chỉ được ghi log và coi như chưa xử lý để không chặn các mục còn lại.</summary>
    private async Task<bool> ProcessAsync(Guid entityId, string action, Func<IAuctionLifecycleService, Task<bool>> work)
    {
        try
        {
            return await InScopeAsync(work);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not {Action} {EntityId}.", action, entityId);
            return false;
        }
    }

    /// <summary>Tạo một scope DI mới, lấy service chuyển trạng thái và chạy công việc trong scope đó.</summary>
    private async Task<T> InScopeAsync<T>(Func<IAuctionLifecycleService, Task<T>> work)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuctionLifecycleService>();
        return await work(service);
    }
}