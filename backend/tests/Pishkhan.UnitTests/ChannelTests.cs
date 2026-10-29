using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;

namespace Pishkhan.UnitTests;

public class ChannelStatusMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Warning = TimeSpan.FromDays(3);

    private static ChannelAccount Connected(DateTimeOffset connectedAt, TimeSpan tokenLifetime) =>
        ChannelAccount.ConnectInstagram(Guid.NewGuid(), "17841400000000001", "shop", "Shop", "enc", connectedAt.Add(tokenLifetime), ["instagram_business_basic"], Guid.NewGuid(), connectedAt);

    [Theory]
    [InlineData(ChannelStatuses.Connecting, ChannelStatuses.Active, true)]
    [InlineData(ChannelStatuses.Active, ChannelStatuses.TokenExpiring, true)]
    [InlineData(ChannelStatuses.TokenExpiring, ChannelStatuses.ReauthRequired, true)]
    [InlineData(ChannelStatuses.TokenExpiring, ChannelStatuses.Active, true)]
    [InlineData(ChannelStatuses.ReauthRequired, ChannelStatuses.Active, true)]
    [InlineData(ChannelStatuses.ReauthRequired, ChannelStatuses.Disconnected, true)]
    [InlineData(ChannelStatuses.ReauthRequired, ChannelStatuses.TokenExpiring, false)]
    [InlineData(ChannelStatuses.Disconnected, ChannelStatuses.TokenExpiring, false)]
    [InlineData(ChannelStatuses.Disconnected, ChannelStatuses.ReauthRequired, false)]
    [InlineData(ChannelStatuses.Active, ChannelStatuses.Active, false)]
    public void Only_planned_transitions_are_allowed(string from, string to, bool allowed) =>
        ChannelAccount.CanMove(from, to).ShouldBe(allowed);

    [Fact]
    public void Connecting_activates_the_account_and_records_the_reason()
    {
        var account = Connected(Now, TimeSpan.FromDays(60));

        account.Status.ShouldBe(ChannelStatuses.Active);
        account.StatusReason.ShouldBe("connected");
        account.StatusChangedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_token_is_due_inside_the_window_and_only_after_24_hours()
    {
        var fresh = Connected(Now.AddHours(-2), TimeSpan.FromDays(5));
        fresh.IsDueForRefresh(Now, TimeSpan.FromDays(10)).ShouldBeFalse();

        var old = Connected(Now.AddDays(-55), TimeSpan.FromDays(60));
        old.IsDueForRefresh(Now, TimeSpan.FromDays(10)).ShouldBeTrue();
        old.IsDueForRefresh(Now, TimeSpan.FromDays(4)).ShouldBeFalse();
    }

    [Fact]
    public void Transient_failures_warn_only_near_expiry_and_a_refresh_recovers()
    {
        var account = Connected(Now.AddDays(-55), TimeSpan.FromDays(60));

        account.TokenRefreshFailed("instagram.transient", Now, Warning).ShouldBeFalse();
        account.Status.ShouldBe(ChannelStatuses.Active);
        account.RefreshFailures.ShouldBe(1);

        account.TokenRefreshFailed("instagram.transient", Now.AddDays(3), Warning).ShouldBeTrue();
        account.Status.ShouldBe(ChannelStatuses.TokenExpiring);

        account.TokenRefreshed("enc2", Now.AddDays(63), Now.AddDays(3)).ShouldBeTrue();
        account.Status.ShouldBe(ChannelStatuses.Active);
        account.RefreshFailures.ShouldBe(0);
        account.LastRefreshError.ShouldBeNull();
        account.TokenEnc.ShouldBe("enc2");
    }

    [Fact]
    public void An_expired_token_requires_reauth_and_disconnect_erases_the_token()
    {
        var account = Connected(Now.AddDays(-61), TimeSpan.FromDays(60));

        account.TokenRefreshFailed("instagram.transient", Now, Warning).ShouldBeTrue();
        account.Status.ShouldBe(ChannelStatuses.ReauthRequired);
        account.StatusReason.ShouldBe("token_expired");
        account.RequireReauth("again", Now).ShouldBeFalse();

        account.Disconnect("disconnected_by_user", Now).ShouldBeTrue();
        account.TokenEnc.ShouldBeNull();
        account.Disconnect("again", Now).ShouldBeFalse();
    }

    [Fact]
    public void Data_deletion_completes_once_every_module_confirmed_and_ignores_redelivery()
    {
        var request = new DataDeletionRequest { ConfirmationCode = "c", ExternalUserId = "u" };

        request.ModuleCompleted("conversations", 7, Now).ShouldBeTrue();
        request.ModuleCompleted("conversations", 7, Now).ShouldBeFalse();
        request.Status.ShouldBe(DataDeletionStatuses.Processing);

        request.ModuleCompleted("automation", 2, Now).ShouldBeTrue();
        request.Status.ShouldBe(DataDeletionStatuses.Completed);
        request.ItemsDeleted.ShouldBe(9);
    }
}

public class InstagramMessageBuilderTests
{
    [Fact]
    public void Text_with_quick_replies()
    {
        var body = InstagramMessageBuilder.Build(OutboundRecipient.User("u1"), new OutboundContent("سایز؟", QuickReplies: [new QuickReply("S", "SIZE_S"), new QuickReply("M", "SIZE_M")]));

        body.ToJsonString(InstagramMessageBuilder.WireJson).ShouldBe("""{"recipient":{"id":"u1"},"message":{"text":"سایز؟","quick_replies":[{"content_type":"text","title":"S","payload":"SIZE_S"},{"content_type":"text","title":"M","payload":"SIZE_M"}]}}""");
    }

