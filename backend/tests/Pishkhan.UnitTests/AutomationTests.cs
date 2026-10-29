using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;

namespace Pishkhan.UnitTests;

/// <summary>Domain tests listed in backend plan 9.5: keyword matching, every trigger kind, priorities, one private reply, flows.</summary>
public class KeywordMatcherTests
{
    private static TriggerConfig Keywords(string match, params string[] keywords) => new() { Match = match, Keywords = keywords };

    [Theory]
    // Arabic yeh and kaf typed on an Arabic keyboard match the Persian keyword.
    [InlineData(MatchModes.Exact, "قیمت", "قيمت")]
    [InlineData(MatchModes.Contains, "کیف", "اين كيف چنده؟")]
    // Half-space, space and no space are the same word.
    [InlineData(MatchModes.Exact, "می‌خوام", "میخوام")]
    [InlineData(MatchModes.Exact, "می‌خوام", "می خوام")]
    [InlineData(MatchModes.AnyOf, "می خوام", "سلام، میخوام سفارش بدم")]
    // Persian and Arabic-Indic digits equal ASCII digits.
    [InlineData(MatchModes.Exact, "کد 25", "کد ۲۵")]
    [InlineData(MatchModes.Contains, "25", "کد ٢٥ رو میخوام")]
    // Punctuation, emoji and letter case do not matter.
    [InlineData(MatchModes.Exact, "price", "PRICE!!! 😍")]
    [InlineData(MatchModes.Exact, "قیمت", "قیمت؟")]
    // Kashida is decorative.
    [InlineData(MatchModes.Exact, "سلام", "ســـلام")]
    public void Matches_regardless_of_letter_digit_and_spacing_variants(string match, string keyword, string text) =>
        KeywordMatcher.Matches(Keywords(match, keyword), text).ShouldBeTrue();

    [Theory]
    [InlineData(MatchModes.Exact, "قیمت", "قیمت چنده")]
    [InlineData(MatchModes.AnyOf, "کیف", "کیفیت خوبه")]
    [InlineData(MatchModes.Contains, "تخفیف", "قیمت")]
    [InlineData(MatchModes.Exact, "قیمت", "")]
    public void Does_not_match_other_text(string match, string keyword, string text) =>
        KeywordMatcher.Matches(Keywords(match, keyword), text).ShouldBeFalse();

    [Theory]
    [InlineData("قیمت*|price*", "قيمت اين چنده", true)]
    [InlineData("*ارسال*", "هزینه ارسال به شیراز؟", true)]
    [InlineData("*ارسال*", "قیمت", false)]
    [InlineData("سایز ۳۸", "سایز 38", true)]
    public void Pattern_mode_supports_wildcards_and_alternatives(string pattern, string text, bool expected) =>
        KeywordMatcher.Matches(new TriggerConfig { Match = MatchModes.Pattern, Pattern = pattern }, text).ShouldBe(expected);

    [Fact]
    public void Pattern_mode_is_not_a_regular_expression()
    {
        // Regex syntax is literal text: "(a+)+" cannot cause catastrophic backtracking and does not match "aaaa".
        KeywordMatcher.Matches(new TriggerConfig { Match = MatchModes.Pattern, Pattern = "(a+)+$" }, new string('a', 5000)).ShouldBeFalse();
        KeywordMatcher.CompilePattern("**|*").ShouldBeNull();
    }
}

