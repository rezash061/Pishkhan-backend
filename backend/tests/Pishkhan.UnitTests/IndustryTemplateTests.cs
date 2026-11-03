using System.Text.Json;
using Pishkhan.Contracts;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Features;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Templates.Domain;
using Pishkhan.Modules.Templates.Services;
using Pishkhan.SharedKernel;

namespace Pishkhan.UnitTests;

public class IndustryTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly TemplateContentValidator Validator = new(new AutomationTemplateValidator());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static TemplateFlow Flow(string key = "ask") => new(key, "پرسش", Json("""
        {"start":"q","nodes":[
          {"id":"q","type":"question","message":{"text":"اسمت چیه؟"},"answerType":"text","variable":"name"},
          {"id":"done","type":"end"}],
         "edges":[{"from":"q","to":"done"}]}
        """));

    private static TemplateRule Rule(string actions, string key = "start", string trigger = """{"match":"contains","keywords":["سلام"]}""", string kind = "dm_keyword") =>
        new(key, "شروع", kind, Json(trigger), Json(actions), 0);

    private static TemplateContent Content(IReadOnlyList<TemplateFlow>? flows = null, IReadOnlyList<TemplateRule>? rules = null, TemplateAgentProfile? profile = null) =>
        new([new RequiredFact("shipping_cost", "هزینه ارسال", "هزینه ارسال چقدر است؟")], [new TemplateFaq("hours", "ساعت کاری؟", "از ۹ تا ۱۸")], flows ?? [], rules ?? [], profile);

    [Fact]
    public void Built_in_templates_cover_the_five_launch_verticals_and_are_valid()
    {
        var seeds = BuiltInTemplates.Load();
        string.Join(',', seeds.Select(s => s.Industry).Order(StringComparer.Ordinal)).ShouldBe("beauty_clinic,clothing,cosmetics,courses,jewelry");
        foreach (var seed in seeds)
        {
            Validator.Validate(seed.Content).ShouldBeEmpty(seed.Industry);
            seed.Content.RequiredFacts.Count.ShouldBeGreaterThanOrEqualTo(5, seed.Industry);
            seed.Content.Faqs.Count.ShouldBeGreaterThanOrEqualTo(5, seed.Industry);
            seed.Content.Flows.ShouldNotBeEmpty(seed.Industry);
            seed.Content.Rules.ShouldNotBeEmpty(seed.Industry);
            seed.Content.AgentProfile.ShouldNotBeNull(seed.Industry);
            TemplateContentValidator.Industries.ShouldContain(seed.Industry);
        }
    }

    [Fact]
    public void A_rule_starts_a_template_flow_by_its_key()
    {
        var content = Content([Flow()], [Rule("""[{"kind":"start_flow","flowKey":"ask"}]""")]);
        Validator.Validate(content).ShouldBeEmpty();
    }

    [Fact]
    public void A_rule_naming_an_unknown_flow_key_is_an_issue()
    {
        var content = Content([Flow()], [Rule("""[{"kind":"start_flow","flowKey":"missing"}]""")]);
        Validator.Validate(content).ShouldContain("rules[start]: template.flow_key_unknown");
    }

    [Fact]
    public void A_template_cannot_point_at_a_workspace_flow_or_card_set()
    {
        var flowId = Guid.NewGuid();
        var content = Content([], [
            Rule($$"""[{"kind":"start_flow","flowId":"{{flowId}}"}]""", "by-id"),
            Rule($$"""[{"kind":"send_cards","cardSetId":"{{flowId}}"}]""", "cards"),
        ]);
        var issues = Validator.Validate(content);
        issues.ShouldContain("rules[by-id]: template.actions_invalid");
        issues.ShouldContain("rules[cards]: template.workspace_reference");
    }

    [Fact]
    public void Rule_and_flow_problems_use_the_panel_error_keys()
    {
        var broken = new TemplateFlow("broken", "خراب", Json("""{"start":"x","nodes":[{"id":"a","type":"end"}],"edges":[]}"""));
        var content = Content([broken], [Rule("""[{"kind":"send_message","message":{"text":"سلام"}}]""", trigger: "{}")]);
        var issues = Validator.Validate(content);
        issues.ShouldContain("flows[broken]: flow.start_missing");
        issues.ShouldContain("rules[start]: automation.keywords_required");
    }

    [Fact]
    public void Keys_must_be_unique_and_well_formed()
    {
        var content = new TemplateContent(
            [new RequiredFact("a_fact", "برچسب", "پرسش"), new RequiredFact("a_fact", "برچسب", "پرسش")],
            [new TemplateFaq("Bad Key", "پرسش", "پاسخ")], [], [], null);
        var issues = Validator.Validate(content);
        issues.ShouldContain("requiredFacts[a_fact]: template.key_invalid");
        issues.ShouldContain("faqs[Bad Key]: template.key_invalid");
    }

    [Fact]
    public void An_agent_profile_needs_a_known_tone()
    {
        var content = Content(profile: new TemplateAgentProfile("دستیار", "angry", null, [], []));
        Validator.Validate(content).ShouldContain("agentProfile.tone: templates.tone_invalid");
    }

    [Fact]
    public void Content_round_trips_through_the_stored_json()
    {
        var content = Content([Flow()], [Rule("""[{"kind":"start_flow","flowKey":"ask"}]""")], new TemplateAgentProfile("دستیار", "friendly", "صمیمی", ["قول"], ["شکایت"]));
        var json = JsonSerializer.Serialize(content, EnvelopeJson.Options);
        var back = JsonSerializer.Deserialize<TemplateContent>(json, EnvelopeJson.Options)!;
        Validator.Validate(back).ShouldBeEmpty();
        JsonSerializer.Serialize(back, EnvelopeJson.Options).ShouldBe(json);
    }

    [Fact]
    public void Only_a_draft_can_be_edited_or_published()
    {
        var template = IndustryTemplate.Create("clothing", "پوشاک", null, Content(), null, Now);
        template.Status.ShouldBe(TemplateStatuses.Draft);
        template.Publish(null, Now);
        template.Status.ShouldBe(TemplateStatuses.Published);
        Should.Throw<DomainException>(() => template.Publish(null, Now));
        Should.Throw<DomainException>(() => template.Update("پوشاک ۲", null, Content(), null, Now));

        var next = template.NextVersion(2, Guid.NewGuid(), Now);
        next.Version.ShouldBe(2);
        next.Status.ShouldBe(TemplateStatuses.Draft);
        next.Industry.ShouldBe("clothing");
    }
}
