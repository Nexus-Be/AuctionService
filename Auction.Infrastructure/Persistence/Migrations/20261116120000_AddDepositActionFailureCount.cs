using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds ActionFailureCount to AuctionDeposits so retries of Release, Capture and Refund no longer change AttemptCount,
    /// which is part of the Idempotency-Key used to create the Wallet hold.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261116120000_AddDepositActionFailureCount")]
    public partial class AddDepositActionFailureCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActionFailureCount",
                table: "AuctionDeposits",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionFailureCount",
                table: "AuctionDeposits");
        }
    }
}
