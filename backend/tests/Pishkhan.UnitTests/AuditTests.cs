using System.Text.Json;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Audit.Domain;
using Pishkhan.Modules.Audit.Features;

namespace Pishkhan.UnitTests;

public class AuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("password", true)]
    [InlineData("newPassword", true)]
    [InlineData("access_token", true)]
    [InlineData("TotpSecretEnc", true)]
    [InlineData("phone", true)]
    [InlineData("phoneE164", true)]
    [InlineData("otp", true)]
    [InlineData("code", true)]
    [InlineData("cardNumber", true)]
    [InlineData("sheba", true)]
    [InlineData("name", false)]
    [InlineData("roles", false)]
    [InlineData("errorCode", false)]
    [InlineData("company", false)]
    public void Sensitive_keys_are_recognized(string key, bool sensitive) => AuditEntries.IsSensitive(key).ShouldBe(sensitive);

    [Fact]
    public void Snapshots_redact_secrets_at_any_depth()
    {
        var snapshot = AuditEntries.Snapshot(new
        {
            Username = "ali",
            Password = "hunter2",
            Settings = new { ApiKey = "k-123", Limit = 5 },
            Contacts = new[] { new { Name = "x", Phone = "+989121234567" } },
            RefreshToken = (string?)null,
        })!.Value;

        snapshot.GetProperty("username").GetString().ShouldBe("ali");
        snapshot.GetProperty("password").GetString().ShouldBe(AuditEntries.Redacted);
        snapshot.GetProperty("settings").GetProperty("apiKey").GetString().ShouldBe(AuditEntries.Redacted);
        snapshot.GetProperty("settings").GetProperty("limit").GetInt32().ShouldBe(5);
        snapshot.GetProperty("contacts")[0].GetProperty("phone").GetString().ShouldBe(AuditEntries.Redacted);
        snapshot.GetRawText().ShouldNotContain("hunter2");
        snapshot.GetRawText().ShouldNotContain("1234567");
    }

    [Fact]
    public void An_impersonating_staff_member_is_the_actor_on_behalf_of_the_user()
    {
        var user = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var entry = AuditEntries.Create(AuditActor.User(user, staff, "192.0.2.1"), "workspace.update", "workspace", "w", Guid.NewGuid(), Now, reason: new string('x', 600));

        entry.ActorType.ShouldBe(AuditActorTypes.Staff);
        entry.ActorId.ShouldBe(staff);
        entry.OnBehalfOf.ShouldBe(user);
        entry.Reason!.Length.ShouldBe(500);
        entry.PartitionKey().ShouldBe(entry.WorkspaceId.ToString());
        AuditEntries.Create(AuditActor.System, "x", "y", null, null, Now).PartitionKey().ShouldBe("platform");
    }

    [Fact]
    public void The_workspace_view_hides_which_staff_member_acted()
    {
        var entry = new AuditEntry
        {
            Id = Guid.NewGuid(), OccurredAt = Now, ActorType = AuditActorTypes.Staff, ActorId = Guid.NewGuid(), ActorName = "support2",
            Action = "workspace.update", EntityType = "workspace", Ip = "192.0.2.7", AfterJson = """{"name":"a"}""",
        };

        var masked = AuditEndpoints.ToResponse(entry, maskStaff: true);
        masked.ActorName.ShouldBe(AuditEndpoints.SupportName);
        masked.ActorId.ShouldBeNull();
        masked.Ip.ShouldBeNull();
        masked.After!.Value.GetProperty("name").GetString().ShouldBe("a");

        var full = AuditEndpoints.ToResponse(entry, maskStaff: false);
        full.ActorName.ShouldBe("support2");
        full.Ip.ShouldBe("192.0.2.7");
    }

    [Fact]
    public void Search_filters_combine_and_support_sees_only_workspace_entries()
    {
        var ws = Guid.NewGuid();
        AuditEntry Row(string action, Guid? workspace, string actor, int minutes) => new()
        {
            Id = Guid.NewGuid(), OccurredAt = Now.AddMinutes(minutes), ActorType = AuditActorTypes.Staff, ActorName = actor, WorkspaceId = workspace,
            Action = action, EntityType = "workspace",
        };

        var rows = new[]
        {
            Row("staff.login", null, "superadmin", 0),
            Row("workspace.update", ws, "Support2", 1),
            Row("workspace.suspend", ws, "superadmin", 2),
            Row("workspace.update", Guid.NewGuid(), "support2", 3),
        }.AsQueryable();

        AuditEndpoints.Filter(rows, new AuditSearch(null, null, null, null, null, null, null, null), null, workspaceOnly: true).Count().ShouldBe(3);
        AuditEndpoints.Filter(rows, new AuditSearch(null, null, null, null, null, null, null, null), null, workspaceOnly: false).Count().ShouldBe(4);
        AuditEndpoints.Filter(rows, new AuditSearch(null, null, "WORKSPACE", null, Now.AddMinutes(1), Now.AddMinutes(2), null, null), ws, workspaceOnly: true)
            .Select(r => r.Action).ShouldBe(["workspace.update", "workspace.suspend"]);
    }
}
