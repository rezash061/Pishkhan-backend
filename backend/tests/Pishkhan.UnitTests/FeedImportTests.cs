using Pishkhan.Modules.Knowledge.Domain;
using Pishkhan.Modules.Knowledge.Services;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.5: what a post caption turns into, and that a price the caption does not state is never trusted.</summary>
public class FeedImportTests
{
    private const string Caption = "مانتو کتان تابستانه 🌸\nسایز ۳۸ تا ۴۶\nقیمت: ۴۵۰ تومن\n#مانتو #کتان";

    [Fact]
    public void A_product_caption_becomes_a_product_with_its_price_in_rial()
    {
        var json = """{"products":[{"title":"مانتو کتان تابستانه","description":"سایز ۳۸ تا ۴۶","price":450000,"tags":["#مانتو","کتان","کتان"]}],"faqs":[]}""";
        var item = FeedExtractor.Parse(json, Caption).ShouldHaveSingleItem();
        item.Kind.ShouldBe(FeedItemKinds.Product);
        item.Title.ShouldBe("مانتو کتان تابستانه");
        item.Price.ShouldBe(4_500_000);
        item.PriceInCaption.ShouldBeTrue();
        item.Tags.ShouldBe(["مانتو", "کتان"]);
    }

    [Fact]
    public void A_price_not_written_in_the_caption_is_marked()
    {
        var json = """{"products":[{"title":"مانتو","price":990000}]}""";
        var item = FeedExtractor.Parse(json, Caption).ShouldHaveSingleItem();
        item.Price.ShouldBe(9_900_000);
        item.PriceInCaption.ShouldBeFalse();
    }

    [Fact]
    public void Prices_given_as_persian_text_are_read()
    {
        var json = """{"products":[{"title":"شال","price":"۱,۲۵۰,۰۰۰"}]}""";
        var item = FeedExtractor.Parse(json, "شال نخی ۱٬۲۵۰٬۰۰۰ تومان").ShouldHaveSingleItem();
        item.Price.ShouldBe(12_500_000);
        item.PriceInCaption.ShouldBeTrue();
    }

    [Fact]
    public void Faqs_need_a_question_and_an_answer_and_junk_is_dropped()
    {
        var json = """{"products":[{"price":100},{"title":""}],"faqs":[{"question":"ارسال دارید؟","answer":"به همه شهرها با پست"},{"question":"فقط سؤال"}]}""";
        var item = FeedExtractor.Parse(json, "ارسال به همه شهرها").ShouldHaveSingleItem();
        item.Kind.ShouldBe(FeedItemKinds.Faq);
        item.Answer.ShouldBe("به همه شهرها با پست");

        FeedExtractor.Parse("not json", Caption).ShouldBeEmpty();
        FeedExtractor.Parse("[]", Caption).ShouldBeEmpty();
        FeedExtractor.Parse(null, Caption).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(450_000, true)]
    [InlineData(450, true)]
    [InlineData(45_000, false)]
    [InlineData(2_000_000, true)]
    public void Caption_numbers_match_as_written_or_in_thousands_and_millions(long toman, bool expected)
    {
        var numbers = FeedExtractor.CaptionNumbers("قیمت ۴۵۰ تومن، سرویس کامل ۲ میلیون");
        FeedExtractor.InCaption(toman, numbers).ShouldBe(expected);
    }

    [Fact]
    public void An_import_reads_at_most_its_posts()
    {
        var now = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var import = FeedImport.Start(Guid.NewGuid(), Guid.NewGuid(), 500, null, now);
        import.MaxPostsToRead.ShouldBe(FeedImport.MaxPosts);
        import.Read(10, 8, 1, 12, now);
        import.Remaining.ShouldBe(190);
        import.Fail(new string('x', 300), now);
        import.Status.ShouldBe(FeedImportStatuses.Failed);
        import.Error!.Length.ShouldBe(100);
    }
}