public class RuleEvaluatorTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static RuleView Rule(string kind, TriggerConfig? trigger = null, int priority = 0, int ageDays = 1, Guid? account = null, string? postId = null) =>
        new(Guid.NewGuid(), account, kind, trigger ?? new TriggerConfig(), [new RuleAction { Kind = ActionKinds.SendMessage, Message = new OutboundContent("ok") }], postId, priority, T0.AddDays(-ageDays));

    private static DmInput Dm(string kind = InboundKinds.Text, string? text = null, string? payload = null, bool isNew = false) => new(Account, kind, text, payload, isNew);

    [Fact]
    public void Each_dm_trigger_kind_matches_its_event()
    {
        var keyword = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.Contains, Keywords = ["قیمت"] });
        var postback = Rule(TriggerKinds.Postback, new TriggerConfig { Payload = "BUY_1" });
        var storyReply = Rule(TriggerKinds.StoryReply);
        var mention = Rule(TriggerKinds.StoryMention);
        var share = Rule(TriggerKinds.PostShare);
        var welcome = Rule(TriggerKinds.NewConversation);
        RuleView[] all = [keyword, postback, storyReply, mention, share, welcome];

        RuleEvaluator.DmCandidates(all, Dm(text: "قيمت؟")).ShouldBe([keyword]);
        RuleEvaluator.DmCandidates(all, Dm(InboundKinds.Postback, "خرید", "BUY_1")).ShouldBe([postback]);
        RuleEvaluator.DmCandidates(all, Dm(InboundKinds.QuickReply, "خرید", "BUY_1")).ShouldBe([postback]);
        RuleEvaluator.DmCandidates(all, Dm(InboundKinds.StoryReply, "عالیه")).ShouldBe([storyReply]);
        RuleEvaluator.DmCandidates(all, Dm(InboundKinds.StoryMention)).ShouldBe([mention]);
        RuleEvaluator.DmCandidates(all, Dm(InboundKinds.Share)).ShouldBe([share]);
        RuleEvaluator.DmCandidates(all, Dm(text: "سلام", isNew: true)).ShouldBe([welcome]);
        RuleEvaluator.DmCandidates(all, Dm(text: "سلام")).ShouldBeEmpty();
        RuleEvaluator.OutsideHoursCandidates([.. all, Rule(TriggerKinds.OutsideBusinessHours)], Account).Count.ShouldBe(1);
    }

    [Fact]
    public void Each_comment_trigger_kind_matches_its_event_and_scope()
    {
        var keyword = Rule(TriggerKinds.CommentKeyword, new TriggerConfig { Match = MatchModes.AnyOf, Keywords = ["قیمت"], Scope = CommentScopes.AllPosts });
        var onPost = Rule(TriggerKinds.CommentAny, new TriggerConfig { Scope = CommentScopes.Post, PostId = "m1" }, account: Account, postId: "m1");
        var nextPost = Rule(TriggerKinds.CommentAny, new TriggerConfig { Scope = CommentScopes.NextPost }, ageDays: 0, account: Account);
        var live = Rule(TriggerKinds.LiveComment);
        RuleView[] all = [keyword, onPost, nextPost, live];

        RuleEvaluator.CommentCandidates(all, new CommentInput(Account, CommentKinds.Comment, "m1", "قيمت")).ShouldBe([keyword, onPost, nextPost]);
        RuleEvaluator.CommentCandidates(all, new CommentInput(Account, CommentKinds.Comment, "m2", "عالی")).ShouldBe([nextPost]);
        RuleEvaluator.CommentCandidates(all, new CommentInput(Account, CommentKinds.LiveComment, "live1", "سلام")).ShouldBe([live]);
        RuleEvaluator.NeedsPostBinding(nextPost).ShouldBeTrue();
        RuleEvaluator.NeedsPostBinding(nextPost with { PostId = "m3" }).ShouldBeFalse();
        RuleEvaluator.CommentCandidates([nextPost with { PostId = "m3" }], new CommentInput(Account, CommentKinds.Comment, "m2", "x")).ShouldBeEmpty();
    }

    [Fact]
    public void Priority_wins_then_specificity_then_age()
    {
        var contains = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.Contains, Keywords = ["قیمت"] }, ageDays: 10);
        var exact = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.Exact, Keywords = ["قیمت"] }, ageDays: 1);
        var important = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.Contains, Keywords = ["قیمت"] }, priority: 5, ageDays: 1);
        var olderTwin = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.Contains, Keywords = ["قیمت"] }, ageDays: 20);
        var welcome = Rule(TriggerKinds.NewConversation, ageDays: 30);

        RuleEvaluator.DmCandidates([contains, exact, important, olderTwin, welcome], Dm(text: "قیمت", isNew: true))
            .ShouldBe([important, exact, olderTwin, contains, welcome]);
    }

    [Fact]
    public void Rules_of_another_channel_account_do_not_apply()
    {
        var other = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Keywords = ["سلام"] }, account: Guid.NewGuid());
        var any = Rule(TriggerKinds.DmKeyword, new TriggerConfig { Keywords = ["سلام"] });
        RuleEvaluator.DmCandidates([other, any], Dm(text: "سلام")).ShouldBe([any]);
    }

    [Fact]
    public void Outside_hours_rule_cools_down_for_twelve_hours_by_default() =>
        Rule(TriggerKinds.OutsideBusinessHours).Cooldown.ShouldBe(TimeSpan.FromHours(12));
}

