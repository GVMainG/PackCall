namespace PackCall.Api.Models;

/// <summary>Страница результатов с общим количеством элементов и страниц.</summary>
public sealed record PageResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages);
