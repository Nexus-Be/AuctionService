namespace Auction.Tests;

using System.Text.Json;
using Auction.Application.Interfaces;
using Auction.Infrastructure.Notifications;
using FluentAssertions;
using Xunit;

/// <summary>T21: bodies and event ids of the two reserve-price notification routes follow the Notification contract.</summary>
public class ReserveNotificationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body, NotificationPayloads.Json);

    [Fact]
    public void ApprovalNeeded_CarriesBothPricesTheDeadlineAndVnd()
    {
        var n = new ReserveApprovalNeededNotification(Guid.NewGuid(), Guid.NewGuid(), "Camera", 14_000_000m, 15_000_000m, At.AddHours(12));

        var j = Json(NotificationPayloads.BuildReserveApprovalNeeded(n));

        j.GetProperty("highestBidAmount").GetDecimal().Should().Be(14_000_000m);
        j.GetProperty("reservePriceAmount").GetDecimal().Should().Be(15_000_000m);
        j.GetProperty("currency").GetString().Should().Be("VND");
        j.GetProperty("sellerUserId").GetGuid().Should().Be(n.SellerUserId);
        j.GetProperty("sourceEventId").GetGuid().Should().Be(NotificationPayloads.ReserveApprovalId(n));
    }

    [Fact]
    public void ApprovalNeeded_SameAuctionAndDeadline_GivesTheSameEventId()
    {
        var one = new ReserveApprovalNeededNotification(Guid.NewGuid(), Guid.NewGuid(), "x", 1m, 2m, At);
        var two = one with { AuctionTitle = "renamed" };

        NotificationPayloads.ReserveApprovalId(one).Should().Be(NotificationPayloads.ReserveApprovalId(two));
    }

    [Fact]
    public void Warning_HasNoRestrictedUntil()
    {
        var n = new ReserveRestrictionNotification(Guid.NewGuid(), Guid.NewGuid(), 2, false, At.AddDays(14), At);

        var j = Json(NotificationPayloads.BuildReserveRestriction(n));

        j.GetProperty("isRestricted").GetBoolean().Should().BeFalse();
        j.TryGetProperty("restrictedUntil", out _).Should().BeFalse();
        j.GetProperty("consecutiveDeclineCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Restriction_CarriesTheEndOfTheRestriction()
    {
        var n = new ReserveRestrictionNotification(Guid.NewGuid(), Guid.NewGuid(), 3, true, At.AddDays(14), At);

        var j = Json(NotificationPayloads.BuildReserveRestriction(n));

        j.GetProperty("isRestricted").GetBoolean().Should().BeTrue();
        j.GetProperty("restrictedUntil").GetDateTimeOffset().Should().Be(At.AddDays(14));
    }

    [Fact]
    public void Routes_AreTheOnesNotificationExposes()
    {
        NotificationPayloads.PathOf(NotificationPayloads.ReserveApprovalNeeded).Should().Be("/api/v1/notifications/auction-reserve-approval-needed");
        NotificationPayloads.PathOf(NotificationPayloads.ReserveRestriction).Should().Be("/api/v1/notifications/auction-reserve-restriction");
    }
}
