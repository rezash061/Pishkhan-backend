namespace Pishkhan.Web.Http;

/// <summary>One page of a keyset-paginated list. Pass <see cref="NextCursor"/> back as <c>cursor</c>; null means the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>One page of an offset-paginated list (back-office tables that show a total and page numbers). <see cref="Page"/> starts at 1.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
