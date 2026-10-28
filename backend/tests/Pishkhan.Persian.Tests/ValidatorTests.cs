using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Persian.Tests;

public class ValidatorTests
{
    [Theory]
    [InlineData("09120001234", "+989120001234")]
    [InlineData("9120001234", "+989120001234")]
    [InlineData("+989120001234", "+989120001234")]
    [InlineData("989120001234", "+989120001234")]
    [InlineData("00989120001234", "+989120001234")]
    [InlineData("۰۹۱۲۰۰۰۱۲۳۴", "+989120001234")]
    [InlineData("٠٩١٢٠٠٠١٢٣٤", "+989120001234")]
    [InlineData("0912 000 1234", "+989120001234")]
    [InlineData("0912-000-1234", "+989120001234")]
    [InlineData("(0912) 0001234", "+989120001234")]
    [InlineData("+98 912 000 1234", "+989120001234")]
    [InlineData("‎09120001234", "+989120001234")]
    [InlineData("09350001234", "+989350001234")]
    [InlineData("09900001234", "+989900001234")]
    public void Valid_mobiles(string input, string e164) => IranianMobile.ToE164(input).ShouldBe(e164);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0212345678")]
    [InlineData("02112345678")]
    [InlineData("0912000123")]
    [InlineData("091200012345")]
    [InlineData("+19120001234")]
    [InlineData("0912abc1234")]
    [InlineData("08120001234")]
    [InlineData("+0989120001234")]
    [InlineData("12345678901234567890123456789012345")]
    [InlineData("09120001234+")]
    public void Invalid_mobiles(string? input) => IranianMobile.IsValid(input).ShouldBeFalse();

    [Fact]
    public void Phone_masking_and_national_form()
    {
        var phone = PhoneNumber.Parse("09120001234");
        phone.National.ShouldBe("09120001234");
        phone.Masked.ShouldBe("+98912***1234");
        phone.ToString().ShouldNotContain("0001");
    }

    [Theory]
    [InlineData("0499370899", true)]
    [InlineData("0790419904", true)]
    [InlineData("0084575948", true)]
    [InlineData("1111111111", false)]
    [InlineData("0000000000", false)]
    [InlineData("0499370898", false)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    [InlineData("۰۴۹۹۳۷۰۸۹۹", true)]
    [InlineData("499370899", true)]
    [InlineData("abcdefghij", false)]
    [InlineData("0013542419", true)]
    public void National_id(string input, bool expected) => NationalId.IsValid(input).ShouldBe(expected);

    [Theory]
    [InlineData("6037997599999993", true)]
    [InlineData("6037-9975-9999-9993", true)]
    [InlineData("۶۰۳۷۹۹۷۵۹۹۹۹۹۹۹۳", true)]
    [InlineData("6037997599999990", false)]
    [InlineData("603799759999999", false)]
    [InlineData("", false)]
    [InlineData("6037 9975 9999 9993", true)]
    [InlineData("abcd997599999993", false)]
    public void Bank_card_luhn(string input, bool expected) => BankCard.IsValid(input).ShouldBe(expected);

    [Fact]
    public void Bank_card_bin_and_mask()
    {
        BankCard.GetBank("6037997599999993").ShouldBe(IranianBanks.Melli);
        BankCard.GetBank("6104337599999993")!.Code.ShouldBe("mellat");
        BankCard.GetBank("1234567599999993").ShouldBeNull();
        BankCard.Mask("6037997599999993").ShouldBe("6037-99**-****-9993");
        BankCard.Last4("6037-9975-9999-9993").ShouldBe("9993");
    }

    [Theory]
    [InlineData("IR062960000000100324200001", true)]
    [InlineData("IR06 2960 0000 0010 0324 2000 01", true)]
    [InlineData("ir062960000000100324200001", true)]
    [InlineData("062960000000100324200001", true)]
    [InlineData("IR062960000000100324200002", false)]
    [InlineData("IR06296000000010032420000", false)]
    [InlineData("DE062960000000100324200001", false)]
    [InlineData("", false)]
    [InlineData("IR820540102680020817909002", true)]
    [InlineData("IR۸۲۰۵۴۰۱۰۲۶۸۰۰۲۰۸۱۷۹۰۹۰۰۲", true)]
    public void Sheba(string input, bool expected) => Persian.Sheba.IsValid(input).ShouldBe(expected);

    [Fact]
    public void Sheba_bank() => Persian.Sheba.GetBank("IR820540102680020817909002").ShouldBe(IranianBanks.Parsian);

    [Theory]
    [InlineData("1193653471", true)]
    [InlineData("۱۱۹۳۶۵۳۴۷۱", true)]
    [InlineData("11936-53471", true)]
    [InlineData("1111111111", false)]
    [InlineData("0193653471", false)]
    [InlineData("1293653471", false)]
    [InlineData("119365347", false)]
    [InlineData("11936534712", false)]
    [InlineData("", false)]
    [InlineData("1193553471", false)]
    [InlineData("1193653472", false)]
    public void Postal_code(string input, bool expected) => PostalCode.IsValid(input).ShouldBe(expected);
}
