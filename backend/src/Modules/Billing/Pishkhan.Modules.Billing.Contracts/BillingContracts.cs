namespace Pishkhan.Modules.Billing.Contracts;

/// <summary>What a plan limits per billing period (backend plan 9.13). A missing or null limit means unlimited.</summary>
public static class BillingMeters
{
    public const string AiConversations = "ai_conversations";
    public const string AiMessages = "ai_messages";
    public const string ChannelAccounts = "channel_accounts";
    public const string Seats = "seats";
    public const string ReceiptVerifications = "receipt_verifications";
    public const string Sms = "sms";

    public static readonly IReadOnlyList<string> All = [AiConversations, AiMessages, ChannelAccounts, Seats, ReceiptVerifications, Sms];
}
