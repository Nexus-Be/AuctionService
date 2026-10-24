namespace Auction.Infrastructure.Persistence.Seeds;

using Microsoft.EntityFrameworkCore;
using Auction.Domain.Entities;
using Auction.Domain.Enums;

public static class AuctionSeedData
{
    public static void SeedAll(ModelBuilder modelBuilder)
    {
        var seedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var sellerUserId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
        var buyerUserId = Guid.Parse("dddddddd-4444-4444-4444-444444444444");

        var auctionProductId = Guid.Parse("44000000-0000-0000-0000-000000000004"); // Product 4: Mũ Bảo Hiểm Xe Đạp
        var auctionSkuId = Guid.Parse("44100000-0000-0000-0000-000000000004");     // NX-HELMET-M
        var auctionId = Guid.Parse("aa000000-0000-0000-0000-000000000001");

        // 1. System Policy Config
        modelBuilder.Entity<AuctionConfig>().HasData(
            new AuctionConfig
            {
                Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                ConfigKey = "DEFAULT_AUCTION_POLICY",
                ConfigValue = "{\"antiSnipingExtensionMinutes\": 5, \"maxExtensionCount\": 10, \"minReputationToBid\": 40, \"minReputationToSell\": 50}",
                Description = "Chính sách tham gia và chống bắn tỉa mặc định toàn sàn",
                Version = 1,
                IsActive = true,
                CreatedAt = seedDate,
                UpdatedAt = seedDate,
                IsDeleted = false
            }
        );

        // 2. Running Auction for Helmet
        modelBuilder.Entity<Auction>().HasData(
            new Auction
            {
                Id = auctionId,
                ProductId = auctionProductId,
                SkuId = auctionSkuId,
                SellerId = sellerUserId,
                SnapshotProductTitle = "Mũ Bảo Hiểm Xe Đạp Nexus Aero - Đỏ / Size M",
                StartPriceAmount = 500000m,
                BidStepAmount = 50000m,
                ScheduledStartAt = seedDate,
                OriginalEndAt = seedDate.AddDays(7),
                MinReputationToBid = 40,
                MinReputationToSell = 50,
                AntiSnipingExtensionMinutes = 5,
                MaxExtensionCount = 10,
                CreatedBy = sellerUserId,
                CreatedAt = seedDate,
                UpdatedAt = seedDate,
                IsDeleted = false
            }
        );

        // 3. Auction State
        modelBuilder.Entity<AuctionState>().HasData(
            new AuctionState
            {
                Id = Guid.Parse("aa100000-0000-0000-0000-000000000001"),
                AuctionId = auctionId,
                Status = AuctionStatus.Running,
                CurrentPriceAmount = 600000m,
                CurrentWinnerId = buyerUserId,
                BidCount = 1,
                ExtensionCount = 0,
                ScheduledEndAt = seedDate.AddDays(7),
                StartedAt = seedDate,
                RowVersion = 1,
                CreatedAt = seedDate,
                UpdatedAt = seedDate,
                IsDeleted = false
            }
        );

        // 4. Product Open Auction
        modelBuilder.Entity<ProductOpenAuction>().HasData(
            new ProductOpenAuction
            {
                Id = Guid.Parse("aa200000-0000-0000-0000-000000000001"),
                ProductId = auctionProductId,
                AuctionId = auctionId,
                CreatedAt = seedDate,
                UpdatedAt = seedDate,
                IsDeleted = false
            }
        );

        // 5. Initial Bid placed by Buyer
        modelBuilder.Entity<Bid>().HasData(
            new Bid
            {
                Id = Guid.Parse("aa300000-0000-0000-0000-000000000001"),
                AuctionId = auctionId,
                BidSeq = 1,
                BidderId = buyerUserId,
                Amount = 600000m,
                PlacedAt = seedDate.AddHours(2),
                CreatedAt = seedDate.AddHours(2),
                UpdatedAt = seedDate.AddHours(2),
                IsDeleted = false
            }
        );
    }
}
