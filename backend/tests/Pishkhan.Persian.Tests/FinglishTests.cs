using Pishkhan.Persian;

namespace Pishkhan.Persian.Tests;

public class FinglishTests
{
    [Theory]
    [InlineData("salam gheymatesh chande?")]
    [InlineData("Slm, size L mojood dari?")]
    [InlineData("mersi kheili ghashange")]
    [InlineData("ersal be shahrestan hazine dare?")]
    [InlineData("lotfan shomare kart ro befrestid")]
    public void Finglish_is_detected(string text) => Finglish.Detect(text).IsFinglish.ShouldBeTrue(text);

    [Theory]
    [InlineData("سلام قیمتش چنده؟")]
    [InlineData("What is the price of this?")]
    [InlineData("Do you have it in size L?")]
    [InlineData("👍")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Persian_English_and_empty_texts_are_not_finglish(string? text) => Finglish.Detect(text).IsFinglish.ShouldBeFalse(text ?? "null");

    [Fact]
    public void Mixed_script_text_is_finglish_only_when_mostly_latin()
    {
        Finglish.Detect("سلام، این مدل موجوده؟ size L").IsFinglish.ShouldBeFalse();
        Finglish.Detect("salam in model mojood hast? سایز").IsFinglish.ShouldBeTrue();
    }

    [Theory]
    [InlineData("salam gheymatesh chande?", "سلام قیمتش چنده?")]
    [InlineData("size L mojood dari?", "سایز ل موجود دارید?")]
    [InlineData("mersi", "مرسی")]
    [InlineData("ersal be tehran", "ارسال به تهران")]
    public void Lexicon_words_get_their_persian_spelling(string latin, string persian) =>
        Finglish.Transliterate(latin).Persian.ShouldBe(persian);

    [Fact]
    public void Unknown_words_are_spelled_by_rules_and_reported()
    {
        var result = Finglish.Transliterate("salam manto shalvar");
        result.UnknownWords.ShouldBe(["manto", "shalvar"]);
        result.Coverage.ShouldBe(0.333);
        result.Persian.ShouldBe("سلام منتو شلور");
    }

    [Theory]
    [InlineData("khoone", "خونه")]
    [InlineData("aab", "آب")]
    [InlineData("esm", "اسم")]
    [InlineData("shir", "شیر")]
    public void Letter_rules_follow_persian_spelling(string latin, string persian) => Finglish.Spell(latin).ShouldBe(persian);

    [Fact]
    public void Persian_digits_punctuation_and_emoji_are_kept()
    {
        Finglish.Transliterate("salam 😊 ۲ ta mikham!").Persian.ShouldBe("سلام 😊 ۲ تا می‌خوام!");
        Finglish.Transliterate("سلام").Coverage.ShouldBe(1);
    }

    [Fact]
    public void Common_suffixes_on_lexicon_words_are_understood()
    {
        Finglish.Transliterate("rangesh").Persian.ShouldBe("رنگ‌هاش");
        Finglish.Transliterate("modelesh").Persian.ShouldBe("مدل");
        Finglish.Transliterate("aksha").Persian.ShouldBe("عکس‌ها");
    }
}
