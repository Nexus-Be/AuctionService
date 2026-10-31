# 06 — DATABASE PERSISTENCE & MIGRATIONS (AUCTION SERVICE)

## 1. Database Isolation
* Dedicated database: `auction_db` on host port `5437`.
* Primary key: 100% `Guid` for entities (`Auctions`, `Bids`, `AuctionStates`).
* Currency & Bid Precision: `StartingPrice`, `CurrentPrice`, `Amount` must configure `HasPrecision(18, 2)`.

## 2. Entity Configuration & Soft Delete
* Every entity declares `IsDeleted` and `DeletedAt`. Enforce query filter: `builder.HasQueryFilter(x => !x.IsDeleted)`.
* Foreign keys configured with `DeleteBehavior.Restrict`.

## 3. Migration Safety
* No destructive migrations (`DROP TABLE`, `DROP COLUMN`) without explicit authorization.
* Startup safe auto-migration: `db.Database.Migrate()` wrapped in `try-catch` checking `db.Database.IsRelational()`.
