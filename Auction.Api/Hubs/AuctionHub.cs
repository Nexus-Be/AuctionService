namespace Auction.Api.Hubs;

using Auction.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Kênh realtime cho người xem phiên đấu giá. Client vào nhóm của phiên đang xem (JoinAuction) và nhận các sự kiện
/// <see cref="BidPlacedEvent"/>, <see cref="AuctionCancelledEvent"/>, <see cref="AuctionStatusChangedEvent"/> do server đẩy xuống.
/// Hub chỉ để nhận tin: đặt giá luôn đi qua POST /api/v1/auctions/{auctionId}/bids, không đi qua hub.
/// Mỗi kết nối chỉ được vào tối đa <see cref="MaxJoinedAuctions"/> phiên và chỉ vào được phiên có tồn tại.
/// </summary>
public sealed class AuctionHub : Hub
{
    /// <summary>Đường dẫn của hub.</summary>
    public const string Route = "/hubs/auctions";

    /// <summary>Tên sự kiện khi có lượt đặt giá mới.</summary>
    public const string BidPlacedEvent = "BidPlaced";

    /// <summary>Tên sự kiện khi phiên bị huỷ.</summary>
    public const string AuctionCancelledEvent = "AuctionCancelled";

    /// <summary>Tên sự kiện khi trạng thái phiên đổi.</summary>
    public const string AuctionStatusChangedEvent = "AuctionStatusChanged";

    /// <summary>Số phiên tối đa một kết nối được theo dõi cùng lúc.</summary>
    public const int MaxJoinedAuctions = 20;

    private const string JoinedAuctionsKey = "joined-auctions";

    private readonly IAuctionDbContext _dbContext;

    /// <summary>Khởi tạo hub với DbContext để kiểm tra phiên có tồn tại khi client xin vào nhóm.</summary>
    public AuctionHub(IAuctionDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Tạo tên nhóm SignalR của một phiên, mỗi phiên một nhóm.</summary>
    public static string GroupName(Guid auctionId) => $"auction:{auctionId:N}";

    /// <summary>
    /// Đưa kết nối hiện tại vào nhóm của phiên để bắt đầu nhận sự kiện của phiên đó.
    /// Báo lỗi VAL_INVALID_IDENTIFIER nếu mã rỗng, AUCTION_NOT_FOUND nếu phiên không tồn tại,
    /// TOO_MANY_AUCTIONS nếu kết nối đã theo dõi đủ số phiên cho phép. Vào lại phiên đã vào thì bỏ qua.
    /// </summary>
    public async Task JoinAuction(Guid auctionId)
    {
        if (auctionId == Guid.Empty)
            throw new HubException("VAL_INVALID_IDENTIFIER");

        var joined = GetJoinedAuctions();
        if (joined.Contains(auctionId))
            return;

        if (joined.Count >= MaxJoinedAuctions)
            throw new HubException("TOO_MANY_AUCTIONS");

        var exists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, Context.ConnectionAborted);
        if (!exists)
            throw new HubException("AUCTION_NOT_FOUND");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(auctionId), Context.ConnectionAborted);
        joined.Add(auctionId);
    }

    /// <summary>Đưa kết nối hiện tại ra khỏi nhóm của phiên, không nhận sự kiện của phiên đó nữa.</summary>
    public async Task LeaveAuction(Guid auctionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(auctionId), Context.ConnectionAborted);
        GetJoinedAuctions().Remove(auctionId);
    }

    /// <summary>Lấy danh sách phiên mà kết nối hiện tại đã vào, lưu trong Context.Items (mỗi kết nối một danh sách).</summary>
    private HashSet<Guid> GetJoinedAuctions()
    {
        if (Context.Items.TryGetValue(JoinedAuctionsKey, out var value) && value is HashSet<Guid> existing)
            return existing;

        var created = new HashSet<Guid>();
        Context.Items[JoinedAuctionsKey] = created;
        return created;
    }
}