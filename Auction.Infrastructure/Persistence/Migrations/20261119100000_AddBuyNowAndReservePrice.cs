using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261119100000_AddBuyNowAndReservePrice")]
    public partial class AddBuyNowAndReservePrice : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""Auctions"" ADD COLUMN IF NOT EXISTS ""ReservePriceAmount"" numeric(18,2) NULL;
ALTER TABLE ""Auctions"" ADD COLUMN IF NOT EXISTS ""BuyNowPriceAmount"" numeric(18,2) NULL;

ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""SellerApprovalDeadlineAt"" timestamp with time zone NULL;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""IsBelowReserve"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""IsBuyNow"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""BuyNowGoodsDepositId"" uuid NULL;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""BuyNowGoodsHoldId"" text NULL;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""BuyNowGoodsAmount"" numeric(18,2) NULL;
ALTER TABLE ""AuctionSettlements"" ADD COLUMN IF NOT EXISTS ""BuyNowGoodsPendingAction"" integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS ""SellerReservePenalties"" (
    ""Id"" uuid NOT NULL,
    ""SellerId"" uuid NOT NULL,
    ""ConsecutiveDeclineCount"" integer NOT NULL,
    ""ReserveRestrictedUntil"" timestamp with time zone NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    ""IsDeleted"" boolean NOT NULL,
    ""DeletedAt"" timestamp with time zone NULL,
    CONSTRAINT ""PK_SellerReservePenalties"" PRIMARY KEY (""Id"")
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SellerReservePenalties_SellerId"" ON ""SellerReservePenalties"" (""SellerId"");
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS ""SellerReservePenalties"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""BuyNowGoodsPendingAction"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""BuyNowGoodsAmount"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""BuyNowGoodsHoldId"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""BuyNowGoodsDepositId"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""IsBuyNow"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""IsBelowReserve"";
ALTER TABLE ""AuctionSettlements"" DROP COLUMN IF EXISTS ""SellerApprovalDeadlineAt"";
ALTER TABLE ""Auctions"" DROP COLUMN IF EXISTS ""BuyNowPriceAmount"";
ALTER TABLE ""Auctions"" DROP COLUMN IF EXISTS ""ReservePriceAmount"";
");
        }
    }
}
