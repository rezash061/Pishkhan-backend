using Pishkhan.Persian;

namespace Pishkhan.Persian.Tests;

public class AddressTests
{
    [Fact]
    public void There_are_31_provinces_with_unique_codes_and_names()
    {
        IranianProvinces.All.Count.ShouldBe(31);
        IranianProvinces.All.Select(p => p.Code).Distinct().Count().ShouldBe(31);
        IranianProvinces.All.Select(p => p.Name).Distinct().Count().ShouldBe(31);
    }

    [Theory]
    [InlineData("tehran", "tehran")]
    [InlineData("تهران", "tehran")]
    [InlineData("استان فارس", "fars")]
    [InlineData("خراسان رضوي", "razavi_khorasan")]
    [InlineData("كرمانشاه", "kermanshah")]
    [InlineData("کرمان", "kerman")]
    [InlineData("چهارمحال‌و‌بختیاری", "chaharmahal_bakhtiari")]
    public void Provinces_are_found_by_code_or_name(string input, string code) => IranianProvinces.Find(input)!.Code.ShouldBe(code);

    [Fact]
    public void Unknown_province_is_null() => IranianProvinces.Find("کالیفرنیا").ShouldBeNull();

    [Theory]
    [InlineData("خرم آباد", "lorestan")]
    [InlineData("مشهد", "razavi_khorasan")]
    [InlineData("کرج", "alborz")]
    public void A_known_city_gives_its_province(string city, string code) => IranianProvinces.OfCity(city)!.Code.ShouldBe(code);

    [Fact]
    public void A_dm_address_is_split_into_its_parts()
    {
        var a = AddressParser.Parse("شیراز، خیابان زند، کوچه ۱۲، پلاک ۴ کد پستی ۷۱۳۴۸۹۵۶۴۱ شماره ۰۹۱۲۳۴۵۶۷۸۹");

        a.ProvinceCode.ShouldBe("fars");
        a.Province.ShouldBe("فارس");
        a.City.ShouldBe("شیراز");
        a.PostalCode.ShouldBe("7134895641");
        a.Mobile.ShouldBe("+989123456789");
        a.Line.ShouldContain("خیابان زند");
        a.Line.ShouldContain("پلاک");
        a.Line.ShouldNotContain("شیراز");
        a.Line.ShouldNotContain("7134895641");
    }

    [Fact]
    public void Province_and_city_both_named()
    {
        var a = AddressParser.Parse("استان خراسان رضوی، شهر نیشابور، بلوار جانبازان ۱۰");

        a.ProvinceCode.ShouldBe("razavi_khorasan");
        a.City.ShouldBe("نیشابور");
        a.Line.ShouldBe("بلوار جانبازان 10");
    }

    [Fact]
    public void Tehran_alone_is_the_city_and_the_province()
    {
        var a = AddressParser.Parse("تهران، ولیعصر، بالاتر از پارک ساعی، پلاک ۲۰");

        a.ProvinceCode.ShouldBe("tehran");
        a.City.ShouldBe("تهران");
    }

    [Fact]
    public void Kerman_is_not_found_inside_kermanshah()
    {
        AddressParser.Parse("کرمانشاه، میدان آزادی").ProvinceCode.ShouldBe("kermanshah");
    }

    [Fact]
    public void An_invalid_postal_code_is_left_in_the_line()
    {
        var a = AddressParser.Parse("اصفهان، خیابان چهارباغ، کد ۰۰۰۰۰۰۰۰۰۰");

        a.PostalCode.ShouldBeNull();
        a.City.ShouldBe("اصفهان");
    }

    [Fact]
    public void Nothing_is_guessed_from_unknown_text()
    {
        var a = AddressParser.Parse("خیابان اول، کوچه دوم");

        a.ProvinceCode.ShouldBeNull();
        a.City.ShouldBeNull();
        a.PostalCode.ShouldBeNull();
        a.Line.ShouldBe("خیابان اول کوچه دوم");
    }
}
