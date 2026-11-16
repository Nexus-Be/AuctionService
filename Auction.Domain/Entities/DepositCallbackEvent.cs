using Auction.Domain.Base;

namespace Auction.Domain.Entities;

/// <summary>Hộp thư đến (Inbox) của callback Wallet: mỗi EventId chỉ được xử lý một lần.</summary>
public class DepositCallbackEvent : BaseEntity<Guid>
{
    public required string EventId { get; set; }
    public required Guid DepositId { get; set; }
    public string? Result { get; set; }

    /// <summary>False khi callback là bản trùng, cũ hoặc không đổi được trạng thái nào.</summary>
    public bool Applied { get; set; }
    public required DateTimeOffset ProcessedAt { get; set; }

    public AuctionDeposit? Deposit { get; set; }
}
