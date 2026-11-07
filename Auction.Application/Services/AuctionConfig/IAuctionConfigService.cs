namespace Auction.Application.Services.AuctionConfig;

using System.Threading;
using System.Threading.Tasks;
using global::Auction.Application.Common.Models;

public interface IAuctionConfigService
{
    /// <summary>Lấy danh sách cấu hình đang áp dụng (chỉ admin).</summary>
    Task<PagedData<AuctionConfigDto>> ListAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Lấy lịch sử các version của một cấu hình, mới nhất trước, kèm các trường thay đổi so với version liền trước (chỉ admin).</summary>
    Task<PagedData<AuctionConfigHistoryItemDto>> GetHistoryAsync(string configKey, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Phát hành version mới cho cấu hình chống bắn tỉa, version cũ được giữ lại làm lịch sử (chỉ admin).</summary>
    Task<AuctionConfigDto> UpdateAsync(ActorContext actor, UpdateAuctionConfigRequest request, CancellationToken cancellationToken = default);
}