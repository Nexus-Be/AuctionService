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

        var auctionProductId = Guid.Parse("44000000-0000-0000-0000-000000000004"); // Product 4: Bike Helmet
        var auctionSkuId = Guid.Parse("44100000-0000-0000-0000-000000000004");     // NX-HELMET-M
        var auctionId = Guid.Parse("aa000000-0000-0000-0000-000000000001");
        var settledAuctionId = Guid.Parse("aa000000-0000-0000-0000-000000000002");

        var bid1Id = Guid.Parse("aa300000-0000-0000-0000-000000000001");
        var bid2Id = Guid.Parse("aa300000-0000-0000-0000-000000000002");

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

        // 2. Auctions (1 Running, 1 Settled)
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
            },
            new Auction
            {
                Id = settledAuctionId,
                ProductId = auctionProductId,
                SkuId = auctionSkuId,
                SellerId = sellerUserId,
                SnapshotProductTitle = "Mũ Bảo Hiểm Xe Đạp Nexus Aero - Đỏ / Size M (Đợt 1)",
                StartPriceAmount = 450000m,
                BidStepAmount = 50000m,
                ScheduledStartAt = seedDate.AddDays(-10),
                OriginalEndAt = seedDate.AddDays(-3),
                MinReputationToBid = 40,
                MinReputationToSell = 50,
                AntiSnipingExtensionMinutes = 5,
                MaxExtensionCount = 10,
                CreatedBy = sellerUserId,
                CreatedAt = seedDate.AddDays(-10),
                UpdatedAt = seedDate.AddDays(-3),
                IsDeleted = false
            }
        );

        // 3. Auction States
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
            },
            new AuctionState
            {
                Id = Guid.Parse("aa100000-0000-0000-0000-000000000002"),
                AuctionId = settledAuctionId,
                Status = AuctionStatus.Settled,
                CurrentPriceAmount = 550000m,
                CurrentWinnerId = buyerUserId,
                BidCount = 1,
                ExtensionCount = 0,
                ScheduledEndAt = seedDate.AddDays(-3),
                StartedAt = seedDate.AddDays(-10),
                RowVersion = 1,
                CreatedAt = seedDate.AddDays(-10),
                UpdatedAt = seedDate.AddDays(-3),
                IsDeleted = false
            }
        );

        // 4. Product Open Auction (Only for currently running auction)
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

        // 5. Bids
        modelBuilder.Entity<Bid>().HasData(
            new Bid
            {
                Id = bid1Id,
                AuctionId = auctionId,
                BidSeq = 1,
                BidderId = buyerUserId,
                Amount = 600000m,
                PlacedAt = seedDate.AddHours(2),
                CreatedAt = seedDate.AddHours(2),
                UpdatedAt = seedDate.AddHours(2),
                IsDeleted = false
            },
            new Bid
            {
                Id = bid2Id,
                AuctionId = settledAuctionId,
                BidSeq = 1,
                BidderId = buyerUserId,
                Amount = 550000m,
                PlacedAt = seedDate.AddDays(-5),
                CreatedAt = seedDate.AddDays(-5),
                UpdatedAt = seedDate.AddDays(-5),
                IsDeleted = false
            }
        );

        // 6. Bid Histories
        modelBuilder.Entity<BidHistory>().HasData(
            new BidHistory
            {
                Id = Guid.Parse("aa400000-0000-0000-0000-000000000001"),
                AuctionId = auctionId,
                BidId = bid1Id,
                SequenceNo = 1,
                BidderId = buyerUserId,
                DisplayName = "Nguyen Van Buyer",
                Amount = 600000m,
                PlacedAt = seedDate.AddHours(2),
                IsOutbid = false,
                IsWinning = true,
                IsInvalidated = false,
                CreatedAt = seedDate.AddHours(2),
                UpdatedAt = seedDate.AddHours(2),
                IsDeleted = false
            },
            new BidHistory
            {
                Id = Guid.Parse("aa400000-0000-0000-0000-000000000002"),
                AuctionId = settledAuctionId,
                BidId = bid2Id,
                SequenceNo = 1,
                BidderId = buyerUserId,
                DisplayName = "Nguyen Van Buyer",
                Amount = 550000m,
                PlacedAt = seedDate.AddDays(-5),
                IsOutbid = false,
                IsWinning = true,
                IsInvalidated = false,
                CreatedAt = seedDate.AddDays(-5),
                UpdatedAt = seedDate.AddDays(-5),
                IsDeleted = false
            }
        );

        // 7. Auction Status Histories
        modelBuilder.Entity<AuctionStatusHistory>().HasData(
            new AuctionStatusHistory
            {
                Id = Guid.Parse("aa500000-0000-0000-0000-000000000001"),
                AuctionId = auctionId,
                FromStatus = AuctionStatus.Scheduled,
                ToStatus = AuctionStatus.Running,
                Reason = "Phiên đấu giá bắt đầu tự động theo lịch",
                ChangedBy = sellerUserId,
                ChangedAt = seedDate,
                CreatedAt = seedDate,
                UpdatedAt = seedDate,
                IsDeleted = false
            },
            new AuctionStatusHistory
            {
                Id = Guid.Parse("aa500000-0000-0000-0000-000000000002"),
                AuctionId = settledAuctionId,
                FromStatus = AuctionStatus.Running,
                ToStatus = AuctionStatus.Ended,
                Reason = "Hết thời gian đấu giá",
                ChangedBy = sellerUserId,
                ChangedAt = seedDate.AddDays(-3),
                CreatedAt = seedDate.AddDays(-3),
                UpdatedAt = seedDate.AddDays(-3),
                IsDeleted = false
            },
            new AuctionStatusHistory
            {
                Id = Guid.Parse("aa500000-0000-0000-0000-000000000003"),
                AuctionId = settledAuctionId,
                FromStatus = AuctionStatus.Ended,
                ToStatus = AuctionStatus.Settled,
                Reason = "Người thắng đã hoàn tất thanh toán",
                ChangedBy = buyerUserId,
                ChangedAt = seedDate.AddDays(-2),
                CreatedAt = seedDate.AddDays(-2),
                UpdatedAt = seedDate.AddDays(-2),
                IsDeleted = false
            }
        );

        // 8. Auction Result (for settled auction)
        modelBuilder.Entity<AuctionResult>().HasData(
            new AuctionResult
            {
                Id = Guid.Parse("aa600000-0000-0000-0000-000000000001"),
                AuctionId = settledAuctionId,
                WinningBidId = bid2Id,
                WinnerId = buyerUserId,
                FinalPriceAmount = 550000m,
                DeterminedAt = seedDate.AddDays(-3),
                CreatedAt = seedDate.AddDays(-3),
                UpdatedAt = seedDate.AddDays(-3),
                IsDeleted = false
            }
        );

        // 9. Auction Settlement (for settled auction)
        modelBuilder.Entity<AuctionSettlement>().HasData(
            new AuctionSettlement
            {
                Id = Guid.Parse("aa700000-0000-0000-0000-000000000001"),
                AuctionId = settledAuctionId,
                OrderId = Guid.Parse("01000000-0000-0000-0000-000000000001"),
                PaymentDeadlineAt = seedDate.AddDays(-1),
                PaidAt = seedDate.AddDays(-2),
                CreatedAt = seedDate.AddDays(-3),
                UpdatedAt = seedDate.AddDays(-2),
                IsDeleted = false
            }
        );

        // 10. Audit Log
        modelBuilder.Entity<AuditLog>().HasData(
            new AuditLog
            {
                Id = Guid.Parse("aa800000-0000-0000-0000-000000000001"),
                ServiceName = "Auction",
                TraceId = "trace-auction-create-001",
                UserId = sellerUserId.ToString(),
                UserName = "seller@nexus.com",
                Action = AuditAction.AuctionCreate,
                EntityName = "Auction",
                EntityId = auctionId.ToString(),
                OldValues = null,
                NewValues = "{\"StartPriceAmount\":500000,\"BidStepAmount\":50000}",
                ChangedColumns = "[\"StartPriceAmount\",\"BidStepAmount\"]",
                IpAddress = "127.0.0.1",
                UserAgent = "NexusClient/1.0",
                CreatedAt = seedDate,
                IsSynced = true
            }
        );
    }
}
