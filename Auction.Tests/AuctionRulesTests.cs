namespace Auction.Tests;

using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Auction.Domain.Rules;
using FluentAssertions;
using Xunit;

public class AuctionRulesTests
{
    [Theory]
    [InlineData(1000, true)]
    [InlineData(5000, true)]
    [InlineData(1500, false)]
    [InlineData(0, false)]
    [InlineData(-1000, false)]
    public void BidStep_MustBeAPositiveMultipleOfOneThousand(int step, bool valid)
    {
        var act = () => AuctionRules.ValidateBidStep(step);
        if (valid) act.Should().NotThrow(); else act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VAL_INVALID_PRICE");
    }

    [Fact]
    public void DefaultBidStep_IsTenPercentOfTheStartPrice_RoundedToThousands()
    {
        AuctionRules.ResolveBidStep(1_234_000m, null, 10m).Should().Be(123_000m);
        AuctionRules.ResolveBidStep(1_000_000m, 20_000m, 10m).Should().Be(20_000m);
    }

    [Fact]
    public void DefaultBidStep_ForATinyStartPrice_AsksTheSellerForAStep()
    {
        var act = () => AuctionRules.ResolveBidStep(1000m, null, 10m);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VAL_INVALID_PRICE");
    }

    [Fact]
    public void Reserve_BelowTheStartPrice_IsRefused()
    {
        var act = () => AuctionRules.ValidateReserveAndBuyNowPrices(100_000m, 90_000m, null);
        act.Should().Throw<BusinessRuleValidationException>().Which.ErrorCode.Should().Be("INVALID_PRICE_RELATION");
    }

    [Fact]
    public void BuyNow_MustBeAboveTheReserve_OrTheStartPriceWhenThereIsNoReserve()
    {
        var withReserve = () => AuctionRules.ValidateReserveAndBuyNowPrices(100_000m, 150_000m, 150_000m);
        var withoutReserve = () => AuctionRules.ValidateReserveAndBuyNowPrices(100_000m, null, 100_000m);
        var ok = () => AuctionRules.ValidateReserveAndBuyNowPrices(100_000m, 150_000m, 200_000m);

        withReserve.Should().Throw<BusinessRuleValidationException>();
        withoutReserve.Should().Throw<BusinessRuleValidationException>();
        ok.Should().NotThrow();
    }

    [Theory]
    [InlineData(AuctionStatus.Running, 150_000, 120_000, 3, true)]
    [InlineData(AuctionStatus.Running, 150_000, 150_000, 3, false)]
    [InlineData(AuctionStatus.Running, null, null, 0, true)]
    [InlineData(AuctionStatus.Running, null, 100_000, 1, false)]
    [InlineData(AuctionStatus.Ended, 150_000, 120_000, 3, false)]
    public void BuyNow_IsAvailableOnlyWhileItIsStillOpen(AuctionStatus status, int? reserve, int? current, int bids, bool expected)
    {
        AuctionRules.IsBuyNowAvailable(status, reserve, 300_000m, current, bids).Should().Be(expected);
    }

    [Theory]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, true, false, true)]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, false, true, true)]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, false, false, false)]   // the winner must not learn the bid was under the reserve
    [InlineData(AuctionSettlementStatus.PendingPayment, true, true, false)]
    [InlineData(AuctionSettlementStatus.Paid, true, false, false)]
    public void SellerApproval_IsShownOnlyToTheSellerAndStaff(AuctionSettlementStatus status, bool isSeller, bool isPrivileged, bool expected)
    {
        AuctionRules.ShowsSellerApproval(status, isSeller, isPrivileged).Should().Be(expected);
    }

    [Fact]
    public void Schedule_RejectsPastStart_ShortAndLongDurations()
    {
        var now = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var past = () => AuctionRules.ValidateSchedule(now.AddMinutes(-1), now.AddHours(3), now);
        var tooShort = () => AuctionRules.ValidateSchedule(now.AddHours(1), now.AddHours(1).AddMinutes(30), now);
        var tooLong = () => AuctionRules.ValidateSchedule(now.AddHours(1), now.AddHours(1).AddHours(169), now);
        var ok = () => AuctionRules.ValidateSchedule(now.AddHours(1), now.AddHours(3), now);

        past.Should().Throw<DomainException>();
        tooShort.Should().Throw<BusinessRuleValidationException>();
        tooLong.Should().Throw<BusinessRuleValidationException>();
        ok.Should().NotThrow();
    }

    [Fact]
    public void ReserveRules_AreTwelveHoursThreeDeclinesFourteenDays()
    {
        AuctionRules.SellerReserveApprovalWindowHours.Should().Be(12);
        AuctionRules.MaxConsecutiveReserveDeclines.Should().Be(3);
        AuctionRules.ReserveRestrictionDays.Should().Be(14);
    }

    [Theory]
    [InlineData(1_000_000, 100_000, null, 1_000_000)]
    [InlineData(0, 50_000, null, 50_000)]
    [InlineData(1_000_000, 100_000, 1_000_000, 1_100_000)]
    public void MinNextBid_FirstBidUsesStartPriceOrOneStepWhenStartIsZero(
        int startPrice, int bidStep, int? currentPrice, int expected)
    {
        BidRules.MinNextBid(startPrice, bidStep, currentPrice).Should().Be(expected);
    }

    [Fact]
    public void EnsureNotStarted_AllowsPendingApprovalAndScheduledBeforeStart()
    {
        var now = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var futureStart = now.AddHours(1);

        var pending = () => AuctionRules.EnsureNotStarted(AuctionStatus.PendingApproval, futureStart, now, "ERR", "msg", 409);
        var scheduled = () => AuctionRules.EnsureNotStarted(AuctionStatus.Scheduled, futureStart, now, "ERR", "msg", 409);
        var running = () => AuctionRules.EnsureNotStarted(AuctionStatus.Running, futureStart, now, "ERR", "msg", 409);

        pending.Should().NotThrow();
        scheduled.Should().NotThrow();
        running.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void EnsureBiddable_RejectsPendingApprovalAsNotRunning()
    {
        var now = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var act = () => BidRules.EnsureBiddable(AuctionStatus.PendingApproval, now.AddHours(2), now);
        act.Should().Throw<BusinessRuleValidationException>().Which.ErrorCode.Should().Be("AUCTION_NOT_RUNNING");
    }
}
