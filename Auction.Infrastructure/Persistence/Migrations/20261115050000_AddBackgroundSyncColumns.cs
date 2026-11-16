using System;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds background sync and retry tracking columns (Overview 4.1 &amp; v0.4) to AuctionDeposits, Auctions, and AuctionWinnerOffers.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261115050000_AddBackgroundSyncColumns")]
    public partial class AddBackgroundSyncColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AuctionDeposits
            migrationBuilder.AddColumn<string>(
                name: "CapturePurpose",
                table: "AuctionDeposits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CapturedAt",
                table: "AuctionDeposits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingAction",
                table: "AuctionDeposits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PendingPurpose",
                table: "AuctionDeposits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "AuctionDeposits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "AuctionDeposits",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsAttention",
                table: "AuctionDeposits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastGatewayEventId",
                table: "AuctionDeposits",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Auctions
            migrationBuilder.AddColumn<int>(
                name: "ViolationState",
                table: "Auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StreakResetState",
                table: "Auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StockReleaseState",
                table: "Auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "Auctions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Auctions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsAttention",
                table: "Auctions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // AuctionWinnerOffers
            migrationBuilder.AddColumn<int>(
                name: "SettlementState",
                table: "AuctionWinnerOffers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "AuctionWinnerOffers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "AuctionWinnerOffers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "AuctionWinnerOffers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsAttention",
                table: "AuctionWinnerOffers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CapturePurpose", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "CapturedAt", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "PendingAction", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "PendingPurpose", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "NextRetryAt", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "LastError", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "NeedsAttention", table: "AuctionDeposits");
            migrationBuilder.DropColumn(name: "LastGatewayEventId", table: "AuctionDeposits");

            migrationBuilder.DropColumn(name: "ViolationState", table: "Auctions");
            migrationBuilder.DropColumn(name: "StreakResetState", table: "Auctions");
            migrationBuilder.DropColumn(name: "StockReleaseState", table: "Auctions");
            migrationBuilder.DropColumn(name: "AttemptCount", table: "Auctions");
            migrationBuilder.DropColumn(name: "NextRetryAt", table: "Auctions");
            migrationBuilder.DropColumn(name: "LastError", table: "Auctions");
            migrationBuilder.DropColumn(name: "NeedsAttention", table: "Auctions");

            migrationBuilder.DropColumn(name: "SettlementState", table: "AuctionWinnerOffers");
            migrationBuilder.DropColumn(name: "AttemptCount", table: "AuctionWinnerOffers");
            migrationBuilder.DropColumn(name: "NextRetryAt", table: "AuctionWinnerOffers");
            migrationBuilder.DropColumn(name: "LastError", table: "AuctionWinnerOffers");
            migrationBuilder.DropColumn(name: "NeedsAttention", table: "AuctionWinnerOffers");
        }
    }
}
