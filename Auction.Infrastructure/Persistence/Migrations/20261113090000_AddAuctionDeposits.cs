using System;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the deposit tables: AuctionDeposits (one deposit per bidder and auction) and DepositCallbackEvents (Inbox of Wallet callbacks).
    /// Additive only. Written by hand (no Designer file); regenerate with `dotnet ef migrations add` if you prefer a generated one.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261113090000_AddAuctionDeposits")]
    public partial class AddAuctionDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuctionDeposits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    GatewayReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GatewayMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaymentRedirectUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PaymentExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReleasePending = table.Column<bool>(type: "boolean", nullable: false),
                    ReleaseReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionDeposits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionDeposits_Auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "Auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DepositCallbackEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DepositId = table.Column<Guid>(type: "uuid", nullable: false),
                    Result = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositCallbackEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepositCallbackEvents_AuctionDeposits_DepositId",
                        column: x => x.DepositId,
                        principalTable: "AuctionDeposits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionDeposits_AuctionId_BidderId",
                table: "AuctionDeposits",
                columns: new[] { "AuctionId", "BidderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionDeposits_BidderId",
                table: "AuctionDeposits",
                column: "BidderId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositCallbackEvents_DepositId",
                table: "DepositCallbackEvents",
                column: "DepositId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositCallbackEvents_EventId",
                table: "DepositCallbackEvents",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DepositCallbackEvents");
            migrationBuilder.DropTable(name: "AuctionDeposits");
        }
    }
}
