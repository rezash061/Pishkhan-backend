using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Infrastructure.Security;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Sms;

/// <summary>
/// One SMS the platform sent or tried to send (admin plan AD-2.4). Never the text: OTPs and invitation links are secrets. The phone is kept
/// masked plus a blind index so staff can find a number's messages. Kept 90 days.
/// </summary>
public sealed class SmsLogEntry
{
    public const string Sent = "sent";
    public const string Failed = "failed";

    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string Purpose { get; set; } = SmsPurposes.Other;

    public Guid? WorkspaceId { get; set; }

    public string PhoneMasked { get; set; } = string.Empty;

    public string PhoneBidx { get; set; } = string.Empty;

    public string Provider { get; set; } = "dev";

    public string Status { get; set; } = Sent;

    public string? Error { get; set; }

    /// <summary>SMS parts the text needs (70 Persian characters each, 67 when split), 1 for a template.</summary>
    public int Segments { get; set; }

    public string? Template { get; set; }

    public const string BlindIndexPurpose = "sms-log";

    public static int SegmentsOf(SmsMessage message) =>
        message.Template is not null && message.TemplateTokens is { Count: > 0 } ? 1 : message.Text.Length <= 70 ? 1 : (message.Text.Length + 66) / 67;
}

/// <summary>Sends through the provider and writes one row to <c>platform.sms_log</c>; a failed log write never fails the send.</summary>
public sealed class LoggingSmsSender(ISmsSender inner, string provider, NpgsqlDataSource dataSource, IBlindIndexer indexer, IClock clock, ILogger<LoggingSmsSender> logger)
    : ISmsSender
{
    private const string Insert =
        "INSERT INTO platform.sms_log (id, created_at, purpose, workspace_id, phone_masked, phone_bidx, provider, status, error, segments, template) " +
        "VALUES (@Id, @CreatedAt, @Purpose, @WorkspaceId, @PhoneMasked, @PhoneBidx, @Provider, @Status, @Error, @Segments, @Template)";

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var entry = new SmsLogEntry
        {
            Id = Uuid.NewV7(now),
            CreatedAt = now,
            Purpose = message.Purpose,
            WorkspaceId = message.WorkspaceId,
            PhoneMasked = message.To.Masked,
            PhoneBidx = indexer.Compute(message.To.E164, SmsLogEntry.BlindIndexPurpose),
            Provider = provider,
            Segments = SmsLogEntry.SegmentsOf(message),
            Template = message.Template is not null && message.TemplateTokens is { Count: > 0 } ? message.Template : null,
        };
        try
        {
            await inner.SendAsync(message, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            entry.Status = SmsLogEntry.Failed;
            entry.Error = e.Message.Length > 300 ? e.Message[..300] : e.Message;
            await WriteAsync(entry);
            throw;
        }

        await WriteAsync(entry);
    }

    private async Task WriteAsync(SmsLogEntry entry)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
            await connection.ExecuteAsync(Insert, entry);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(e, "Could not write the SMS log entry {Id}", entry.Id);
        }
    }
}