public class RuleValidatorTests
{
    private static RuleDefinition Comment(params RuleAction[] actions) =>
        new("پاسخ کامنت", Guid.NewGuid(), TriggerKinds.CommentKeyword, new TriggerConfig { Match = MatchModes.Contains, Keywords = ["قیمت"] }, actions, 0, true);

    private static RuleAction PrivateReply => new() { Kind = ActionKinds.PrivateReply, Message = new OutboundContent("قیمت رو دایرکت فرستادیم") };

    [Fact]
    public void Only_one_private_reply_per_comment_rule() =>
        RuleValidator.Validate(Comment(PrivateReply, PrivateReply)).ShouldContain(("actions", "automation.one_private_reply"));

    [Fact]
    public void Comment_rules_cannot_message_a_commenter_outside_the_private_reply()
    {
        RuleValidator.Validate(Comment(new RuleAction { Kind = ActionKinds.SendMessage, Message = new OutboundContent("x") }))
            .ShouldContain(("actions[0]", "automation.action_needs_conversation"));
        RuleValidator.Validate(Comment(PrivateReply, new RuleAction { Kind = ActionKinds.PublicReply, Replies = ["ممنون 🌸", "دایرکت رو چک کنید"] })).ShouldBeEmpty();
    }

    [Fact]
    public void Rejects_unknown_later_and_incomplete_definitions()
    {
        var dm = new RuleDefinition("x", null, TriggerKinds.DmKeyword, new TriggerConfig(), [new RuleAction { Kind = ActionKinds.SendMessage, Message = new OutboundContent("x") }], 0, true);
        RuleValidator.Validate(dm).ShouldContain(("trigger.keywords", "automation.keywords_required"));
        RuleValidator.Validate(dm with { TriggerKind = TriggerKinds.OrderStatusChanged }).ShouldContain(("triggerKind", "automation.trigger_unavailable"));
        RuleValidator.Validate(dm with { TriggerKind = "nope" }).ShouldContain(("triggerKind", "automation.trigger_unknown"));
        RuleValidator.Validate(dm with { TriggerKind = TriggerKinds.NewConversation, Actions = [new RuleAction { Kind = ActionKinds.ScheduleFollowUp }] })
            .ShouldContain(("actions[0]", "automation.action_unavailable"));
        RuleValidator.Validate(Comment(PrivateReply) with { Trigger = new TriggerConfig { Keywords = ["a"], Scope = CommentScopes.NextPost }, ChannelAccountId = null })
            .ShouldContain(("channelAccountId", "automation.channel_required"));
    }
}

