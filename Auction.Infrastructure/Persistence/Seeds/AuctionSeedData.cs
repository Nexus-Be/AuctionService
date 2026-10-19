namespace Auction.Infrastructure.Persistence.Seeds;

using Microsoft.EntityFrameworkCore;
using Auction.Domain.Entities;

public static class AuctionSeedData
{
    public static void SeedAuctionConfigs(ModelBuilder modelBuilder)
    {
        var configId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        modelBuilder.Entity<AuctionConfig>().HasData(
            new AuctionConfig
            {
                Id = configId,
                ConfigKey = "DEFAULT_AUCTION_POLICY",
                ConfigValue = "{\"antiSnipingExtensionMinutes\": 5, \"maxExtensionCount\": 10, \"minReputationToBid\": 40, \"minReputationToSell\": 50}",
                Description = "Chính sách tham gia và chống bắn tỉa mặc định toàn sàn",
                Version = 1,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDeleted = false
            }
        );
    }
}
