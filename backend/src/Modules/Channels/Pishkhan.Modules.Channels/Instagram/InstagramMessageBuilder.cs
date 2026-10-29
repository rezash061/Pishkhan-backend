using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Pishkhan.Modules.Channels.Contracts;

namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>Builds the Send API request body (<c>POST /me/messages</c>) from channel-neutral content. Limits are Instagram's.</summary>
public static class InstagramMessageBuilder
{
    public const int MaxTextLength = 1000;
    public const int MaxQuickReplies = 13;
    public const int MaxButtons = 3;
    public const int MaxCards = 10;
    public const int MaxTitleLength = 80;
    public const int MaxButtonTitleLength = 20;

    /// <summary>Writes Persian as UTF-8 instead of <c>\uXXXX</c> escapes (still escapes HTML-sensitive characters).</summary>
    public static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static JsonObject Build(OutboundRecipient recipient, OutboundContent content)
    {
        var to = recipient.CommentId is { Length: > 0 } commentId
            ? new JsonObject { ["comment_id"] = commentId }
            : new JsonObject { ["id"] = recipient.UserId ?? throw new ArgumentException("A recipient needs a user id or a comment id.", nameof(recipient)) };
        var body = new JsonObject { ["recipient"] = to, ["message"] = Message(content) };
        if (content.Tag is { Length: > 0 } tag)
        {
            body["messaging_type"] = "MESSAGE_TAG";
            body["tag"] = tag;
        }

        return body;
    }

    private static JsonObject Message(OutboundContent content)
    {
        if (content.Cards is { Count: > 0 } cards)
        {
            return Template(new JsonObject
            {
                ["template_type"] = "generic",
                ["elements"] = new JsonArray([.. cards.Take(MaxCards).Select(Card)]),
            });
        }

        if (content.Attachment is { } attachment)
        {
            return new JsonObject
            {
                ["attachment"] = new JsonObject
                {
                    ["type"] = attachment.Type,
                    ["payload"] = new JsonObject { ["url"] = attachment.Url.ToString() },
                },
            };
        }

        var text = Clip(content.Text ?? string.Empty, MaxTextLength);
        JsonObject message = content.Buttons is { Count: > 0 } buttons
            ? Template(new JsonObject
            {
                ["template_type"] = "button",
                ["text"] = Clip(text, 640),
                ["buttons"] = Buttons(buttons),
            })
            : new JsonObject { ["text"] = text };

        if (content.QuickReplies is { Count: > 0 } quickReplies)
        {
            message["quick_replies"] = new JsonArray([.. quickReplies.Take(MaxQuickReplies).Select(q => (JsonNode)new JsonObject
            {
                ["content_type"] = "text",
                ["title"] = Clip(q.Title, MaxButtonTitleLength),
                ["payload"] = q.Payload,
            })]);
        }

        return message;
    }

    private static JsonObject Template(JsonObject payload) =>
        new() { ["attachment"] = new JsonObject { ["type"] = "template", ["payload"] = payload } };

    private static JsonNode Card(MessageCard card)
    {
        var element = new JsonObject { ["title"] = Clip(card.Title, MaxTitleLength) };
        if (card.Subtitle is { Length: > 0 } subtitle)
        {
            element["subtitle"] = Clip(subtitle, MaxTitleLength);
        }

        if (card.ImageUrl is { } image)
        {
            element["image_url"] = image.ToString();
        }

        if (card.DefaultUrl is { } url)
        {
            element["default_action"] = new JsonObject { ["type"] = "web_url", ["url"] = url.ToString() };
        }

        if (card.Buttons is { Count: > 0 } buttons)
        {
            element["buttons"] = Buttons(buttons);
        }

        return element;
    }

    private static JsonArray Buttons(IReadOnlyList<MessageButton> buttons) =>
        new([.. buttons.Take(MaxButtons).Select(b => (JsonNode)(b.Type == "web_url"
            ? new JsonObject { ["type"] = "web_url", ["title"] = Clip(b.Title, MaxButtonTitleLength), ["url"] = b.Url?.ToString() }
            : new JsonObject { ["type"] = "postback", ["title"] = Clip(b.Title, MaxButtonTitleLength), ["payload"] = b.Payload }))]);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
