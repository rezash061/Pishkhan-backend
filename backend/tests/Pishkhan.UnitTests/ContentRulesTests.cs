using System.Text.Json;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Modules.Content.Features;

namespace Pishkhan.UnitTests;

public sealed class ContentRulesTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("connect-instagram", true)]
    [InlineData("اتوماسیون-دایرکت", true)]
    [InlineData("می‌خواهم-بفروشم", true)]
    [InlineData("Bad-Slug", false)]
    [InlineData("two  spaces", false)]
    [InlineData("-leading", false)]
    [InlineData("a/b", false)]
    public void Slugs_are_lower_case_latin_or_persian_words_with_hyphens(string slug, bool valid) => ContentRules.IsSlug(slug).ShouldBe(valid);

    [Theory]
    [InlineData("/waitlist", true)]
    [InlineData("https://pishkhan.ir/a", true)]
    [InlineData("http://pishkhan.ir/a", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/a b", false)]
    public void Links_are_https_or_site_paths(string link, bool valid) => ContentRules.IsLink(link).ShouldBe(valid);

    [Fact]
    public void Bodies_allow_only_known_blocks_and_safe_links_at_any_depth()
    {
        ContentRules.Body(Json("""[{"type":"p","text":"سلام"},{"type":"hero","cta":{"text":"شروع","href":"/waitlist"}}]""")).Error.ShouldBeNull();
        ContentRules.Body(Json("""[{"type":"html","text":"<b>x</b>"}]""")).Error.ShouldBe("validation.content.block_type");
        ContentRules.Body(Json("""[{"text":"no type"}]""")).Error.ShouldBe("validation.content.block_type");
        ContentRules.Body(Json("""[{"type":"features","items":[{"title":"x","image":"javascript:x"}]}]""")).Error.ShouldBe("validation.content.link");
        ContentRules.Body(Json("""{"type":"p"}""")).Error.ShouldBe("validation.content.body");
        ContentRules.Body(null).Json.ShouldBe("[]");
        ContentRules.Data(Json("""["not an object"]""")).Error.ShouldBe("validation.content.data");
    }

    [Fact]
    public void Search_text_is_normalised_and_skips_links_and_types()
    {
        var text = ContentRules.SearchText(new EntryContent(
            "اتصال اينستاگرام", "راهنما", null, null, """[{"type":"cta","text":"ورود","href":"/login"}]""", """{"keywords":["Password"]}"""));
        text.ShouldContain("اینستاگرام");
        text.ShouldContain("ورود");
        text.ShouldContain("password");
        text.ShouldNotContain("/login");
        text.ShouldNotContain("cta");
    }

    [Fact]
    public void Paths_follow_the_websites_routes()
    {
        ContentRules.Paths(ContentKinds.Help, "connect", "start").ShouldBe(["/help", "/help/start", "/help/start/connect"]);
        ContentRules.Paths(ContentKinds.Page, "home", null).ShouldBe(["/"]);
        ContentRules.Paths(ContentKinds.Legal, "terms", null).ShouldBe(["/terms"]);
    }
}
