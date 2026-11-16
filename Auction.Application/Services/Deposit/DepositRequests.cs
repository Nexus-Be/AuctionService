namespace Auction.Application.Services.Deposit;

using System;

// Fields are nullable so that a missing value is reported with a clear message instead of a silent default.

/// <summary>
/// Buyer xác nhận điều khoản cọc đã thấy ở deposit-requirement. AcceptedPolicyVersion và AcceptedDepositAmount được so với số tính lại ở server.
/// </summary>
public sealed record CreateDepositRequest(int? AcceptedPolicyVersion, decimal? AcceptedDepositAmount);

/// <summary>Kết quả Authorization do Wallet báo về.</summary>
public enum DepositAuthorizationResult
{
    Authorized = 0,
    Failed = 1,
    Expired = 2,
    Voided = 3
}

/// <summary>
/// Callback từ Wallet. EventId dùng để loại trùng. Auction không tin nội dung này mà hỏi lại Wallet trạng thái thật của HoldId,
/// nên Amount, AuthorizedAt, ExpiresAt, FailureCode và ProviderRef chỉ để đối chiếu và ghi log.
/// </summary>
public sealed record DepositAuthorizationResultRequest(
    string? EventId,
    string? HoldId,
    DepositAuthorizationResult? Result,
    decimal? Amount,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? ExpiresAt,
    string? FailureCode,
    string? ProviderRef);
