namespace Auction.Tests;

using System;
using System.Collections.Generic;
using Auction.Application.Common;
using Auction.Application.Interfaces;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;
using Auction.Domain.Rules;
using FluentAssertions;
using Xunit;

/// <summary>The deposit rules decide how much money a bidder locks, so every boundary of them is pinned here.</summary>
public class DepositRulesTests
{
    private static readonly DepositPolicy Policy = DepositRules.DefaultPolicy;

    // ------------------------------------------------------------------ amount

    [Theory]
    // reputation tiers (both ends are inside the tier): 80-100 = 5%, 60-79 = 10%, 40-59 = 15%, 0-39 = 20%
    [InlineData(1_000_000, 100, 0, 50_000, 5)]
    [InlineData(1_000_000, 80, 0, 50_000, 5)]
    [InlineData(1_000_000, 79, 0, 100_000, 10)]
    [InlineData(1_000_000, 60, 0, 100_000, 10)]
    [InlineData(1_000_000, 59, 0, 150_000, 15)]
    [InlineData(1_000_000, 40, 0, 150_000, 15)]
    [InlineData(1_000_000, 39, 0, 200_000, 20)]
    [InlineData(1_000_000, 0, 0, 200_000, 20)]
    // the streak surcharge (+10 points from a streak of 2) and the 30% cap
    [InlineData(1_000_000, 90, 1, 50_000, 5)]
    [InlineData(1_000_000, 90, 2, 150_000, 15)]
    [InlineData(1_000_000, 90, 7, 150_000, 15)]
    [InlineData(1_000_000, 10, 2, 300_000, 30)]          // 20 + 10 = 30, exactly the cap
    public void The_rate_comes_from_the_reputation_tier_plus_the_streak_surcharge_capped_at_the_maximum(
        decimal startPrice, int reputation, int streak, decimal expectedAmount, decimal expectedRate)
    {
        var quote = DepositRules.Calculate(Policy, startPrice, reputation, isNewUser: false, streak);

        quote.Amount.Should().Be(expectedAmount);
        quote.RatePercent.Should().Be(expectedRate);
    }

    [Fact]
    public void The_cap_applies_even_when_tier_and_surcharge_add_up_to_more()
    {
        var policy = Policy with { StreakSurcharges = new[] { new StreakSurcharge(1, 25m) } };

        var quote = DepositRules.Calculate(policy, 1_000_000m, 10, false, 1);   // 20 + 25 = 45, capped at 30

        quote.RatePercent.Should().Be(30m);
        quote.Amount.Should().Be(300_000m);
    }

    [Theory]
    [InlineData(true, 95)]       // flagged as a new user: the new-user rate wins over a high score
    [InlineData(false, null)]    // no score yet
    [InlineData(true, null)]
    public void A_new_user_or_a_user_without_a_score_pays_the_new_user_rate(bool isNewUser, int? reputation)
    {
        var quote = DepositRules.Calculate(Policy, 1_000_000m, reputation, isNewUser, 0);

        quote.RatePercent.Should().Be(20m);
        quote.Amount.Should().Be(200_000m);
    }

    [Fact]
    public void A_score_outside_every_tier_falls_back_to_the_new_user_rate()
    {
        DepositRules.Calculate(Policy, 1_000_000m, 150, false, 0).RatePercent.Should().Be(20m);
        DepositRules.Calculate(Policy, 1_000_000m, -5, false, 0).RatePercent.Should().Be(20m);
    }

    [Fact]
    public void The_amount_is_rounded_up_to_a_whole_unit()
    {
        // 5% of 1,000,001 is 50,000.05: the bidder locks 50,001, never less than the rate says.
        DepositRules.Calculate(Policy, 1_000_001m, 90, false, 0).Amount.Should().Be(50_001m);
    }

    [Fact]
    public void A_small_start_price_pays_the_minimum_deposit_and_says_so()
    {
        var quote = DepositRules.Calculate(Policy, 400_000m, 90, false, 0);        // 5% = 20,000, below the 50,000 minimum

        quote.Amount.Should().Be(50_000m);
        quote.MinDepositApplied.Should().BeTrue();
    }

