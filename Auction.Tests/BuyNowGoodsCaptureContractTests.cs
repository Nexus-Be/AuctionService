namespace Auction.Tests;

using Auction.Application.Services.Deposit;
using FluentAssertions;
using Xunit;

/// <summary>
/// Wallet accepts an ORDER_PAYMENT capture only with exactly one SYSTEM_ESCROW transfer that has no owner. The Buy Now goods hold used to be sent
/// with the seller id and was refused with 400, so it was never captured and the seller could not be paid from it.
/// </summary>
public class BuyNowGoodsCaptureContractTests
{
    [Fact]
    public void The_goods_hold_is_captured_with_one_ownerless_system_escrow_transfer()
    {
        var transfers = DepositService.BuyNowGoodsCaptureTransfers(1_950_000m, Guid.NewGuid(), Guid.NewGuid());

        var transfer = transfers.Should().ContainSingle().Subject;
        transfer.DestinationType.Should().Be("SYSTEM_ESCROW");
        transfer.DestinationUserId.Should().BeNull();
        transfer.Amount.Should().Be(1_950_000m);
    }
}
