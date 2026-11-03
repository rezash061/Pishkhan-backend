using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>
/// Voice notes (BE-2.4, gap G15): downloaded from Instagram's CDN into the Files module, scanned like every file, transcribed, and then
/// announced again as a text message so rules, flows and the AI answer them. When the audio cannot be fetched, is not clean, or no
/// speech model answers, the conversation is handed to a person: a voice note is never left unanswered in silence.
/// </summary>
internal static class VoiceNotes
{
    /// <summary>Instagram voice notes are at most a minute long, a few hundred kilobytes; 25 MB is the transcription endpoints' limit.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    public const string HandoffReason = "voice_unavailable";

    /// <summary>The first audio attachment with a URL, or null.</summary>
    public static Uri? AudioUrl(IReadOnlyList<InboundAttachment> attachments) =>
        attachments.FirstOrDefault(a => a.Type == AttachmentTypes.Audio && a.Url is not null) is { Url: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : null;

    /// <summary>Records the failure and hands the conversation to a person, with the message announced again for the inbox.</summary>
    public static async Task FailAsync(ConversationsDbContext db, Message message, DateTimeOffset now, CancellationToken ct)
    {
        message.TranscriptFailed(now);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
        if (conversation is null)
        {
            return;
        }

        db.Publish(
            new ConversationActionsRequested(
                conversation.ChannelAccountId, conversation.ChannelKind, conversation.ExternalUserId, HandledBy.Rule, null, null, message.Id, message.CreatedAt,
                [new ConversationAction(ConversationActionKinds.Handoff, Reason: HandoffReason)]),
            ConversationActionsRequested.Key(conversation.ChannelAccountId, conversation.ExternalUserId));
        Announce(db, conversation, message, transcript: null);
    }

    /// <summary>
    /// Announces the message again. With a transcript it is a text message for the engine; without one it only refreshes the inbox
    /// (still <c>AwaitingTranscript</c>, so nothing answers it). <c>IsRepeat</c> keeps it out of the message counts.
    /// </summary>
    public static void Announce(ConversationsDbContext db, Conversation conversation, Message message, string? transcript) =>
        db.Publish(
            new InboundMessageReceived(
                conversation.Id, message.Id, message.CreatedAt, conversation.ChannelAccountId, conversation.ChannelKind, conversation.ContactId,
                transcript is null ? message.Kind : InboundKinds.Text, transcript, transcript is null ? null : PersianNormalizer.Normalize(transcript), 1,
                ExternalUserId: conversation.ExternalUserId, AwaitingTranscript: transcript is null, IsRepeat: true),
            conversation.Id.ToString());
}

/// <summary>Outbound group: copies the voice note into the Files module, which scans it; transcription waits for the scan.</summary>
internal sealed class VoiceNoteHandler(ConversationsDbContext db, SafeHttpClient http, IFileService files, IClock clock, ILogger<VoiceNoteHandler> logger)
    : IdempotentHandler<VoiceNoteReceived, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(VoiceNoteReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var stored = await Db.Messages.FirstOrDefaultAsync(m => m.Id == message.MessageId && m.CreatedAt == message.MessageCreatedAt, cancellationToken);
        if (stored is not { TranscriptStatus: TranscriptStatuses.Pending } || stored.MediaFileId is not null)
        {
            return;
        }

        try
        {
            var download = await http.DownloadAsync(message.AudioUrl, VoiceNotes.MaxBytes, SafeHttpClient.MetaCdnSuffixes, cancellationToken: cancellationToken);
            using (download.Content)
            {
                stored.MediaFileId = await files.IngestAsync(message.WorkspaceId, FilePurposes.MessageAttachment, download.Content, download.ContentType, "voice-note", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is SafeHttpException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Voice note {MessageId} not downloaded: {Error}", stored.Id, ex.Message);
            await VoiceNotes.FailAsync(Db, stored, clock.UtcNow, cancellationToken);
        }
    }
}

/// <summary>Outbound group: a scanned voice note is transcribed (if clean) and announced as text; otherwise the conversation is handed off.</summary>
internal sealed class VoiceNoteScanHandler(
    ConversationsDbContext db, IFileService files, IObjectStorage storage, ISpeechTranscriber transcriber, IClock clock, ILogger<VoiceNoteScanHandler> logger)
    : IdempotentHandler<FileScanCompleted, ConversationsDbContext>(db)
{
    protected override async Task HandleOnceAsync(FileScanCompleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (message.Purpose != FilePurposes.MessageAttachment)
        {
            return;
        }

        var stored = await Db.Messages.FirstOrDefaultAsync(m => m.MediaFileId == message.FileId && m.TranscriptStatus == TranscriptStatuses.Pending, cancellationToken);
        if (stored is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var file = message.Status == FileStatuses.Clean ? await files.GetCleanAsync(message.FileId, cancellationToken) : null;
        if (file is null)
        {
            logger.LogWarning("Voice note {MessageId} not transcribed: the file is {Status}", stored.Id, message.Status);
            await VoiceNotes.FailAsync(Db, stored, now, cancellationToken);
            return;
        }

        string text;
        try
        {
            await using var audio = await storage.OpenReadAsync(file.Bucket, file.StorageKey, cancellationToken);
            text = (await transcriber.TranscribeAsync(audio, "voice" + Extension(file.ContentType), message.WorkspaceId, cancellationToken)).Text;
        }
        catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException)
        {
            logger.LogWarning("Voice note {MessageId} not transcribed: {Error}", stored.Id, ex.Message);
            await VoiceNotes.FailAsync(Db, stored, now, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Silence or noise: a person listens rather than the AI guessing.
            await VoiceNotes.FailAsync(Db, stored, now, cancellationToken);
            return;
        }

        stored.Transcribed(text, now);
        var conversation = await Db.Conversations.FirstOrDefaultAsync(c => c.Id == stored.ConversationId, cancellationToken);
        if (conversation is not null)
        {
            VoiceNotes.Announce(Db, conversation, stored, stored.Transcript);
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        "audio/ogg" => ".ogg",
        "audio/mpeg" => ".mp3",
        "audio/aac" => ".aac",
        _ => ".m4a",
    };
}
