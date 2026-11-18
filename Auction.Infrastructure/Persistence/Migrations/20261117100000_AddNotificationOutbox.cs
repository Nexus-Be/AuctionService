using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auction.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the notification outbox: a notification row is saved in the same transaction as the business change that causes it and a
    /// worker sends it afterwards. Written with IF NOT EXISTS because the database is shared and may already hold the table.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261117100000_AddNotificationOutbox")]
    public partial class AddNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""NotificationOutbox"" (
    ""Id"" uuid NOT NULL,
    ""EventId"" uuid NOT NULL,
    ""Kind"" character varying(60) NOT NULL,
    ""AuctionId"" uuid NOT NULL,
    ""PayloadJson"" text NULL,
    ""ContextJson"" text NULL,
    ""Status"" integer NOT NULL,
    ""AttemptCount"" integer NOT NULL,
    ""NextRetryAt"" timestamp with time zone NOT NULL,
    ""LockedUntil"" timestamp with time zone NULL,
    ""LastError"" character varying(500) NULL,
    ""SentAt"" timestamp with time zone NULL,
    ""GiveUpAt"" timestamp with time zone NOT NULL,
    ""CreatedAt"" timestamp with time zone NOT NULL,
    ""UpdatedAt"" timestamp with time zone NOT NULL,
    ""IsDeleted"" boolean NOT NULL,
    ""DeletedAt"" timestamp with time zone NULL,
    CONSTRAINT ""PK_NotificationOutbox"" PRIMARY KEY (""Id"")
);
CREATE INDEX IF NOT EXISTS ""IX_NotificationOutbox_AuctionId"" ON ""NotificationOutbox"" (""AuctionId"");
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_NotificationOutbox_EventId"" ON ""NotificationOutbox"" (""EventId"");
CREATE INDEX IF NOT EXISTS ""IX_NotificationOutbox_Status_NextRetryAt"" ON ""NotificationOutbox"" (""Status"", ""NextRetryAt"");
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""NotificationOutbox"";");
        }
    }
}
