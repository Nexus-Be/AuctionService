namespace Auction.Infrastructure.Notifications;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Signs the short-lived service token (HS256) that proves a request to the Gateway and to the Notification service comes from AuctionService.
/// Issuer, audience and scope are fixed by the integration contract; the subject is Auction's configured notification service user.
/// </summary>
public static class ServiceTokenFactory
{
    public const string Issuer = "auction-service";
    public const string Audience = "nexus-auction-notifications";
    public const string Scope = "auction.notifications.write";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>True when the signing key is long enough to be used (HS256 needs at least 32 bytes).</summary>
    public static bool IsUsable(string? signingKey) => !string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) >= 32;

    public static string Create(string signingKey, Guid serviceUserId, DateTimeOffset now)
    {
        if (!IsUsable(signingKey))
            throw new InvalidOperationException("The notification signing key is missing or shorter than 32 bytes.");

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = serviceUserId.ToString("D"),
            ["scope"] = Scope,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(Lifetime).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N")
        }));
        var signed = header + "." + payload;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return signed + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signed)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
