namespace Auction.Application.Common.Models;

using System;
using System.Collections.Generic;

public sealed record PagedData<T>(
    IReadOnlyList<T> Items,
    PaginationMetadata Pagination);

public sealed record PaginationMetadata(
    int PageIndex,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage)
{
    public static PaginationMetadata Create(int pageIndex, int pageSize, int totalCount)
    {
        var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
        return new PaginationMetadata(
            PageIndex: pageIndex,
            PageSize: pageSize,
            TotalCount: totalCount,
            TotalPages: totalPages,
            HasPreviousPage: pageIndex > 1,
            HasNextPage: pageIndex < totalPages);
    }
}
