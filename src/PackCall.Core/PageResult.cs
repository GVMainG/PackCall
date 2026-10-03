namespace PackCall.Core
{
    public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount);
}
