namespace Auction.Tests;

using System;
using Auction.Application.Services.Deposit;
using Auction.Domain.Enums;
using Auction.Domain.Rules;
using FluentAssertions;
using Xunit;

public class BuyNowAndDepositContractTests
{
    [Fact]
    public void BuyNowGoodsCaptureTransfers_SendsToSystemEscrowWithNullRecipientUserId()
    {
        var auctionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        const decimal amount = 850_000m;

        var transfers = DepositService.BuyNowGoodsCaptureTransfers(auctionId, orderId, amount);

        transfers.Should().ContainSingle();
        var instruction = transfers[0];
        instruction.DestinationType.Should().Be("SYSTEM_ESCROW");
        instruction.DestinationUserId.Should().BeNull();
        instruction.Amount.Should().Be(amount);
        instruction.Description.Should().Contain(auctionId.ToString()).And.Contain(orderId.ToString());
    }

    [Theory]
    [InlineData(AuctionStatus.Running, null, 1_000_000, null, 0, true)]
    [InlineData(AuctionStatus.Running, null, 1_000_000, 900_000, 1, false)]
    [InlineData(AuctionStatus.Running, null, 1_000_000, 1_000_000, 1, false)]
    [InlineData(AuctionStatus.Running, 800_000, 1_000_000, 850_000, 1, false)]
    [InlineData(AuctionStatus.Running, 800_000, 1_000_000, 700_000, 1, true)]
    [InlineData(AuctionStatus.Scheduled, null, 1_000_000, null, 0, false)]
    [InlineData(AuctionStatus.Ended, null, 1_000_000, null, 0, false)]
    public void IsBuyNowAvailable_FollowsStatusReserveAndCurrentPriceRules(
        AuctionStatus status, int? reserve, int? buyNow, int? currentPrice, int bidCount, bool expected)
    {
        AuctionRules.IsBuyNowAvailable(
                status,
                reserve.HasValue ? reserve.Value : null,
                buyNow.HasValue ? buyNow.Value : null,
                currentPrice.HasValue ? currentPrice.Value : null,
                bidCount)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, true, false, true)]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, false, true, true)]
    [InlineData(AuctionSettlementStatus.AwaitingSellerApproval, false, false, false)]
    [InlineData(AuctionSettlementStatus.PendingPayment, true, true, false)]
    public void ShowsSellerApproval_HidesReserveStateFromBuyers(
        AuctionSettlementStatus settlementStatus, bool isSeller, bool isPrivileged, bool expected)
    {
        AuctionRules.ShowsSellerApproval(settlementStatus, isSeller, isPrivileged).Should().Be(expected);
    }
}
