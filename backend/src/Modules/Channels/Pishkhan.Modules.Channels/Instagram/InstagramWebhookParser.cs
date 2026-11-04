using System.Globalization;
using System.Text.Json;
using Pishkhan.Modules.Channels.Contracts;

namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>
/// Turns one webhook <c>entry</c> into normalized messages. Unknown shapes are skipped, never thrown on:
/// Meta adds fields and event types without notice. Reactions and deletions are ignored for now.
/// </summary>
public sealed class InstagramWebhookParser : IChannelWebhookParser
{
    // Attachment types of a post or reel the customer shared into the conversation.
    private static readonly HashSet<string> SharedTypes = new(StringComparer.Ordinal) { "share", "ig_post", "ig_reel", "reel" };

    public IReadOnlyList<InboundChannelComment> ParseInstagramComments(JsonElement entry)
    {
        if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entryTime = entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64())
            : DateTimeOffset.UtcNow;
        var result = new List<InboundChannelComment>();
        foreach (var change in changes.EnumerateArray())
        {
            var kind = String(change, "field") switch
            {
                "comments" => CommentKinds.Comment,
                "live_comments" => CommentKinds.LiveComment,
                _ => null,
            };
            if (kind is null || !change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = String(value, "id");
            var from = IdOf(value, "from");
            var media = value.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            var mediaId = media.ValueKind == JsonValueKind.Object ? String(media, "id") : null;
            if (id is null || from is null || mediaId is null)
            {
                continue;
            }

            var username = value.TryGetProperty("from", out var f) ? String(f, "username") : null;
            var createdAt = String(value, "timestamp") is { } ts && long.TryParse(ts, CultureInfo.InvariantCulture, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : entryTime;
            result.Add(new InboundChannelComment(kind, id, mediaId, String(media, "media_product_type"), String(value, "parent_id"), from, username, String(value, "text"), createdAt));
        }

        return result;
    }

    public IReadOnlyList<InboundChannelMessage> ParseInstagramEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("messaging", out var messaging) || messaging.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<InboundChannelMessage>();
        foreach (var item in messaging.EnumerateArray())
        {
            if (ParseMessaging(item) is { } message)
            {
                result.Add(message);
            }
        }

        return result;
    }

    private static InboundChannelMessage? ParseMessaging(JsonElement item)
    {
        var sender = IdOf(item, "sender");
        var recipient = IdOf(item, "recipient");
        if (sender is null || recipient is null)
        {
            return null;
        }

        var sentAt = item.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64())
            : DateTimeOffset.UtcNow;

        if (item.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (Bool(message, "is_deleted") || Bool(message, "is_unsupported") || String(message, "mid") is not { } mid)
            {
                return null;
            }

            var attachments = new List<InboundAttachment>();
            if (message.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var url = a.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object ? String(payload, "url") : null;
                    attachments.Add(new InboundAttachment(String(a, "type") ?? "unknown", url));
                }
            }

            string? storyId = null;
            string? storyUrl = null;
            if (message.TryGetProperty("reply_to", out var replyTo) && replyTo.TryGetProperty("story", out var story))
            {
                storyId = String(story, "id");
                storyUrl = String(story, "url");
            }

            var quickReply = message.TryGetProperty("quick_reply", out var qr) ? String(qr, "payload") : null;
            var text = String(message, "text");
            var kind = storyId is not null ? InboundKinds.StoryReply
                : attachments.Any(a => a.Type == "story_mention") ? InboundKinds.StoryMention
                : attachments.Any(a => SharedTypes.Contains(a.Type)) ? InboundKinds.Share
                : attachments.Count > 0 ? InboundKinds.Attachment
                : quickReply is not null ? InboundKinds.QuickReply
                : InboundKinds.Text;
            return new InboundChannelMessage(sender, recipient, mid, sentAt, kind, text, attachments, storyId, quickReply, Bool(message, "is_echo"), storyUrl);
        }

        if (item.TryGetProperty("postback", out var postback) && postback.ValueKind == JsonValueKind.Object)
        {
            var mid = String(postback, "mid") ?? $"postback:{sender}:{sentAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
            return new InboundChannelMessage(sender, recipient, mid, sentAt, InboundKinds.Postback, String(postback, "title"), [], null, String(postback, "payload"), false);
        }

        if (item.TryGetProperty("read", out var read) && read.ValueKind == JsonValueKind.Object && String(read, "mid") is { } readMid)
        {
            return new InboundChannelMessage(sender, recipient, readMid, sentAt, InboundKinds.Read, null, [], null, null, false);
        }

        return null;
    }

    private static string? IdOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? String(p, "id") : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    public InboundChannelMessage? ParseBaleUpdate(JsonElement update, string botId)
    {
        if (update.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (update.TryGetProperty("callback_query", out var callback) && callback.ValueKind == JsonValueKind.Object)
        {
            if (!callback.TryGetProperty("from", out var from) || !from.TryGetProperty("id", out var fromId)
                || !callback.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var callbackId = callback.TryGetProperty("id", out var cid) ? cid.ToString() : Guid.NewGuid().ToString("N");
            return new InboundChannelMessage(
                fromId.ToString(), botId, "cb:" + callbackId, DateTimeOffset.UtcNow, InboundKinds.Postback, data.GetString(), [], null, data.GetString(), false);
        }

        if (!update.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatId)
            || (chat.TryGetProperty("type", out var type) && type.GetString() != "private"))
        {
            return null;
        }

        var sentAt = message.TryGetProperty("date", out var date) && date.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow;
        var text = message.TryGetProperty("text", out var t) ? t.GetString() : message.TryGetProperty("caption", out var c) ? c.GetString() : null;
        var hasMedia = message.TryGetProperty("photo", out _) || message.TryGetProperty("voice", out _) || message.TryGetProperty("audio", out _)
            || message.TryGetProperty("video", out _) || message.TryGetProperty("document", out _);
        if (text is null && !hasMedia)
        {
            return null;
        }

        var messageId = message.TryGetProperty("message_id", out var mid) ? mid.ToString() : Guid.NewGuid().ToString("N");
        return new InboundChannelMessage(
            chatId.ToString(), botId, $"{chatId}:{messageId}", sentAt, text is null ? InboundKinds.Attachment : InboundKinds.Text, text, [], null, null, false);
    }
}
