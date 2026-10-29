namespace Pishkhan.Web.Http;

/// <summary>One page of a keyset-paginated list. Pass <see cref="NextCursor"/> back as <c>cursor</c>; null means the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
