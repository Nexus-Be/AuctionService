namespace Auction.Api.Hubs;

using Auction.Application.Interfaces;
using Auction.Application.Services.Auction;
using Auction.Application.Services.Bid;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

/// <summary>
/// Đẩy sự kiện của phiên đấu giá tới những người đang xem phiên đó qua SignalR. Lỗi khi đẩy chỉ được ghi
/// log rồi bỏ qua, vì dữ liệu đã được lưu và không được báo là thất bại.
/// </summary>
public sealed class SignalRAuctionNotifier : IAuctionNotifier
{
    private readonly IHubContext<AuctionHub> _hub;
    private readonly ILogger<SignalRAuctionNotifier> _logger;

    public SignalRAuctionNotifier(IHubContext<AuctionHub> hub, ILogger<SignalRAuctionNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Gửi sự kiện BidPlaced tới nhóm của phiên.</summary>
    public Task BidPlacedAsync(Guid auctionId, BidPlacedMessage message, CancellationToken cancellationToken = default) =>
        SendToAuctionAsync(auctionId, AuctionHub.BidPlacedEvent, message, cancellationToken);

    /// <summary>Gửi sự kiện AuctionCancelled tới nhóm của phiên.</summary>
    public Task AuctionCancelledAsync(Guid auctionId, AuctionCancelledMessage message, CancellationToken cancellationToken = default) =>
        SendToAuctionAsync(auctionId, AuctionHub.AuctionCancelledEvent, message, cancellationToken);

    /// <summary>Gửi sự kiện AuctionStatusChanged tới nhóm của phiên.</summary>
    public Task AuctionStatusChangedAsync(Guid auctionId, AuctionStatusChangedMessage message, CancellationToken cancellationToken = default) =>
        SendToAuctionAsync(auctionId, AuctionHub.AuctionStatusChangedEvent, message, cancellationToken);

    /// <summary>Gửi một sự kiện tới nhóm của phiên. Mọi lỗi được ghi log và nuốt để không ảnh hưởng thao tác chính.</summary>
    private async Task SendToAuctionAsync(Guid auctionId, string eventName, object message, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients
                .Group(AuctionHub.GroupName(auctionId))
                .SendAsync(eventName, message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push {EventName} for auction {AuctionId}", eventName, auctionId);
        }
    }
}