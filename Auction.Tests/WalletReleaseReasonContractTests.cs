namespace Auction.Tests;

using System.Reflection;
using Auction.Infrastructure.Clients;
using FluentAssertions;
using Xunit;

/// <summary>
/// T23: Auction has many detailed reasons for releasing a deposit, but Wallet accepts only five. The mapper in WalletClient must turn every
/// reason Auction uses into one of those five, otherwise Wallet answers 400 and the money stays locked.
/// The same five values are checked on the Wallet side (Wallet.Tests, ReleaseReasonContractTests); change both together.
/// </summary>
public class WalletReleaseReasonContractTests
{
    private static readonly string[] WalletAccepts =
        { "AUCTION_ENDED_NOT_WINNER", "AUCTION_CANCELLED", "AUCTION_FAILED", "ADMIN_FORCE_CANCEL", "BUYER_WITHDRAWN" };

    private static string Normalize(string? reason)
    {
        var method = typeof(WalletClient).GetMethod("NormalizeReleaseReason", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("the mapper that protects the Wallet contract must exist");
        return (string)method!.Invoke(null, new object?[] { reason })!;
    }

    [Theory]
    [InlineData("AUCTION_FAILED")]
    [InlineData("AUCTION_ENDED_NOT_WINNER")]
    [InlineData("AUCTION_ENDED_BUY_NOW")]
    [InlineData("AMOUNT_MISMATCH")]
    [InlineData("LATE_AUTHORIZATION")]
    [InlineData("RECONCILED_CLOSED_AUCTION")]
    [InlineData("RESERVE_NOT_MET")]
    [InlineData("SECOND_CHANCE_EXPIRED")]
    [InlineData("SECOND_CHANCE_DECLINED")]
    [InlineData("ORDER_CANCELLED")]
    [InlineData("AUCTION_CANCELLED")]
    [InlineData("ADMIN_FORCE_CANCEL")]
    [InlineData("SELLER_REJECTED")]
    [InlineData("SELLER_ACCEPT_EXPIRED")]
    [InlineData("AUCTION_SETTLED")]
    [InlineData("something-new")]
    [InlineData(null)]
    public void EveryReasonAuctionUses_BecomesOneWalletAccepts(string? reason)
    {
        WalletAccepts.Should().Contain(Normalize(reason));
    }

    [Theory]
    [InlineData("RESERVE_NOT_MET", "AUCTION_FAILED")]
    [InlineData("SECOND_CHANCE_EXPIRED", "AUCTION_FAILED")]
    [InlineData("ORDER_CANCELLED", "AUCTION_CANCELLED")]
    [InlineData("ADMIN_FORCE_CANCEL", "ADMIN_FORCE_CANCEL")]
    [InlineData("AUCTION_ENDED_BUY_NOW", "AUCTION_ENDED_NOT_WINNER")]
    public void KnownReasonsKeepTheirMeaning(string reason, string expected)
    {
        Normalize(reason).Should().Be(expected);
    }
}
