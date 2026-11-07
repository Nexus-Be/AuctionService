namespace Auction.Domain.Enums;

/// <summary>Trạng thái lời mời mua dành cho bidder thay thế.</summary>
public enum AuctionWinnerOfferStatus
{
    /// <summary>Lời mời đang chờ bidder phản hồi.</summary>
    Pending = 0,
    /// <summary>Bidder đã chấp nhận lời mời.</summary>
    Accepted = 1,
    /// <summary>Bidder đã từ chối lời mời.</summary>
    Declined = 2,
    /// <summary>Lời mời đã hết hạn mà không được chấp nhận.</summary>
    Expired = 3
}
