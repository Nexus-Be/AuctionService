namespace Auction.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Dữ liệu settlement gửi sang Commerce để tạo đơn cho người thắng hoặc người về nhì (POST /api/v1/auction-orders/settlement).</summary>
public sealed record CommerceSettlementPayload(
    Guid AuctionId,
    Guid WinningBidId,
    Guid WinnerId,
    Guid SellerId,
    Guid ProductId,
    Guid SkuId,
    string ProductName,
    string? ProductImageUrl,
    decimal WinningAmount,
    DateTimeOffset PaymentDeadlineAt,
    decimal DepositAmount = 0m,
    string Source = "WINNER",
    Guid? DepositId = null);

public interface ICommerceClient
{
    /// <summary>
    /// Gửi settlement sang Commerce. Trả mã đơn Commerce vừa tạo hoặc đã có (null nếu câu trả lời không có mã đơn, hoặc Commerce báo đã có đơn còn hiệu lực
    /// của người thắng khác). Ném 503 khi Commerce lỗi tạm thời (đáng thử lại) và 502 khi Commerce từ chối (thử lại cũng không đổi).
    /// </summary>
    Task<Guid?> SendSettlementAsync(CommerceSettlementPayload payload, CancellationToken cancellationToken = default);
}
