using Pishkhan.Modules.Notifications.Contracts;
using Pishkhan.Modules.Notifications.Domain;

namespace Pishkhan.UnitTests;

public class NotificationTests
{
    [Fact]
    public void Preferences_fall_back_to_defaults_and_never_text_for_kinds_without_sms()
    {
        var automation = NotificationCatalog.Find(NotificationKinds.Automation);
        NotificationCatalog.Effective(automation, null).ShouldBe(new ChannelChoice(true, true, false));

        var preference = NotificationPreference.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationKinds.Automation);
        preference.Set(inApp: false, push: false, sms: true, DateTimeOffset.UtcNow);
        NotificationCatalog.Effective(automation, preference).ShouldBe(new ChannelChoice(false, false, false));

        NotificationCatalog.Find("something_new").ShouldSatisfyAllConditions(
            k => k.SmsAvailable.ShouldBeFalse(),
            k => k.Defaults.ShouldBe(new ChannelChoice(true, false, false)));
    }

    [Fact]
    public void One_sms_per_kind_every_thirty_minutes()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var log = SmsLog.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationKinds.Channel);
        log.TryClaim(now).ShouldBeTrue();
        log.TryClaim(now.AddMinutes(29)).ShouldBeFalse();
        log.TryClaim(now.AddMinutes(30)).ShouldBeTrue();
    }
}