    [Fact]
    public void The_deposit_never_exceeds_the_start_price()
    {
        var quote = DepositRules.Calculate(Policy, 30_000m, 90, false, 0);         // the minimum (50,000) is more than the whole price

        quote.Amount.Should().Be(30_000m);
        quote.MinDepositApplied.Should().BeTrue();
    }

    [Fact]
    public void The_minimum_is_not_reported_when_the_rate_already_reaches_it()
    {
        DepositRules.Calculate(Policy, 1_000_000m, 90, false, 0).MinDepositApplied.Should().BeFalse();
    }

    // ------------------------------------------------------------------ window

    [Theory]
    [InlineData(AuctionStatus.Scheduled, 10, true)]
    [InlineData(AuctionStatus.Scheduled, -10, true)]     // Scheduled stays open (the end time is not the start of the run yet)
    [InlineData(AuctionStatus.Running, 10, true)]
    [InlineData(AuctionStatus.Running, 0, false)]        // the moment of the end time is already closed
    [InlineData(AuctionStatus.Running, -10, false)]
    [InlineData(AuctionStatus.Draft, 10, false)]
    [InlineData(AuctionStatus.Settled, 10, false)]
    [InlineData(AuctionStatus.Ended, 10, false)]
    [InlineData(AuctionStatus.Cancelled, 10, false)]
    public void Deposits_are_open_only_while_the_auction_is_scheduled_or_running_and_not_over(AuctionStatus status, int minutesUntilEnd, bool open)
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        DepositRules.IsOpenForDeposit(status, now.AddMinutes(minutesUntilEnd), now).Should().Be(open);
    }

    // ------------------------------------------------------------------ policy validation

    private static IReadOnlyList<DepositTier> GoodTiers => new[] { new DepositTier(0, 49, 20m), new DepositTier(50, 100, 5m) };

    private static void Validate(
        string? reason = "valid reason", IReadOnlyList<DepositTier>? tiers = null, decimal? newUser = 20m, decimal? max = 30m,
        decimal? min = 50_000m, decimal? seller = 70m, decimal? platform = 30m, IReadOnlyList<StreakSurcharge>? streaks = null) =>
        DepositRules.ValidatePolicy(reason, tiers ?? GoodTiers, newUser, streaks ?? Array.Empty<StreakSurcharge>(), max, min, seller, platform);

    [Fact]
    public void A_consistent_policy_is_accepted()
    {
        var act = () => Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void A_policy_change_needs_a_reason_of_at_least_five_characters(string? reason)
    {
        var act = () => Validate(reason: reason);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public void The_forfeiture_split_must_add_up_to_100()
    {
        var act = () => Validate(seller: 60m, platform: 30m);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("FORFEITURE_SPLIT_NOT_100");
    }

    [Theory]
    [InlineData(0, 49, 50, 99)]          // gap: 100 is not covered
    [InlineData(0, 60, 50, 100)]         // overlap
    [InlineData(5, 100, 0, 0)]           // does not start at 0
    public void The_tiers_must_cover_0_to_100_without_gaps_or_overlaps(int aMin, int aMax, int bMin, int bMax)
    {
        var tiers = bMax == 0 && bMin == 0
            ? new[] { new DepositTier(aMin, aMax, 10m) }
            : new[] { new DepositTier(aMin, aMax, 10m), new DepositTier(bMin, bMax, 5m) };

        var act = () => Validate(tiers: tiers);

        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("POLICY_TIERS_INVALID");
    }

    [Fact]
    public void A_tier_rate_above_the_maximum_is_refused()
    {
        var act = () => Validate(tiers: new[] { new DepositTier(0, 100, 40m) }, max: 30m);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("POLICY_TIERS_INVALID");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void The_maximum_rate_must_be_between_0_and_100(int max)
    {
        var act = () => Validate(max: max);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public void The_new_user_rate_cannot_exceed_the_maximum_and_the_minimum_deposit_cannot_be_negative()
    {
        var newUser = () => Validate(newUser: 40m, max: 30m);
        var minimum = () => Validate(min: -1m);

        newUser.Should().Throw<DomainException>();
        minimum.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_streak_surcharge_needs_a_streak_of_at_least_one_and_a_non_negative_addition()
    {
        var zero = () => Validate(streaks: new[] { new StreakSurcharge(0, 5m) });
        var negative = () => Validate(streaks: new[] { new StreakSurcharge(2, -1m) });

        zero.Should().Throw<DomainException>();
        negative.Should().Throw<DomainException>();
    }
}

/// <summary>Who may take part in an auction, from the facts the User service reports.</summary>
public class DepositProfileGuardTests
{
    private static UserAuctionDepositProfile Profile(
        string status = "Active", bool restricted = false, DateTimeOffset? until = null, int? reputation = 90) =>
        new(Guid.NewGuid(), status, reputation, false, 0, restricted, restricted ? "Suspended" : null, until, null);

    [Fact]
    public void An_active_unrestricted_account_may_join_whatever_its_reputation()
    {
        var low = () => DepositProfileGuard.EnsureCanJoin(Profile(reputation: 0));
        var none = () => DepositProfileGuard.EnsureCanJoin(Profile(reputation: null));

        low.Should().NotThrow();
        none.Should().NotThrow();
    }

    [Theory]
    [InlineData("PendingVerification", "ACCOUNT_NOT_VERIFIED", 403)]
    [InlineData("PENDING_VERIFICATION", "ACCOUNT_NOT_VERIFIED", 403)]
    [InlineData("Banned", "ACCOUNT_BANNED", 403)]
    [InlineData("banned", "ACCOUNT_BANNED", 403)]
    [InlineData("Deleted", "USER_NOT_FOUND", 404)]
    public void An_account_that_is_not_in_good_standing_is_refused_with_its_own_code(string status, string code, int http)
    {
        var act = () => DepositProfileGuard.EnsureCanJoin(Profile(status));

        var error = act.Should().Throw<DomainException>().Which;
        error.ErrorCode.Should().Be(code);
        error.StatusCode.Should().Be(http);
    }

    [Fact]
    public void A_restricted_bidder_is_refused_and_the_message_names_the_end_of_a_timed_restriction()
    {
        var open = () => DepositProfileGuard.EnsureCanJoin(Profile(restricted: true));
        var timed = () => DepositProfileGuard.EnsureCanJoin(Profile(restricted: true, until: new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero)));

        open.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("BIDDER_RESTRICTED");
        timed.Should().Throw<DomainException>().Which.Message.Should().Contain("2026-12-01 08:30 UTC");
    }
}

/// <summary>The rules every bid goes through before any money is touched.</summary>
public class BidRulesTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("1000.5", true)]
    [InlineData("1000.55", true)]
    [InlineData("1000.555", false)]    // three decimals are not money
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData(null, false)]
    public void A_bid_amount_is_positive_with_at_most_two_decimals(string? amount, bool valid)
    {
        var act = () => BidRules.ValidateAmount(amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        if (valid) act.Should().NotThrow();
        else act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VAL_INVALID_BID_AMOUNT");
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("\"12\"", 12)]
    [InlineData("W/\"12\"", 12)]
    [InlineData(" w/\"0\" ", 0)]
    public void The_row_version_is_read_from_if_match_with_or_without_quotes_and_the_weak_prefix(string header, long expected)
    {
        BidRules.ParseRowVersion(header).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "VAL_IF_MATCH_REQUIRED")]
    [InlineData("", "VAL_IF_MATCH_REQUIRED")]
    [InlineData("abc", "VAL_INVALID_IF_MATCH")]
    [InlineData("-3", "VAL_INVALID_IF_MATCH")]
    [InlineData("1.5", "VAL_INVALID_IF_MATCH")]
    public void A_missing_or_malformed_if_match_is_refused(string? header, string code)
    {
        var act = () => BidRules.ParseRowVersion(header);
        act.Should().Throw<DomainException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void The_idempotency_key_is_required_and_has_a_length_limit()
    {
        var missing = () => BidRules.ValidateIdempotencyKey(" ");
        var tooLong = () => BidRules.ValidateIdempotencyKey(new string('k', BidRules.IdempotencyKeyMaxLength + 1));
        var fits = () => BidRules.ValidateIdempotencyKey(new string('k', BidRules.IdempotencyKeyMaxLength));

        missing.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VAL_IDEMPOTENCY_KEY_REQUIRED");
        tooLong.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("VAL_INVALID_IDEMPOTENCY_KEY");
        fits.Should().NotThrow();
    }

    [Fact]
    public void Bids_are_accepted_only_while_the_auction_is_running_and_not_past_its_end()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        var running = () => BidRules.EnsureBiddable(AuctionStatus.Running, now.AddSeconds(1), now);
        var atEnd = () => BidRules.EnsureBiddable(AuctionStatus.Running, now, now);
        var notStarted = () => BidRules.EnsureBiddable(AuctionStatus.Scheduled, now.AddHours(1), now);
        var finished = () => BidRules.EnsureBiddable(AuctionStatus.Settled, now.AddHours(-1), now);

        running.Should().NotThrow();
        atEnd.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("AUCTION_ENDED");
        notStarted.Should().Throw<BusinessRuleValidationException>().Which.ErrorCode.Should().Be("AUCTION_NOT_RUNNING");
        finished.Should().Throw<DomainException>().Which.ErrorCode.Should().Be("AUCTION_ENDED");
    }

    [Fact]
    public void The_first_bid_must_add_one_step_to_start_price_and_later_bids_must_add_one_step()
    {
        BidRules.MinNextBid(1_000_000m, 50_000m, null).Should().Be(1_050_000m);
        BidRules.MinNextBid(1_000_000m, 50_000m, 1_200_000m).Should().Be(1_250_000m);

        var low = () => BidRules.EnsureMeetsMinimum(1_249_999m, 1_250_000m);
        var exact = () => BidRules.EnsureMeetsMinimum(1_250_000m, 1_250_000m);
        low.Should().Throw<BusinessRuleValidationException>().Which.ErrorCode.Should().Be("BID_TOO_LOW");
        exact.Should().NotThrow();
    }
}