    [Fact]
    public void Private_reply_goes_to_the_comment()
    {
        var body = InstagramMessageBuilder.Build(OutboundRecipient.Comment("c9"), new OutboundContent("قیمت را دایرکت کردیم"));

        body["recipient"]!.ToJsonString().ShouldBe("""{"comment_id":"c9"}""");
    }

    [Fact]
    public void Buttons_become_a_button_template_capped_at_three()
    {
        var buttons = Enumerable.Range(1, 5).Select(i => MessageButton.Postback($"b{i}", $"P{i}")).Append(MessageButton.Link("site", new Uri("https://shop.example/x"))).ToList();
        var body = InstagramMessageBuilder.Build(OutboundRecipient.User("u1"), new OutboundContent("انتخاب کنید", Buttons: buttons));

        var payload = body["message"]!["attachment"]!["payload"]!;
        payload["template_type"]!.GetValue<string>().ShouldBe("button");
        payload["buttons"]!.AsArray().Count.ShouldBe(InstagramMessageBuilder.MaxButtons);
    }

    [Fact]
    public void Cards_become_a_generic_template_with_default_action()
    {
        var card = new MessageCard("مانتو کتان", "۸۹۰٬۰۰۰ تومان", new Uri("https://cdn.example/1.jpg"), new Uri("https://shop.example/p/1"), [MessageButton.Postback("خرید", "BUY_1")]);
        var body = InstagramMessageBuilder.Build(OutboundRecipient.User("u1"), new OutboundContent(null, Cards: [card]));

        var element = body["message"]!["attachment"]!["payload"]!["elements"]![0]!;
        element["title"]!.GetValue<string>().ShouldBe("مانتو کتان");
        element["image_url"]!.GetValue<string>().ShouldBe("https://cdn.example/1.jpg");
        element["default_action"]!["url"]!.GetValue<string>().ShouldBe("https://shop.example/p/1");
        element["buttons"]![0]!["payload"]!.GetValue<string>().ShouldBe("BUY_1");
    }

    [Fact]
    public void Attachments_and_text_limits()
    {
        var image = InstagramMessageBuilder.Build(OutboundRecipient.User("u1"), new OutboundContent(null, new OutboundAttachment(AttachmentTypes.Image, new Uri("https://cdn.example/a.jpg"))));
        image["message"]!.ToJsonString().ShouldBe("""{"attachment":{"type":"image","payload":{"url":"https://cdn.example/a.jpg"}}}""");

        var longText = InstagramMessageBuilder.Build(OutboundRecipient.User("u1"), new OutboundContent(new string('x', 1500)));
        longText["message"]!["text"]!.GetValue<string>().Length.ShouldBe(InstagramMessageBuilder.MaxTextLength);
        new OutboundContent("  ").IsEmpty.ShouldBeTrue();
    }
}

public class InstagramGraphClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (InstagramGraphClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        var handler = new StubHandler(respond);
        return (new InstagramGraphClient(new HttpClient(handler), Options.Create(new InstagramOptions { AppSecret = "s" })), handler);
    }

    [Fact]
    public async Task Media_page_reads_items_and_the_next_cursor()
    {
        var (client, handler) = Create(_ => (HttpStatusCode.OK, """
            {"data":[{"id":"m1","caption":"تخفیف","media_type":"IMAGE","media_url":"https://cdn/1.jpg","permalink":"https://instagram.com/p/1","timestamp":"2026-10-20T08:00:00+0000"}],
             "paging":{"cursors":{"before":"b","after":"a1"},"next":"https://graph.instagram.com/next"}}
            """));

        var page = await client.ListMediaAsync("tok", "a0", 100, TestContext.Current.CancellationToken);

        page.Items.Single().Caption.ShouldBe("تخفیف");
        page.Items.Single().Timestamp.ShouldBe(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        page.NextCursor.ShouldBe("a1");
        var url = handler.Requests.Single().RequestUri!.ToString();
        url.ShouldContain("limit=50");
        url.ShouldContain("after=a0");
        handler.Requests.Single().Headers.Authorization!.Parameter.ShouldBe("tok");
    }

    [Fact]
    public async Task Last_media_page_has_no_cursor()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"data":[],"paging":{"cursors":{"before":"b","after":"a1"}}}"""));

        (await client.ListMediaAsync("tok", null, 10, TestContext.Current.CancellationToken)).NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task User_profile_reads_follow_flags()
    {
        var (client, _) = Create(_ => (HttpStatusCode.OK, """{"name":"Sara","username":"sara.k","profile_pic":"https://cdn/p.jpg","follower_count":120,"is_user_follow_business":true,"is_business_follow_user":false}"""));

        var profile = await client.GetUserProfileAsync("tok", "u1", TestContext.Current.CancellationToken);

        profile.FollowsBusiness.ShouldBe(true);
        profile.BusinessFollowsUser.ShouldBe(false);
        profile.FollowerCount.ShouldBe(120);
    }

    [Fact]
    public async Task Send_posts_the_built_body_and_errors_are_typed()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath.EndsWith("/replies", StringComparison.Ordinal)
            ? (HttpStatusCode.BadRequest, """{"error":{"code":190,"message":"expired"}}""")
            : (HttpStatusCode.OK, """{"recipient_id":"u1","message_id":"mid.1"}"""));

        var id = await client.SendAsync("tok", new JsonObject { ["recipient"] = new JsonObject { ["id"] = "u1" } }, TestContext.Current.CancellationToken);
        id.ShouldBe("mid.1");
        handler.Bodies[0].ShouldBe("""{"recipient":{"id":"u1"}}""");

        var ex = await Should.ThrowAsync<InstagramApiException>(() => client.ReplyToCommentAsync("tok", "c1", "ممنون", TestContext.Current.CancellationToken));
        ex.Kind.ShouldBe(InstagramErrorKind.InvalidToken);
    }
}
