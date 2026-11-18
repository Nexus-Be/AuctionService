using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261118100000_AddAuctionWatchAndReport")]
    public partial class AddAuctionWatchAndReport : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""AuctionWatches"" (
    ""Id"" uuid NOT NULL,
    ""AuctionId"" uuid NOT NULL,
    ""UserId"" uuid NOT NULL,
    ""NotifyOnNewBid"" boolean NOT NULL,
    ""LastNotifiedAt"" timestamp with time zone NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    ""IsDeleted"" boolean NOT NULL,
    ""DeletedAt"" timestamp with time zone NULL,
    CONSTRAINT ""PK_AuctionWatches"" PRIMARY KEY (""Id""),
    CONSTRAINT ""FK_AuctionWatches_Auctions_AuctionId"" FOREIGN KEY (""AuctionId"") REFERENCES ""Auctions"" (""Id"") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AuctionWatches_AuctionId_UserId"" ON ""AuctionWatches"" (""AuctionId"", ""UserId"");
CREATE INDEX IF NOT EXISTS ""IX_AuctionWatches_UserId_CreatedAt"" ON ""AuctionWatches"" (""UserId"", ""CreatedAt"");

CREATE TABLE IF NOT EXISTS ""AuctionReports"" (
    ""Id"" uuid NOT NULL,
    ""AuctionId"" uuid NOT NULL,
    ""ReporterId"" uuid NOT NULL,
    ""Reason"" integer NOT NULL,
    ""Description"" character varying(1000) NOT NULL,
    ""Status"" integer NOT NULL,
    ""AdminNote"" character varying(1000) NULL,
    ""ReviewedBy"" uuid NULL,
    ""ReviewedAt"" timestamp with time zone NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    ""IsDeleted"" boolean NOT NULL,
    ""DeletedAt"" timestamp with time zone NULL,
    CONSTRAINT ""PK_AuctionReports"" PRIMARY KEY (""Id""),
    CONSTRAINT ""FK_AuctionReports_Auctions_AuctionId"" FOREIGN KEY (""AuctionId"") REFERENCES ""Auctions"" (""Id"") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AuctionReports_AuctionId_ReporterId"" ON ""AuctionReports"" (""AuctionId"", ""ReporterId"");
CREATE INDEX IF NOT EXISTS ""IX_AuctionReports_Status_CreatedAt"" ON ""AuctionReports"" (""Status"", ""CreatedAt"");
CREATE INDEX IF NOT EXISTS ""IX_AuctionReports_ReporterId_CreatedAt"" ON ""AuctionReports"" (""ReporterId"", ""CreatedAt"");
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS ""AuctionReports"";
DROP TABLE IF EXISTS ""AuctionWatches"";
");
        }
    }
}