/// <summary>When the Buy Now button is open, so a buyer is never offered a price the auction can no longer honour.</summary>
public class BuyNowAvailabilityTests
{
    [Theory]
    // no reserve: open only before the first bid
    [InlineData(AuctionStatus.Running, null, 2_000_000, null, 0, true)]
    [InlineData(AuctionStatus.Running, null, 2_000_000, 1_000_000, 1, false)]
    // with a reserve: open until a bid reaches it
    [InlineData(AuctionStatus.Running, 1_500_000, 2_000_000, null, 0, true)]
    [InlineData(AuctionStatus.Running, 1_500_000, 2_000_000, 1_400_000, 3, true)]
    [InlineData(AuctionStatus.Running, 1_500_000, 2_000_000, 1_500_000, 4, false)]
    [InlineData(AuctionStatus.Running, 1_500_000, 2_000_000, 1_800_000, 5, false)]
    // never without a Buy Now price, never outside a running auction
    [InlineData(AuctionStatus.Running, null, null, null, 0, false)]
    [InlineData(AuctionStatus.Scheduled, null, 2_000_000, null, 0, false)]
    [InlineData(AuctionStatus.Settled, null, 2_000_000, null, 0, false)]
    [InlineData(AuctionStatus.Ended, null, 2_000_000, null, 0, false)]
    public void Buy_now_is_open_only_in_the_cases_the_rule_allows(
        AuctionStatus status, int? reserve, int? buyNow, int? current, int bids, bool expected)
    {
        var open = AuctionRules.IsBuyNowAvailable(status, reserve, buyNow, current, bids);

        open.Should().Be(expected);
    }
}
