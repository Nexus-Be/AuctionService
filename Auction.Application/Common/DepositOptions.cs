namespace Auction.Application.Common;

using System;
using System.Collections.Generic;

/// <summary>
/// Settings of the deposit feature, read from the "Deposit" configuration section.
/// CallbackBaseUrl is the address Wallet uses to reach this service (for example the API Gateway), without a trailing path.
/// AllowedReturnUrlHosts lists the host names a returnUrl may point to; an empty list refuses every returnUrl.
/// PaymentWindowHours is the winner payment window kept by Commerce and is only used to size the hold.
/// SafetyMarginMinutes: a bid is refused or re-verified when the hold expires within this margin.
/// </summary>
public sealed record DepositOptions(
    string CallbackBaseUrl,
    IReadOnlyList<string> AllowedReturnUrlHosts,
    int PaymentWindowHours = 24,
    int SafetyMarginMinutes = 5)
{
    public static DepositOptions Default { get; } = new(string.Empty, Array.Empty<string>());
}
