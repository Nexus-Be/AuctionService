using System;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stores the non-payment violation waiting for the User service (buyer, order, time, second chance or not) when it is decided.
    /// The retry job used to read them from the settlement, which a runner-up's acceptance overwrites.
    /// Existing Pending rows keep null values and fall back to the settlement, as before.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261116140000_AddPendingViolationSnapshot")]
    public partial class AddPendingViolationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PendingViolationBuyerId",
                table: "Auctions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingViolationOrderId",
                table: "Auctions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingViolationOccurredAt",
                table: "Auctions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingViolationIsSecondChance",
                table: "Auctions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PendingViolationBuyerId", table: "Auctions");
            migrationBuilder.DropColumn(name: "PendingViolationOrderId", table: "Auctions");
            migrationBuilder.DropColumn(name: "PendingViolationOccurredAt", table: "Auctions");
            migrationBuilder.DropColumn(name: "PendingViolationIsSecondChance", table: "Auctions");
        }
    }
}
