# AUCTION SERVICE — AI AGENT MISSION PROTOCOL & RULES

> **Service:** AuctionService (Port 8005, Database `auction_db:5437`)
> **Scoping:** 100% Inward-Only. Never reference outward to root or sibling services.

## Local Governance Rules
Every AI Coding Agent working inside AuctionService must strictly comply with the local rules in `.agents/rules/`:
1. [01 Clean Architecture Layers](rules/01_clean_architecture_layers.md): 4-layer structure, Feature Co-location, No-Repository over EF Core.
2. [02 API Routing & Envelopes](rules/02_api_routing_and_envelopes.md): 100% Plural route convention, pure DTO return, zero ResultFilter.
3. [03 Security Headers & Internal API](rules/03_security_headers_and_internal.md): Public vs Internal controller segregation, `X-Internal-Service-Key`, bidder identity.
4. [04 Audit & Auction Logging](rules/04_audit_and_event_logging.md): AuditableEntity tracking, bid and session lifecycle audit trails.
5. [05 Error Handling & Enums](rules/05_error_handling_and_enums.md): 100% English sentence-case messages, SCREAMING_SNAKE_CASE error codes, PascalCase JSON enum serialization.
6. [06 Database & Migrations](rules/06_database_and_migrations.md): Database isolation, PostgreSQL integer enums, soft delete, deterministic seed GUIDs.

## Verification Checklist
- `dotnet build Auction.sln -c Release` must succeed with 0 errors.
