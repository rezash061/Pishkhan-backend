namespace Pishkhan.Modules.Tenancy.Contracts;

/// <summary>Permission names checked by endpoint filters (backend plan 3.4). Roles map to sets of these.</summary>
public static class Permissions
{
    public const string SettingsRead = "workspace.settings.read";
    public const string SettingsWrite = "workspace.settings.write";
    public const string MembersRead = "workspace.members.read";
    public const string MembersManage = "workspace.members.manage";
    public const string BillingManage = "workspace.billing.manage";
    public const string ChannelsManage = "workspace.channels.manage";
    public const string InboxRead = "workspace.inbox.read";
    public const string InboxReply = "workspace.inbox.reply";
    public const string AutomationWrite = "workspace.automation.write";
    public const string AgentWrite = "workspace.agent.write";
    public const string KnowledgeWrite = "workspace.knowledge.write";
    public const string CatalogRead = "workspace.catalog.read";
    public const string CatalogWrite = "workspace.catalog.write";
    public const string CrmRead = "workspace.crm.read";
    public const string CrmWrite = "workspace.crm.write";
    public const string FilesUpload = "workspace.files.upload";
    public const string FilesRead = "workspace.files.read";
    public const string AnalyticsRead = "workspace.analytics.read";
    public const string WorkspaceDelete = "workspace.delete";

    public static readonly IReadOnlyList<string> All =
    [
        SettingsRead, SettingsWrite, MembersRead, MembersManage, BillingManage, ChannelsManage, InboxRead, InboxReply,
        AutomationWrite, AgentWrite, KnowledgeWrite, CatalogRead, CatalogWrite, CrmRead, CrmWrite, FilesUpload, FilesRead,
        AnalyticsRead, WorkspaceDelete,
    ];
}

public static class WorkspaceRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Agent = "agent";
    public const string Viewer = "viewer";

    public static readonly IReadOnlyList<string> All = [Owner, Admin, Agent, Viewer];

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Sets = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        [Owner] = Permissions.All.ToHashSet(StringComparer.Ordinal),
        [Admin] = Permissions.All.Except([Permissions.BillingManage, Permissions.WorkspaceDelete]).ToHashSet(StringComparer.Ordinal),
        [Agent] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.SettingsRead, Permissions.InboxRead, Permissions.InboxReply, Permissions.CatalogRead, Permissions.CrmRead,
            Permissions.CrmWrite, Permissions.FilesUpload, Permissions.FilesRead, Permissions.MembersRead,
        },
        [Viewer] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.SettingsRead, Permissions.InboxRead, Permissions.CatalogRead, Permissions.CrmRead, Permissions.FilesRead,
            Permissions.MembersRead, Permissions.AnalyticsRead,
        },
    };

    public static bool IsValid(string role) => Sets.ContainsKey(role);

    public static IReadOnlySet<string> PermissionsOf(string role) => Sets.TryGetValue(role, out var set) ? set : new HashSet<string>();
}