public class CommentEventTests
{
    [Fact]
    public void A_comment_gets_exactly_one_private_reply()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new CommentEvent { CommentId = "c1", MediaId = "m1", FromExternalId = "u1", Kind = CommentKinds.Comment, Source = CommentSources.Webhook };
        record.ClaimPrivateReply(now).ShouldBeTrue();
        record.ClaimPrivateReply(now.AddSeconds(5)).ShouldBeFalse();
        record.PrivateReplyDueAt.ShouldBe(now);
    }

    [Fact]
    public void Private_replies_respect_the_seven_day_limit()
    {
        var now = DateTimeOffset.UtcNow;
        CommentEvent.CanPrivateReply(now.AddDays(-6), now).ShouldBeTrue();
        CommentEvent.CanPrivateReply(now.AddDays(-7).AddMinutes(-1), now).ShouldBeFalse();
    }
}

public class BusinessHoursTests
{
    private static readonly BusinessHours Shop = new(
        [
            new(DayOfWeek.Saturday, [new TimeRange(new TimeOnly(9, 0), new TimeOnly(13, 0)), new TimeRange(new TimeOnly(16, 0), new TimeOnly(21, 0))]),
            new(DayOfWeek.Friday, []),
        ],
        [new DateOnly(2026, 11, 7)]);

    [Theory]
    // 2026-10-31 is a Saturday. Tehran is UTC+03:30.
    [InlineData("2026-10-31T06:00:00Z", true)] // 09:30
    [InlineData("2026-10-31T10:00:00Z", false)] // 13:30, lunch break
    [InlineData("2026-10-31T17:00:00Z", true)] // 20:30
    [InlineData("2026-10-31T17:31:00Z", false)] // 21:01
    [InlineData("2026-11-06T08:00:00Z", false)] // Friday, closed
    [InlineData("2026-11-07T06:00:00Z", false)] // Saturday 09:30, but a closed date
    public void Opening_hours_are_local_to_the_workspace(string utc, bool open) =>
        Shop.IsOpen(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(open);

    [Fact]
    public void Always_open_is_open_at_any_time() => BusinessHours.AlwaysOpen.IsOpen(DateTimeOffset.UtcNow).ShouldBeTrue();
}

public class TemplateRendererTests
{
    [Fact]
    public void Fills_variables_and_falls_back()
    {
        var values = new Dictionary<string, string?> { ["contact.first_name"] = "سارا", ["comment.username"] = null };
        TemplateRenderer.Render("سلام {{contact.first_name}} جان", values).ShouldBe("سلام سارا جان");
        TemplateRenderer.Render("سلام {{ comment.username | دوست عزیز }}!", values).ShouldBe("سلام دوست عزیز!");
        TemplateRenderer.Render("{{unknown}}کد", values).ShouldBe("کد");
        TemplateRenderer.FirstName("  سارا  محمدی ").ShouldBe("سارا");
    }
}

public class FlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static FlowContext Context(DateTimeOffset now, bool windowOpen = true, params string[] tags) =>
        new(now, windowOpen, tags, new Dictionary<string, string>(), new Dictionary<string, string?> { ["contact.first_name"] = "علی" }, BusinessHours.AlwaysOpen);

