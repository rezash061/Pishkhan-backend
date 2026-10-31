using Pishkhan.Modules.Tenancy.Domain;

namespace Pishkhan.UnitTests;

public class WorkspaceTests
{
    private static readonly DateTimeOffset Asked = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deletion_is_scheduled_thirty_days_after_the_request_and_cancel_clears_it()
    {
        var workspace = Workspace.Create("گالری ترمه", "termeh", "clothing", Asked.AddDays(-100));
        workspace.DeletionScheduledFor.ShouldBeNull();

        workspace.RequestDeletion(Asked);
        Workspace.StatusName(workspace.Status).ShouldBe("pending_deletion");
        workspace.DeletionScheduledFor.ShouldBe(Asked.AddDays(30));

        workspace.CancelDeletion();
        workspace.Status.ShouldBe(WorkspaceStatus.Active);
        workspace.DeletionScheduledFor.ShouldBeNull();

        workspace.RequestDeletion(Asked);
        workspace.MarkDeleted(Asked.AddDays(30));
        Workspace.StatusName(workspace.Status).ShouldBe("deleted");
        workspace.DeletionScheduledFor.ShouldBeNull();
    }
}
