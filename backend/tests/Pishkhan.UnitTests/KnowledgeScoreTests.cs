using Pishkhan.Modules.Automation.Features;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Features;
using Pishkhan.Modules.Knowledge.Services;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Templates.Domain;
using Pishkhan.Modules.Templates.Services;

namespace Pishkhan.UnitTests;

/// <summary>BE-1.9: the knowledge score and the onboarding drafts.</summary>
public class KnowledgeScoreTests
{
    private static FactStatus Fact(string key, bool covered) => new(key, key, key + "?", covered, covered ? Guid.NewGuid() : null);

    private static IReadOnlyList<FactStatus> Facts(int covered, int total) =>
        [.. Enumerable.Range(0, total).Select(i => Fact($"fact_{i}", i < covered))];

    [Fact]
    public void A_fresh_shop_scores_only_for_having_no_unanswered_questions()
    {
        var score = KnowledgeScorer.Compute("clothing", Facts(0, 8), 0, new ProductStats(0, 0, 0), 0);
        score.Score.ShouldBe(15);
        score.Suggestions.ShouldBe(["knowledge.suggest.answer_facts", "knowledge.suggest.add_products"]);
        score.Facts.ShouldAllBe(f => !f.Covered);
    }

    [Fact]
    public void Answering_every_required_fact_alone_passes_sixty()
    {
        // The plan's acceptance: onboarding chat alone takes a fresh workspace above 60.
        KnowledgeScorer.Compute("clothing", Facts(8, 8), 0, new ProductStats(0, 0, 0), 0).Score.ShouldBe(75);
        KnowledgeScorer.Compute("courses", Facts(7, 7), 0, new ProductStats(0, 0, 0), 0).Score.ShouldBe(100);
    }

    [Fact]
    public void Service_industries_without_products_move_the_product_points_to_facts()
    {
        var score = KnowledgeScorer.Compute("beauty_clinic", Facts(1, 2), 0, new ProductStats(0, 0, 0), 0);
        score.Parts.Select(p => p.Key).ShouldBe(["facts", "unanswered"]);
        score.Parts[0].Max.ShouldBe(85);
        score.Parts[0].Points.ShouldBe(43);
        score.Products.ShouldBeNull();
        score.Suggestions.ShouldNotContain("knowledge.suggest.add_products");
    }

    [Fact]
    public void Products_count_when_priced_and_in_stock()
    {
        var score = KnowledgeScorer.Compute("cosmetics", Facts(4, 4), 0, new ProductStats(10, 5, 10), 0);
        score.Parts.Single(p => p.Key == "products").Points.ShouldBe(19);
        score.Suggestions.ShouldBe(["knowledge.suggest.add_prices"]);
        score.Score.ShouldBe(60 + 19 + 15);
    }

    [Fact]
    public void Open_unanswered_questions_lose_a_tenth_each()
    {
        KnowledgeScorer.Compute("clothing", Facts(0, 1), 0, new ProductStats(0, 0, 0), 4).Parts.Single(p => p.Key == "unanswered").Points.ShouldBe(9);
        KnowledgeScorer.Compute("clothing", Facts(0, 1), 0, new ProductStats(0, 0, 0), 25).Parts.Single(p => p.Key == "unanswered").Points.ShouldBe(0);
    }

    [Fact]
    public void Without_a_template_ten_enabled_faqs_earn_the_facts_part()
    {
        KnowledgeScorer.Compute("other", [], 10, new ProductStats(0, 0, 0), 0).Parts[0].Points.ShouldBe(60);
        var some = KnowledgeScorer.Compute("other", [], 3, new ProductStats(0, 0, 0), 0);
        some.Parts[0].Points.ShouldBe(18);
        some.Suggestions.ShouldContain("knowledge.suggest.add_faqs");
    }

    [Fact]
    public void A_model_draft_must_have_a_question_and_an_answer()
    {
        OnboardingDrafter.Parse("""{"question":"هزینه ارسال چقدره؟","answer":"تهران ۵۰ هزار تومان است."}""").ShouldBe(("هزینه ارسال چقدره؟", "تهران ۵۰ هزار تومان است."));
        OnboardingDrafter.Parse("""{"question":"هزینه ارسال چقدره؟"}""").ShouldBeNull();
        OnboardingDrafter.Parse("not json").ShouldBeNull();
        OnboardingDrafter.Parse(null).ShouldBeNull();
    }

    [Fact]
    public void Without_a_model_the_owner_words_become_the_answer()
    {
        var draft = OnboardingDrafter.Fallback(new RequiredFact("shipping_cost", "هزینه ارسال", "هزینه ارسال چقدر است؟"), "تهران ۵۰ تومن");
        draft.Question.ShouldBe("هزینه ارسال چقدر است؟");
        draft.Answer.ShouldBe("تهران ۵۰ تومن");
        draft.Drafted.ShouldBe("fallback");
    }

    [Fact]
    public void A_template_faq_can_only_answer_one_of_its_required_facts()
    {
        var validator = new TemplateContentValidator(new AutomationTemplateValidator());
        var content = new TemplateContent(
            [new RequiredFact("shipping_cost", "هزینه ارسال", "هزینه ارسال چقدر است؟")],
            [new TemplateFaq("shipping", "ارسال؟", "[مبلغ] تومان", "shipping_cost"), new TemplateFaq("other", "سؤال؟", "پاسخ", "missing_fact")],
            [], [], null);
        validator.Validate(content).ShouldBe(["faqs[other].factKey: templates.fact_unknown"]);
    }
}