    private static FlowRun NewRun() => FlowRun.Start(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), ChannelKinds.Instagram, "u1", null, Now);

    /// <summary>Welcome, ask the size (choice), branch on a VIP tag, wait an hour, send the price, end.</summary>
    private static FlowGraph SizeFlow() => new(
        "hello",
        [
            new FlowNode { Id = "hello", Type = FlowNodeTypes.Message, Message = new OutboundContent("سلام {{contact.first_name}}!") },
            new FlowNode { Id = "size", Type = FlowNodeTypes.Question, Message = new OutboundContent("چه سایزی؟"), AnswerType = AnswerTypes.Choice, Choices = ["۳۸", "۴۰", "۴۲"], Variable = "size", RetryText = "یکی از گزینه‌ها رو انتخاب کنید", MaxRetries = 1 },
            new FlowNode { Id = "vip", Type = FlowNodeTypes.Condition, Conditions = [new FlowCondition("tag", "has", "vip")] },
            new FlowNode { Id = "discount", Type = FlowNodeTypes.Message, Message = new OutboundContent("۱۰٪ تخفیف ویژه") },
            new FlowNode { Id = "wait", Type = FlowNodeTypes.Delay, DelaySeconds = 3600 },
            new FlowNode { Id = "price", Type = FlowNodeTypes.Message, Message = new OutboundContent("سایز {{flow.size}} موجوده") },
            new FlowNode { Id = "tag", Type = FlowNodeTypes.Action, Actions = [new RuleAction { Kind = ActionKinds.AddTag, Tag = "lead" }] },
        ],
        [
            new FlowEdge("hello", "size"),
            new FlowEdge("size", "vip"),
            new FlowEdge("vip", "discount", FlowHandles.True),
            new FlowEdge("vip", "wait", FlowHandles.False),
            new FlowEdge("discount", "wait"),
            new FlowEdge("wait", "price"),
            new FlowEdge("price", "tag"),
        ]);

    private static string? TextOf(FlowEffect effect) => (effect as FlowEffect.Send)?.Content.Text;

    [Fact]
    public void Runs_questions_conditions_delays_and_actions()
    {
        var graph = SizeFlow();
        FlowValidator.Validate(graph).IsValid.ShouldBeTrue();
        var run = NewRun();

        var start = FlowRunner.Start(graph, run, Context(Now));
        start.Select(TextOf).ShouldBe(["سلام علی!", "چه سایزی؟"]);
        ((FlowEffect.Send)start[1]).Content.QuickReplies!.Select(q => q.Payload).ShouldBe(["choice:0", "choice:1", "choice:2"]);
        run.Status.ShouldBe(FlowRunStatuses.WaitingInput);

        // Typed with ASCII digits, the answer matches the Persian-digit choice.
        var answered = FlowRunner.Answer(graph, run, "40", null, Context(Now.AddMinutes(1), tags: "vip"));
        answered.Select(TextOf).First().ShouldBe("۱۰٪ تخفیف ویژه");
        answered.OfType<FlowEffect.Timer>().Single().DueAt.ShouldBe(Now.AddMinutes(1).AddHours(1));
        run.Variables["size"].ShouldBe("۴۰");
        run.Status.ShouldBe(FlowRunStatuses.WaitingTimer);

        var resumed = FlowRunner.TimerFired(graph, run, "wait", Context(Now.AddHours(2)));
        TextOf(resumed[0]).ShouldBe("سایز ۴۰ موجوده");
        resumed.OfType<FlowEffect.Act>().Single().Actions[0].Tag.ShouldBe("lead");
        run.Status.ShouldBe(FlowRunStatuses.Completed);
    }

    [Fact]
    public void A_quick_reply_payload_answers_a_choice()
    {
        var graph = SizeFlow();
        var run = NewRun();
        FlowRunner.Start(graph, run, Context(Now));
        FlowRunner.Answer(graph, run, "۴۲", "choice:2", Context(Now)).ShouldNotBeEmpty();
        run.Variables["size"].ShouldBe("۴۲");
    }

    [Fact]
    public void Invalid_answers_retry_then_abandon()
    {
        var graph = SizeFlow();
        var run = NewRun();
        FlowRunner.Start(graph, run, Context(Now));
        FlowRunner.Answer(graph, run, "سبز", null, Context(Now)).Select(TextOf).ShouldBe(["یکی از گزینه‌ها رو انتخاب کنید"]);
        run.Status.ShouldBe(FlowRunStatuses.WaitingInput);
        FlowRunner.Answer(graph, run, "قرمز", null, Context(Now)).ShouldBeEmpty();
        run.Status.ShouldBe(FlowRunStatuses.Abandoned);
    }

    [Fact]
    public void Window_expiry_during_a_delay_ends_the_run_without_sending()
    {
        var graph = SizeFlow();
        var run = NewRun();
        FlowRunner.Start(graph, run, Context(Now));
        FlowRunner.Answer(graph, run, "۳۸", null, Context(Now));
        run.Status.ShouldBe(FlowRunStatuses.WaitingTimer);

        FlowRunner.TimerFired(graph, run, "wait", Context(Now.AddHours(25), windowOpen: false)).ShouldBeEmpty();
        run.Status.ShouldBe(FlowRunStatuses.Expired);
    }

    [Fact]
    public void A_stale_timer_does_nothing()
    {
        var graph = SizeFlow();
        var run = NewRun();
        FlowRunner.Start(graph, run, Context(Now));
        FlowRunner.TimerFired(graph, run, "wait", Context(Now)).ShouldBeEmpty();
        run.Status.ShouldBe(FlowRunStatuses.WaitingInput);
    }

    [Theory]
    [InlineData(AnswerTypes.Number, "۱۲٬۵۰۰", "12500")]
    [InlineData(AnswerTypes.Number, "دوازده", null)]
    [InlineData(AnswerTypes.Phone, "۰۹۱۲۱۲۳۴۵۶۷", "+989121234567")]
    [InlineData(AnswerTypes.Phone, "12345", null)]
    [InlineData(AnswerTypes.Text, "  تهران، ونک  ", "تهران، ونک")]
    public void Answers_are_validated_and_normalized(string type, string text, string? expected) =>
        FlowRunner.ReadAnswer(new FlowNode { Id = "q", Type = FlowNodeTypes.Question, AnswerType = type, Variable = "v" }, text, null).ShouldBe(expected);

    [Fact]
    public void Validator_reports_broken_graphs()
    {
        var bad = new FlowGraph(
            "a",
            [
                new FlowNode { Id = "a", Type = FlowNodeTypes.Message, Message = new OutboundContent("x") },
                new FlowNode { Id = "b", Type = FlowNodeTypes.Condition, Conditions = [new FlowCondition("tag", "has", "x")] },
                new FlowNode { Id = "g", Type = FlowNodeTypes.FollowGate },
                new FlowNode { Id = "q", Type = FlowNodeTypes.Question, AnswerType = "color", Variable = "Bad Name" },
            ],
            [new FlowEdge("a", "b"), new FlowEdge("b", "a", FlowHandles.True), new FlowEdge("a", "zzz"), new FlowEdge("a", "b", "maybe")]);

        var codes = FlowValidator.Validate(bad).Errors.Select(e => e.Code).ToList();
        codes.ShouldContain("flow.node_type_unavailable");
        codes.ShouldContain("flow.answer_type_invalid");
        codes.ShouldContain("flow.variable_invalid");
        codes.ShouldContain("flow.prompt_required");
        codes.ShouldContain("flow.edge_unknown_node");
        codes.ShouldContain("flow.handle_invalid");

        var loop = new FlowGraph(
            "a",
            [new FlowNode { Id = "a", Type = FlowNodeTypes.Message, Message = new OutboundContent("x") }, new FlowNode { Id = "b", Type = FlowNodeTypes.Message, Message = new OutboundContent("y") }, new FlowNode { Id = "c", Type = FlowNodeTypes.End }],
            [new FlowEdge("a", "b"), new FlowEdge("b", "a")]);
        var result = FlowValidator.Validate(loop);
        result.Errors.Select(e => e.Code).ShouldBe(["flow.loop_without_wait"]);
        result.Warnings.Select(w => w.NodeId).ShouldBe(["c"]);
    }

    [Fact]
    public void Runner_stops_a_runaway_graph()
    {
        var loop = new FlowGraph(
            "a",
            [new FlowNode { Id = "a", Type = FlowNodeTypes.Message, Message = new OutboundContent("x") }],
            [new FlowEdge("a", "a")]);
        var run = NewRun();
        FlowRunner.Start(loop, run, Context(Now)).Count.ShouldBe(FlowRunner.MaxSteps);
        run.Status.ShouldBe(FlowRunStatuses.Failed);
    }
}
