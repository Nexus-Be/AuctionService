namespace Auction.Application.Common;

public static class PaginationConstants
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100; // Matches ApiGateway MaxPageSize = 100 standard

    /// <summary>
    /// Normalizes page (page >= 1) and limit (1 <= limit <= 100) safely.
    /// </summary>
    public static (int Page, int Limit) Normalize(int page, int limit)
    {
        var safePage = page < 1 ? DefaultPage : page;
        var safeLimit = limit switch
        {
            < MinPageSize => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => limit
        };

        return (safePage, safeLimit);
    }
}
