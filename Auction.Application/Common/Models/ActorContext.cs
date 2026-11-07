namespace Auction.Application.Common.Models;

using System;

/// <summary>Who is calling and where from. Used for ownership checks and audit logs.</summary>
public sealed record ActorContext(Guid UserId, string? TraceId = null, string? ClientIp = null);
