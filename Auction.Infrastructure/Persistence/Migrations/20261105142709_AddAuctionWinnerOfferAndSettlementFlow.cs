using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionWinnerOfferAndSettlementFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionSettlements_Auctions_AuctionId",
                table: "AuctionSettlements");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "AuctionSettlements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "WinnerId",
                table: "AuctionSettlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WinningBidId",
                table: "AuctionSettlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuctionWinnerOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidId = table.Column<Guid>(type: "uuid", nullable: false),
                    BidderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OfferedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResponseReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionWinnerOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionWinnerOffers_Auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalTable: "Auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuctionWinnerOffers_Bids_BidId",
                        column: x => x.BidId,
                        principalTable: "Bids",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "AuctionSettlements",
                keyColumn: "Id",
                keyValue: new Guid("aa700000-0000-0000-0000-000000000001"),
                columns: new[] { "Status", "WinnerId", "WinningBidId" },
                values: new object[] { 1, new Guid("dddddddd-4444-4444-4444-444444444444"), new Guid("aa300000-0000-0000-0000-000000000002") });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionSettlements_Status_PaymentDeadlineAt",
                table: "AuctionSettlements",
                columns: new[] { "Status", "PaymentDeadlineAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionSettlements_WinningBidId",
                table: "AuctionSettlements",
                column: "WinningBidId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionWinnerOffers_AuctionId",
                table: "AuctionWinnerOffers",
                column: "AuctionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionWinnerOffers_BidId",
                table: "AuctionWinnerOffers",
                column: "BidId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionWinnerOffers_Status_ExpiresAt",
                table: "AuctionWinnerOffers",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionSettlements_Auctions_AuctionId",
                table: "AuctionSettlements",
                column: "AuctionId",
                principalTable: "Auctions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionSettlements_Bids_WinningBidId",
                table: "AuctionSettlements",
                column: "WinningBidId",
                principalTable: "Bids",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionSettlements_Auctions_AuctionId",
                table: "AuctionSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_AuctionSettlements_Bids_WinningBidId",
                table: "AuctionSettlements");

            migrationBuilder.DropTable(
                name: "AuctionWinnerOffers");

            migrationBuilder.DropIndex(
                name: "IX_AuctionSettlements_Status_PaymentDeadlineAt",
                table: "AuctionSettlements");

            migrationBuilder.DropIndex(
                name: "IX_AuctionSettlements_WinningBidId",
                table: "AuctionSettlements");

            migrationBuilder.DeleteData(
                table: "AuctionStates",
                keyColumn: "Id",
                keyValue: new Guid("aa100000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "AuctionStates",
                keyColumn: "Id",
                keyValue: new Guid("aa100000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "ProductOpenAuctions",
                keyColumn: "Id",
                keyValue: new Guid("aa200000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "Auctions",
                keyColumn: "Id",
                keyValue: new Guid("aa000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "Auctions",
                keyColumn: "Id",
                keyValue: new Guid("aa000000-0000-0000-0000-000000000004"));

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AuctionSettlements");

            migrationBuilder.DropColumn(
                name: "WinnerId",
                table: "AuctionSettlements");

            migrationBuilder.DropColumn(
                name: "WinningBidId",
                table: "AuctionSettlements");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionSettlements_Auctions_AuctionId",
                table: "AuctionSettlements",
                column: "AuctionId",
                principalTable: "Auctions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
