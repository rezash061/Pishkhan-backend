using Pishkhan.Modules.Backoffice.Domain;
using Pishkhan.Modules.Backoffice.Features;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.SharedKernel;

namespace Pishkhan.UnitTests;

/// <summary>AD-1.6 to AD-1.8: feature flag evaluation and the quarantine's false-positive rule.</summary>
public class PlatformOpsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private static FeatureFlag Flag(bool enabled = true, int rollout = 100, params string[] plans) =>
        FeatureFlag.Create("new_inbox", null, enabled, rollout, [.. plans], Guid.NewGuid(), T0);

    [Fact]
    public void An_override_wins_over_everything_else()
    {
        var ws = Guid.NewGuid();
        FeatureFlagEvaluator.Evaluate(Flag(enabled: false), ws, null, true).ShouldBe((true, "override"));
        FeatureFlagEvaluator.Evaluate(Flag(), ws, null, false).ShouldBe((false, "override"));
    }

    [Fact]
    public void A_disabled_flag_or_another_plan_is_off()
    {
        var ws = Guid.NewGuid();
        FeatureFlagEvaluator.Evaluate(Flag(enabled: false), ws, "pro", null).ShouldBe((false, "off"));
        FeatureFlagEvaluator.Evaluate(Flag(plans: "pro"), ws, "starter", null).ShouldBe((false, "plan"));
        FeatureFlagEvaluator.Evaluate(Flag(plans: "pro"), ws, null, null).ShouldBe((false, "plan"));
        FeatureFlagEvaluator.Evaluate(Flag(plans: "pro"), ws, "pro", null).ShouldBe((true, "rollout"));
    }

    [Fact]
    public void Rollout_is_stable_and_only_grows_with_the_percentage()
    {
        var workspaces = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToList();
        var at20 = workspaces.Where(w => FeatureFlagEvaluator.Evaluate(Flag(rollout: 20), w, null, null).Effective).ToHashSet();
        var at50 = workspaces.Where(w => FeatureFlagEvaluator.Evaluate(Flag(rollout: 50), w, null, null).Effective).ToHashSet();
        at20.IsSubsetOf(at50).ShouldBeTrue();
        at20.Count.ShouldBeInRange(300, 500);
        at50.Count.ShouldBeInRange(850, 1150);
        workspaces.Count(w => FeatureFlagEvaluator.Evaluate(Flag(rollout: 0), w, null, null).Effective).ShouldBe(0);
        workspaces.All(w => FeatureFlagEvaluator.Bucket("new_inbox", w) == FeatureFlagEvaluator.Bucket("new_inbox", w)).ShouldBeTrue();
    }

    [Fact]
    public void A_false_positive_sends_the_file_back_to_scanning_instead_of_releasing_it()
    {
        var file = StoredFile.Receive(Guid.NewGuid(), Guid.NewGuid(), FilePurposes.ProductImage, "a.jpg", "image/jpeg", "image/jpeg", 10, "hash", "quarantine", null, T0);
        Should.Throw<DomainException>(() => file.MarkFalsePositive(Guid.NewGuid(), T0)).Message.ShouldBe("files.not_infected");

        file.MarkInfected("ClamAV 1.4", "Eicar", T0);
        var staff = Guid.NewGuid();
        file.MarkFalsePositive(staff, T0.AddMinutes(1));
        file.Status.ShouldBe(FileStatuses.PendingScan);
        file.FalsePositiveAt.ShouldBe(T0.AddMinutes(1));
        file.FalsePositiveBy.ShouldBe(staff);
    }

    [Fact]
    public void ClamAV_signature_age_comes_from_the_version_string()
    {
        var now = new DateTimeOffset(2026, 11, 6, 9, 31, 2, TimeSpan.Zero);
        PlatformOverviewEndpoints.SignatureAge("ClamAV 1.4.1/27450/Tue Nov  4 09:31:02 2026", now).ShouldBe(TimeSpan.FromDays(2));
        PlatformOverviewEndpoints.SignatureAge("ClamAV 1.4.1", now).ShouldBeNull();
        PlatformOverviewEndpoints.SignatureAge(null, now).ShouldBeNull();
    }

    [Fact]
    public void Staff_activation_clears_a_lockout_and_a_totp_reset_forces_enrollment()
    {
        var staff = StaffUser.Create("ops", "hash", ["Support", "Support"], T0);
        staff.Roles.ShouldBe(["Support"]);
        for (var i = 0; i < 5; i++)
        {
            staff.RecordFailure(T0);
        }

        staff.IsLocked(T0).ShouldBeTrue();
        staff.Disable();
        staff.Activate();
        staff.Status.ShouldBe(StaffStatus.Active);
        staff.IsLocked(T0).ShouldBeFalse();

        staff.SetTotpSecret("enc");
        staff.EnableTotp(10);
        staff.ResetTotp();
        staff.TotpEnabled.ShouldBeFalse();
        staff.TotpSecretEnc.ShouldBeNull();
        staff.SetTotpSecret("enc2"); // enrollment works again

        staff.SetRoles(["Finance", "Support", "Finance"]);
        staff.Roles.ShouldBe(["Finance", "Support"]);
    }
}
