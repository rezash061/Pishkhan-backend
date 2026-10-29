using System.Globalization;
using Dapper;
using Npgsql;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>
/// Monthly range partitions for high-volume tables (<c>conversations.messages</c>, <c>analytics.events</c>, <c>audit.audit_entries</c>).
/// The parent table is created by a migration with <c>PARTITION BY RANGE</c> on its timestamp column; this helper creates partitions ahead.
/// </summary>
public static class Partitioning
{
    public static readonly IReadOnlyList<string> PartitionedTables = ["conversations.messages", "audit.audit_entries"];

    public static async Task EnsureMonthlyPartitionsAsync(NpgsqlConnection connection, string qualifiedTable, DateTimeOffset from, int monthsAhead, CancellationToken cancellationToken = default)
    {
        var parts = qualifiedTable.Split('.');
        if (parts.Length != 2 || parts.Any(p => !IsIdentifier(p)))
        {
            throw new ArgumentException("Expected schema.table", nameof(qualifiedTable));
        }

        var start = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= monthsAhead; i++)
        {
            var lower = start.AddMonths(i);
            var upper = lower.AddMonths(1);
            var name = $"{parts[1]}_{lower.ToString("yyyy_MM", CultureInfo.InvariantCulture)}";
            var sql = $"""
                CREATE TABLE IF NOT EXISTS {parts[0]}.{name} PARTITION OF {parts[0]}.{parts[1]}
                FOR VALUES FROM ('{lower:yyyy-MM-dd}') TO ('{upper:yyyy-MM-dd}');
                """;
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        }
    }

    private static bool IsIdentifier(string s) => s.Length is > 0 and < 64 && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}
