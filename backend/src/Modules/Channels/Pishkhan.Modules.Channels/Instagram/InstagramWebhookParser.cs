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
            if (message.TryGetProperty("reply_to", out var replyTo) && replyTo.TryGetProperty("story", out var story))
            {
                storyId = String(story, "id");
            }

            var quickReply = message.TryGetProperty("quick_reply", out var qr) ? String(qr, "payload") : null;
            var text = String(message, "text");
            var kind = storyId is not null ? InboundKinds.StoryReply : attachments.Count > 0 ? InboundKinds.Attachment : quickReply is not null ? InboundKinds.QuickReply : InboundKinds.Text;
            return new InboundChannelMessage(sender, recipient, mid, sentAt, kind, text, attachments, storyId, quickReply, Bool(message, "is_echo"));
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
}
