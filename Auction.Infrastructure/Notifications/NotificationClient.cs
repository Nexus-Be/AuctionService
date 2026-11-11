namespace Auction.Infrastructure.Notifications;

using System;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Auction.Application.Interfaces;

/// <summary>
/// Cấu hình gửi thông báo sang Notification service.
/// Enabled (Notification:Enabled, mặc định tắt), BaseUrl (Notification:BaseUrl, mặc định http://localhost:8007),
/// ServiceUserId (Notification:ServiceUserId) là mã gửi trong header x-user-id vì Notification yêu cầu header này cùng role "service",
/// DevWinnerEmail (Notification:DevWinnerEmail) chỉ để test: có giá trị thì mọi email người thắng gửi tới địa chỉ này và bỏ qua việc tra User service.
/// </summary>
public sealed record NotificationOptions(bool Enabled, string BaseUrl, Guid ServiceUserId, string? DevWinnerEmail)
{
    /// <summary>Số lần thử tối đa cho mỗi thông báo khi Notification lỗi tạm thời (5xx, hết giờ chờ, mất kết nối).</summary>
    public const int MaxAttempts = 3;

    public static readonly Guid DefaultServiceUserId = new("00000000-0000-0000-0000-0000000000a1");

    public static NotificationOptions FromConfiguration(IConfiguration configuration)
    {
        var enabled = bool.TryParse(configuration["Notification:Enabled"], out var parsedEnabled) && parsedEnabled;
        var baseUrl = configuration["Notification:BaseUrl"];
        var serviceUser = Guid.TryParse(configuration["Notification:ServiceUserId"], out var parsedId) && parsedId != Guid.Empty
            ? parsedId
            : DefaultServiceUserId;
        var devEmail = configuration["Notification:DevWinnerEmail"];

        return new NotificationOptions(
            enabled,
            string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8007" : baseUrl.Trim(),
            serviceUser,
            string.IsNullOrWhiteSpace(devEmail) ? null : devEmail.Trim());
    }
}

/// <summary>Một thông báo đang chờ gửi trong hàng đợi.</summary>
public abstract record NotificationWorkItem;

public sealed record BidOutbidWorkItem(BidOutbidNotification Notification) : NotificationWorkItem;

public sealed record AuctionWinnerWorkItem(AuctionWinnerNotification Notification) : NotificationWorkItem;

/// <summary>
/// Hàng đợi trong bộ nhớ cho thông báo gửi sang Notification service. Hàm Notify* trả về ngay và không bao giờ ném lỗi.
/// Tắt (Notification:Enabled=false) thì bỏ qua thông báo. Hàng đợi đầy thì bỏ thông báo mới và ghi log.
/// Vì không có outbox, thông báo còn trong hàng đợi sẽ mất nếu service khởi động lại.
/// </summary>
public sealed class NotificationClient : INotificationClient
{
    private const int Capacity = 1000;

    private readonly Channel<NotificationWorkItem> _queue = Channel.CreateBounded<NotificationWorkItem>(
        new BoundedChannelOptions(Capacity) { SingleReader = true });
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationClient> _logger;

    public NotificationClient(NotificationOptions options, ILogger<NotificationClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Phía đọc của hàng đợi, chỉ dùng cho NotificationDispatcher.</summary>
    public ChannelReader<NotificationWorkItem> Reader => _queue.Reader;

    public void NotifyBidOutbid(BidOutbidNotification notification) =>
        Enqueue(new BidOutbidWorkItem(notification), "bid outbid", notification.AuctionId);

    public void NotifyAuctionWinner(AuctionWinnerNotification notification) =>
        Enqueue(new AuctionWinnerWorkItem(notification), "auction winner", notification.AuctionId);

    private void Enqueue(NotificationWorkItem item, string kind, Guid auctionId)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Notifications are disabled: the {Kind} notification of auction {AuctionId} was skipped.", kind, auctionId);
            return;
        }

        try
        {
            if (!_queue.Writer.TryWrite(item))
                _logger.LogWarning("The notification queue is full: the {Kind} notification of auction {AuctionId} was dropped.", kind, auctionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the {Kind} notification of auction {AuctionId}.", kind, auctionId);
        }
    }
}
