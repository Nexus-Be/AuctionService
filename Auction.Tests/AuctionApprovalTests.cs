namespace Auction.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Auction.Application.Common.Models;
using Auction.Application.Interfaces;
using Auction.Application.Services.Auction;
using Auction.Application.Services.Deposit;
using Auction.Domain.Entities;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Auction.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

public class AuctionApprovalTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (AuctionService Service, AppDbContext Db, Mock<ICatalogSync> CatalogSync) CreateSut(DateTimeOffset now)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"auction-approval-{Guid.NewGuid()}")
            .Options;
        var db = new AppDbContext(options);

        var catalog = new Mock<ICatalogClient>();
        var users = new Mock<IUserClient>();
        var wallet = new Mock<IWalletClient>();
        var notifier = new Mock<IAuctionNotifier>();
        var notifications = new Mock<INotificationClient>();
        var catalogSync = new Mock<ICatalogSync>();
        var commerceSync = new Mock<ICommerceSync>();
        var deposits = new Mock<IDepositService>();

        var service = new AuctionService(
            db, catalog.Object, users.Object, wallet.Object, new FixedTimeProvider(now),
            notifier.Object, notifications.Object, catalogSync.Object, commerceSync.Object, deposits.Object);

        return (service, db, catalogSync);
    }

    private static async Task<Auction> SeedPendingAuctionAsync(AppDbContext db, Guid sellerId, DateTimeOffset startAt, DateTimeOffset endAt, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var auction = new Auction
        {
            Id = id,
            ProductId = productId,
            SkuId = Guid.NewGuid(),
            SellerId = sellerId,
            SnapshotProductTitle = "Vintage Watch",
            StartPriceAmount = 1_000_000m,
            BidStepAmount = 100_000m,
            ScheduledStartAt = startAt,
            OriginalEndAt = endAt,
            AntiSnipingExtensionMinutes = 5,
            MaxExtensionCount = 3,
            CreatedBy = sellerId,
            UpdatedBy = sellerId,
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddHours(-2),
            AuctionState = new AuctionState
            {
                Id = Guid.NewGuid(),
                AuctionId = id,
                Status = AuctionStatus.PendingApproval,
                ScheduledEndAt = endAt,
                RowVersion = 1,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-2)
            },
            ProductOpenAuction = new ProductOpenAuction
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                AuctionId = id,
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddHours(-2)
            }
        };

        db.Auctions.Add(auction);
        await db.SaveChangesAsync();
        return auction;
    }

    [Fact]
    public async Task PendingApproval_PastStartTime_CanBeRescheduledBySellerAndThenApproved()
    {
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var (service, db, _) = CreateSut(now);
        var sellerId = Guid.NewGuid();
        var adminActor = new ActorContext(Guid.NewGuid(), "trace-1", "127.0.0.1");
        var sellerActor = new ActorContext(sellerId, "trace-2", "127.0.0.1");

        // ScheduledStartAt is already in the past because admin reviewed late.
        var auction = await SeedPendingAuctionAsync(db, sellerId, now.AddMinutes(-10), now.AddHours(2), now);

        var approvePast = () => service.ApproveAuctionAsync(adminActor, auction.Id);
        (await approvePast.Should().ThrowAsync<DomainException>())
            .Which.ErrorCode.Should().Be("AUCTION_START_TIME_PASSED");

        // Seller must be able to move the schedule forward without hitting a deadlock.
        var updated = await service.UpdateAsync(
            sellerActor,
            auction.Id,
            new UpdateAuctionRequest(null, null, now.AddHours(1), now.AddHours(3)));
        updated.Status.Should().Be(AuctionStatus.PendingApproval);

        // Once rescheduled into the future, admin can approve cleanly.
        var approved = await service.ApproveAuctionAsync(adminActor, auction.Id);
        approved.FromStatus.Should().Be(AuctionStatus.PendingApproval);
        approved.Status.Should().Be(AuctionStatus.Scheduled);
    }

    [Fact]
    public async Task PublicList_NeverExposesPendingApproval_EvenWhenExplicitlyFiltered()
    {
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var (service, db, _) = CreateSut(now);
        var sellerId = Guid.NewGuid();

        await SeedPendingAuctionAsync(db, sellerId, now.AddHours(1), now.AddHours(3), now);

        var publicDefault = await service.ListActiveAuctionsAsync(1, 20, new AuctionListFilter());
        var publicExplicit = await service.ListActiveAuctionsAsync(1, 20, new AuctionListFilter(Status: "PendingApproval"));
        var adminList = await service.ListAdminAuctionsAsync(1, 20, status: "PENDING_APPROVAL");

        publicDefault.Items.Should().BeEmpty();
        publicExplicit.Items.Should().BeEmpty();
        adminList.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task RejectAuction_RequiresValReason_CancelsAndUnlocksCatalogProduct()
    {
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var (service, db, catalogSync) = CreateSut(now);
        var sellerId = Guid.NewGuid();
        var adminActor = new ActorContext(Guid.NewGuid(), "trace-3", "127.0.0.1");

        var auction = await SeedPendingAuctionAsync(db, sellerId, now.AddHours(1), now.AddHours(3), now);

        var missingReason = () => service.RejectAuctionAsync(adminActor, auction.Id, new RejectAuctionRequest("   "));
        (await missingReason.Should().ThrowAsync<DomainException>())
            .Which.ErrorCode.Should().Be("VAL_REASON_REQUIRED");

        var rejected = await service.RejectAuctionAsync(adminActor, auction.Id, new RejectAuctionRequest("Giá khởi điểm không hợp lệ"));
        rejected.FromStatus.Should().Be(AuctionStatus.PendingApproval);
        rejected.Status.Should().Be(AuctionStatus.Cancelled);

        (await db.ProductOpenAuctions.AnyAsync(p => p.AuctionId == auction.Id)).Should().BeFalse();
        catalogSync.Verify(c => c.UnlockProduct(auction.ProductId, auction.Id), Times.Once);
    }
}
