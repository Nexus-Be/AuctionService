# 01 — CLEAN ARCHITECTURE & BOUNDED CONTEXT (AUCTION SERVICE)

## 1. Bounded Context & Port Allocation
* Bounded Context: Real-time auction sessions, incremental bidding, auction status lifecycle, settlement.
* Service Port: `8005` (HTTP REST API).
* Database: PostgreSQL `auction_db` on port `5437`.

## 2. 4-Layer Clean Architecture
* `Auction.Domain`: Entities (`AuctionSession`, `Bid`, `AuctionWinner`), Enums (`AuctionStatus`), Exceptions (`AuctionDomainException`).
* `Auction.Application`: Use cases in `Services/{Feature}/`, interface `IAuctionDbContext`, request/response records.
* `Auction.Infrastructure`: `AppDbContext`, EF Core configurations, migrations.
* `Auction.Api`: Thin controllers in `Controllers/Public/`, middlewares, extensions, composition root (`Program.cs`).

## 3. Feature Co-Location (Mandatory)
* `Services/Auction/`: `IAuctionService.cs`, `AuctionService.cs`, `AuctionRequests.cs`, `AuctionResponses.cs`.
* `Services/Bid/`: `IBidService.cs`, `BidService.cs`, `BidRequests.cs`, `BidResponses.cs`.

## 4. Thin 1-to-1 Controller Pairing
* `AuctionsController.cs` -> injects `IAuctionService`.
* `BidsController.cs` -> injects `IBidService`.

## 5. Absolute Architectural Prohibitions
* 🛑 Prohibit Repository Pattern over EF Core (inject `IAuctionDbContext` directly).
* 🛑 Prohibit MediatR & CQRS handlers.
* 🛑 Prohibit gRPC and Message Brokers.
