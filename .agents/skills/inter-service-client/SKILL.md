---
name: inter-service-client
description: Add or modify outbound typed REST clients in AuctionService (port 8005). Use when AuctionService interacts with WalletService for bid deposits/payments or NotificationService for outbid alerts.
---

# Outbound REST Client in AuctionService

> Scope: AuctionService repository only. Never reference parent workspace paths, root documentation, or sibling service directories.

## Local Facts

- Service: AuctionService
- Local HTTP Port: 8005
- Primary Downstream Targets:
  - WalletService (`8008`): Bid deposit holds, auction settlement payments (`IWalletClient`)
  - NotificationService (`8007`): Outbid notices, session closure notifications (`INotificationClient`)
- Interface Location: `Auction.Application/Interfaces/`
- Implementation Location: `Auction.Infrastructure/Services/{Target}/`
- DI Registration: `Auction.Infrastructure/DependencyInjection.cs`

## Invariants

1. **100% RESTful HTTP/JSON:** RabbitMQ, MassTransit, and message brokers are decommissioned. All inter-service calls use typed `HttpClient`.
2. **Strict Project Isolation:** Never reference external assemblies or query other microservice databases directly.
3. **No Internal API Key:** Inter-service routes use standard public endpoints. Do not send `X-Internal-Service-Key`.
4. **Mandatory Idempotency Keys:** Mutating deposit or settlement calls must transmit an `Idempotency-Key` header with a UUIDv4 string.
5. **Identity Propagation:** Pass the bidder/user identity in the `x-user-id` header.
6. **Timeout:** Enforce `client.Timeout = TimeSpan.FromSeconds(10)`.

## Implementation Steps

### 1. Application Contract
Define `Auction.Application/Interfaces/IWalletClient.cs`:
```csharp
namespace Auction.Application.Interfaces;

public interface IWalletClient
{
    Task<bool> HoldBidDepositAsync(
        Guid auctionId,
        string bidderUserId,
        decimal amount,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}
```

### 2. Infrastructure Typed Client
Implement `Auction.Infrastructure/Services/Wallet/WalletClient.cs`:
```csharp
namespace Auction.Infrastructure.Services.Wallet;

public sealed class WalletClient : IWalletClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WalletClient> _logger;

    public WalletClient(HttpClient httpClient, ILogger<WalletClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> HoldBidDepositAsync(
        Guid auctionId,
        string bidderUserId,
        decimal amount,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { auctionId, amount };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/wallets/checkout/direct")
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(bidderUserId))
            {
                request.Headers.TryAddWithoutValidation("x-user-id", bidderUserId);
            }

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[REST Wallet] Bid hold failed: {Status} {Error}", response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[REST Wallet] Connection error while holding bid deposit.");
            return false;
        }
    }
}
```

### 3. Dependency Injection Registration
In `Auction.Infrastructure/DependencyInjection.cs`:
```csharp
var walletBaseUrl = Environment.GetEnvironmentVariable("WALLET_SERVICE_URL")
    ?? configuration["WALLET_SERVICE_URL"]
    ?? configuration["Services:Wallet:BaseUrl"]
    ?? "http://localhost:8008/";

services.AddHttpClient<IWalletClient, WalletClient>(client =>
{
    client.BaseAddress = new Uri(walletBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
```

### 4. Configuration Synchronization
Declare `WALLET_SERVICE_URL: "${WALLET_SERVICE_URL:-http://localhost:8008/}"` in `docker-compose.yml` and `.env.example`.
