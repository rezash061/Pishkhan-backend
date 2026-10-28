using System.Reflection;

namespace Pishkhan.Contracts;

public sealed record TopicDefinition(string Name, int Partitions, TimeSpan Retention, string KeyDescription);

/// <summary>
/// The single list of Kafka topics (backend plan section 6.2). The Migrator creates them; dev hosts may create them if missing.
/// Naming: <c>&lt;module&gt;.&lt;event&gt;.v&lt;version&gt;</c>.
/// </summary>
public static class Topics
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan TwoWeeks = TimeSpan.FromDays(14);

    public const string InstagramWebhookReceived = "channels.instagram-webhook-received.v1";
    public const string InboundMessageReceived = "channels.inbound-message-received.v1";
    public const string InboundCommentReceived = "channels.inbound-comment-received.v1";
    public const string ChannelStatusChanged = "channels.channel-status-changed.v1";
    public const string ReplyRequested = "conversations.reply-requested.v1";
    public const string OutboundMessageRequested = "conversations.outbound-message-requested.v1";
    public const string OutboundMessageCompleted = "conversations.outbound-message-completed.v1";
    public const string HandoffRequested = "conversations.handoff-requested.v1";
    public const string FileScanRequested = "files.scan-requested.v1";
    public const string FileScanCompleted = "files.scan-completed.v1";
    public const string KnowledgeSourceChanged = "knowledge.source-changed.v1";
    public const string ProductChanged = "catalog.product-changed.v1";
    public const string PricesUpdated = "catalog.prices-updated.v1";
    public const string OrderCreated = "orders.order-created.v1";
    public const string OrderStatusChanged = "orders.order-status-changed.v1";
    public const string PaymentConfirmed = "payments.payment-confirmed.v1";
    public const string ReceiptSubmitted = "payments.receipt-submitted.v1";
    public const string ReceiptVerified = "payments.receipt-verified.v1";
    public const string UsageRecorded = "billing.usage-recorded.v1";
    public const string UserRegistered = "identity.user-registered.v1";
    public const string WorkspaceChanged = "tenancy.workspace-changed.v1";
    public const string MembershipChanged = "tenancy.membership-changed.v1";
    public const string NotificationRequested = "notifications.notification-requested.v1";
    public const string AuditEntryRecorded = "audit.entry-recorded.v1";
    public const string AnalyticsEventRecorded = "analytics.event-recorded.v1";
    public const string CacheInvalidated = "platform.cache-invalidated.v1";
    public const string TimerFired = "platform.timer-fired.v1";

    public static string Dlq(string consumerGroup) => consumerGroup + ".dlq";

    public static readonly IReadOnlyList<string> ConsumerGroups = ["ingest", "engine", "ai", "outbound", "media", "commerce", "platform"];

    public static readonly IReadOnlyList<TopicDefinition> All =
    [
        new(InstagramWebhookReceived, 24, TwoWeeks, "instagram account id"),
        new(InboundMessageReceived, 48, TwoWeeks, "conversation id"),
        new(InboundCommentReceived, 24, TwoWeeks, "instagram media id"),
        new(ChannelStatusChanged, 6, TwoWeeks, "channel account id"),
        new(ReplyRequested, 48, Week, "conversation id"),
        new(OutboundMessageRequested, 48, Week, "conversation id"),
        new(OutboundMessageCompleted, 48, Week, "conversation id"),
        new(HandoffRequested, 12, Week, "conversation id"),
        new(FileScanRequested, 12, Week, "file id"),
        new(FileScanCompleted, 12, Week, "file id"),
        new(KnowledgeSourceChanged, 12, Week, "workspace id"),
        new(ProductChanged, 12, Week, "workspace id"),
        new(PricesUpdated, 12, Week, "workspace id"),
        new(OrderCreated, 12, Week, "order id"),
        new(OrderStatusChanged, 12, Week, "order id"),
        new(PaymentConfirmed, 12, Week, "order id"),
        new(ReceiptSubmitted, 12, Week, "receipt id"),
        new(ReceiptVerified, 12, Week, "receipt id"),
        new(UsageRecorded, 12, Week, "workspace id"),
        new(UserRegistered, 6, Week, "user id"),
        new(WorkspaceChanged, 6, Week, "workspace id"),
        new(MembershipChanged, 6, Week, "workspace id"),
        new(NotificationRequested, 12, Week, "user id"),
        new(AuditEntryRecorded, 12, Week, "workspace id"),
        new(AnalyticsEventRecorded, 24, Week, "workspace id"),
        new(CacheInvalidated, 6, TimeSpan.FromDays(1), "cache tag"),
        new(TimerFired, 12, Week, "timer kind"),
        .. ConsumerGroups.Select(g => new TopicDefinition(Dlq(g), 6, TwoWeeks, "original key")),
    ];

    public static IntegrationEventAttribute Describe(Type eventType) =>
        eventType.GetCustomAttribute<IntegrationEventAttribute>()
        ?? throw new InvalidOperationException($"{eventType.FullName} has no [IntegrationEvent] attribute.");

    public static string TypeName(Type eventType)
    {
        var attribute = Describe(eventType);
        return attribute.Type ?? attribute.Topic[..attribute.Topic.LastIndexOf(".v", StringComparison.Ordinal)];
    }
}
