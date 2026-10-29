namespace Pishkhan.Web.Http;

/// <summary>One page of a keyset-paginated list. Pass <see cref="NextCursor"/> back as <c>cursor</c>; null means the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Opaque keyset cursor: base64url of "unix-microseconds:guid".</summary>
public static class Cursor
{
    public static string Write(DateTimeOffset at, Guid id) =>
        Infrastructure.Storage.Base64Url.Encode(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{at.ToUnixTimeMilliseconds() * 1000 + at.Ticks / 10 % 1000}:{id:N}")));

    public static bool TryRead(string? cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var parts = System.Text.Encoding.ASCII.GetString(Infrastructure.Storage.Base64Url.Decode(cursor)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var micros) || !Guid.TryParse(parts[1], out id))
            {
                return false;
            }

            at = DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>One page of an offset-paginated list (back-office tables that show a total and page numbers). <see cref="Page"/> starts at 1.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
