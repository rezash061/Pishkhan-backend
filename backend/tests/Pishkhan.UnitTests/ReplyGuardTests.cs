using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Replies;
using Pishkhan.Modules.Knowledge.Contracts;

namespace Pishkhan.UnitTests;

public sealed class ReplyGuardTests
{
    [Theory]
    [InlineData("قیمتش ۱٬۸۵۰٬۰۰۰ تومان است", 1_850_000L, "toman")]
    [InlineData("فقط 1,850,000 تومن", 1_850_000L, "toman")]
    [InlineData("۱.۸۵۰.۰۰۰ تومان", 1_850_000L, "toman")]
    [InlineData("۱۸۵ هزار تومان میشه", 185_000L, "toman")]
    [InlineData("۱ میلیون و ۸۵۰ هزار تومان", 1_850_000L, "toman")]
    [InlineData("یک میلیون و هشتصد و پنجاه هزار تومان", 1_850_000L, "toman")]
    [InlineData("۱.۵ میلیون تومان", 1_500_000L, "toman")]
    [InlineData("۱۸,۵۰۰,۰۰۰ ریال", 1_850_000L, "rial")]
    [InlineData("قیمت این مدل ۴۵۰۰۰۰ هست", 450_000L, null)]
    [InlineData("۳۲۰ هزار", 320_000L, null)]
    public void Amounts_in_any_style_are_found_in_toman(string text, long toman, string? unit)
    {
        var mention = MoneyText.Find(text).ShouldHaveSingleItem();
        mention.Toman.ShouldBe(toman);
        mention.Unit.ShouldBe(unit);
    }

    [Theory]
    [InlineData("ارسال دو تا چهار روز کاری طول می‌کشه")]
    [InlineData("شماره تماس ۰۹۱۲۱۲۳۴۵۶۷")]
    [InlineData("سایز ۴۲ و ۴۳ موجوده، ۲ هزار نفر خریدن")]
    [InlineData("۲۰ درصد تخفیف")]
    public void Other_numbers_are_not_money(string text)
    {
        MoneyText.Find(text).ShouldBeEmpty();
    }

    private static GuardContext Context(params string[] facts)
    {
        var guard = new GuardContext { NeverSay = ["ارزان‌ترین بازار"], CustomerText = "قیمتش چنده؟" };
        foreach (var f in facts)
        {
            guard.Allow(f);
        }

        return guard;
    }

    [Fact]
    public void A_price_from_the_facts_passes_in_any_format()
    {
        var guard = Context("""{"title":"کیف","price":"۱٬۸۵۰٬۰۰۰ تومان","priceToman":1850000}""");
        ReplyGuard.Check("قیمت کیف ۱ میلیون و ۸۵۰ هزار تومانه 😊", guard).ShouldBeEmpty();
        ReplyGuard.Check("قیمتش 1,850,000 تومن", guard).ShouldBeEmpty();
        ReplyGuard.Check("۱۸٬۵۰۰٬۰۰۰ ریال", guard).ShouldBeEmpty();
    }

    [Fact]
    public void A_price_not_in_the_facts_is_blocked()
    {
        var guard = Context("""{"title":"کیف","price":"۱٬۸۵۰٬۰۰۰ تومان","priceToman":1850000}""");
        var issue = ReplyGuard.Check("برای شما ۱٬۶۰۰٬۰۰۰ تومان حساب می‌کنیم", guard).ShouldHaveSingleItem();
        issue.Kind.ShouldBe("price");
        ReplyGuard.Explain([issue]).ShouldContain("1,600,000");
    }

    [Fact]
    public void Never_say_links_length_and_script_are_checked()
    {
        var guard = Context("سفارش از سایت shop.example.ir هم ممکنه");
        ReplyGuard.Check("ما ارزان‌ترین بازار هستیم", guard).ShouldContain(i => i.Kind == "never_say");
        ReplyGuard.Check("از https://shop.example.ir/bag ببینید", guard).ShouldBeEmpty();
        ReplyGuard.Check("از www.digikala.com بخرید", guard).ShouldContain(i => i.Kind == "link" && i.Detail == "digikala.com");
        ReplyGuard.Check(new string('ا', ReplyGuard.MaxLength + 1), guard).ShouldContain(i => i.Kind == "length");
        ReplyGuard.Check("Sure, the bag is available", guard).ShouldContain(i => i.Kind == "language");

        var english = new GuardContext { CustomerText = "hi, do you ship to Shiraz?" };
        ReplyGuard.Check("Yes, we ship to every city.", english).ShouldBeEmpty();
    }

    [Fact]
    public void Model_outputs_are_parsed_leniently()
    {
        Classification.Parse("""```json {"intent":"complaint","sentiment":"negative","language":"fa","confidence":0.8} ```""")
            .ShouldBe(new Classification("complaint", "fa", "negative", 0.8));
        Classification.Parse("""{"intent":"weird","confidence":"2"}""").ShouldBe(new Classification("other", null, null, 1));
        Classification.Parse("not json").ShouldBe(Classification.Unknown);

        GeneratedReply.Parse("""{"reply":"سلام!","confidence":0.9,"answered":true}""").ShouldBe(new GeneratedReply("سلام!", 0.9, true));
        GeneratedReply.Parse("""{"reply":"","answered":false}""").Answered.ShouldBeFalse();
        GeneratedReply.Parse("سلام، بفرمایید").ShouldBe(new GeneratedReply("سلام، بفرمایید", 0.5, true));
    }

    [Fact]
    public void Handoff_rules_run_in_code()
    {
        var rules = new HandoffRules { Keywords = ["مرجوعی فوری"] };
        HandoffPolicy.Before("سلام ادمین، قیمت؟", [], rules).ShouldBeNull();
        HandoffPolicy.Before("میخوام با اپراتور صحبت کنم", [], rules).ShouldBe("human_request");
        HandoffPolicy.Before("مرجوعی فوری دارم", [], rules).ShouldBe("keyword");
        HandoffPolicy.Before("سلام", ["VIP"], rules).ShouldBe("vip");
        HandoffPolicy.Before("میخوام با اپراتور صحبت کنم", [], new HandoffRules { HumanRequest = false }).ShouldBeNull();

        HandoffPolicy.After(new Classification("complaint", "fa", "negative", 0.9), rules).ShouldBe("complaint");
        HandoffPolicy.After(new Classification("complaint", "fa", "neutral", 0.9), rules).ShouldBeNull();
        HandoffPolicy.After(new Classification("discount_request", "fa", null, 0.9), rules).ShouldBe("discount_request");
        HandoffPolicy.After(new Classification("discount_request", "fa", null, 0.9), new HandoffRules { DiscountRequest = false }).ShouldBeNull();
    }

    [Fact]
    public void Confidence_combines_the_model_the_classifier_and_grounding()
    {
        var hit = new KnowledgeHit(Guid.NewGuid(), Guid.NewGuid(), "faq", "ارسال", "متن", 0.03, 0.8, null);
        ReplyPipeline.Confidence(new Classification("shipping", null, null, 0.9), new GeneratedReply("x", 0.9, true), [hit], false).ShouldBe(0.875);
        ReplyPipeline.Confidence(new Classification("other", null, null, 0.5), new GeneratedReply("x", 0.4, true), [], false).ShouldBe(0.4);
        ReplyPipeline.Confidence(new Classification("price_question", null, null, 0.8), new GeneratedReply("x", 0.8, true), [], true).ShouldBe(0.85);
    }
}
