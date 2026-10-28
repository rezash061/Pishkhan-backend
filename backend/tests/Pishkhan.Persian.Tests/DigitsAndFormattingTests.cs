using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Persian.Tests;

public class DigitsAndFormattingTests
{
    [Theory]
    [InlineData("123", "۱۲۳")]
    [InlineData("٠١٢", "۰۱۲")]
    [InlineData("abc 45", "abc ۴۵")]
    [InlineData("", "")]
    [InlineData("۱۲", "۱۲")]
    public void To_persian(string input, string expected) => PersianDigits.ToPersian(input).ShouldBe(expected);

    [Theory]
    [InlineData("۰۹۱۲-۰۰۰ ۱۲۳۴", "09120001234")]
    [InlineData("abc", "")]
    [InlineData("٤٥", "45")]
    public void Digits_only(string input, string expected) => PersianDigits.DigitsOnly(input).ShouldBe(expected);

    [Theory]
    [InlineData(1_250_000L, "۱۲۵٬۰۰۰ تومان")]
    [InlineData(10L, "۱ تومان")]
    [InlineData(0L, "۰ تومان")]
    [InlineData(15L, "۲ تومان")]
    [InlineData(10_000_000_000L, "۱٬۰۰۰٬۰۰۰٬۰۰۰ تومان")]
    public void Format_toman(long rial, string expected) => PersianMoney.FormatToman(Money.Rial(rial)).ShouldBe(expected);

    [Fact]
    public void Jalali_conversion_of_known_date()
    {
        // 28 October 2026 12:00 UTC is 6 Aban 1405 in Tehran.
        var date = Jalali.FromUtc(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        date.ShouldBe(new Jalali.JalaliDate(1405, 8, 6));
        date.MonthName.ShouldBe("آبان");
        Jalali.FormatLong(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)).ShouldBe("۶ آبان ۱۴۰۵");
        Jalali.FormatShort(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)).ShouldBe("۱۴۰۵/۰۸/۰۶");
    }

    [Fact]
    public void Nowruz_boundary_uses_tehran_time()
    {
        // 20 March 2026 21:00 UTC is already 21 March 00:30 in Tehran: 1 Farvardin 1405.
        Jalali.FromUtc(new DateTimeOffset(2026, 3, 20, 21, 0, 0, TimeSpan.Zero)).ShouldBe(new Jalali.JalaliDate(1405, 1, 1));
        Jalali.FromUtc(new DateTimeOffset(2026, 3, 20, 20, 0, 0, TimeSpan.Zero)).ShouldBe(new Jalali.JalaliDate(1404, 12, 29));
    }

    [Fact]
    public void Gregorian_round_trip() => Jalali.FromDateTime(Jalali.ToGregorian(1403, 12, 30)).ShouldBe(new Jalali.JalaliDate(1403, 12, 30));

    [Fact]
    public void Weekday_and_time_format() =>
        Jalali.FormatWithWeekdayAndTime(new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero)).ShouldBe("چهارشنبه ۶ آبان ۱۴۰۵، ساعت ۱۴:۳۰");

    [Theory]
    [InlineData("می خواهم", "می‌خواهم")]
    [InlineData("نمی دانم", "نمی‌دانم")]
    [InlineData("کتاب ها", "کتاب‌ها")]
    [InlineData("بزرگ ترین", "بزرگ‌ترین")]
    [InlineData("لباس های زیبا", "لباس‌های زیبا")]
    [InlineData("سلام دوست", "سلام دوست")]
    public void Zwnj_fixer(string input, string expected) => ZwnjFixer.Fix(input).ShouldBe(expected);
}
