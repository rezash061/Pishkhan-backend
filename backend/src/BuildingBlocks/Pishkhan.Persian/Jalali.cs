using System.Globalization;
using Pishkhan.SharedKernel;

namespace Pishkhan.Persian;

/// <summary>Jalali (Solar Hijri) conversion for the edges only: exports and generated messages. Storage is UTC.</summary>
public static class Jalali
{
    private static readonly PersianCalendar Calendar = new();

    private static readonly string[] MonthNames =
    [
        "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور",
        "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند",
    ];

    private static readonly string[] WeekdayNames =
    [
        "یکشنبه", "دوشنبه", "سه‌شنبه", "چهارشنبه", "پنجشنبه", "جمعه", "شنبه",
    ];

    public readonly record struct JalaliDate(int Year, int Month, int Day)
    {
        public string MonthName => MonthNames[Month - 1];

        public override string ToString() => $"{Year:0000}/{Month:00}/{Day:00}";
    }

    /// <summary>Converts a UTC instant to the Jalali date in Asia/Tehran.</summary>
    public static JalaliDate FromUtc(DateTimeOffset utc)
    {
        var local = TehranTime.ToTehran(utc).DateTime;
        return FromDateTime(local);
    }

    public static JalaliDate FromDateTime(DateTime date) =>
        new(Calendar.GetYear(date), Calendar.GetMonth(date), Calendar.GetDayOfMonth(date));

    public static DateTime ToGregorian(int year, int month, int day) => Calendar.ToDateTime(year, month, day, 0, 0, 0, 0);

    public static bool IsLeapYear(int year) => Calendar.IsLeapYear(year);

    /// <summary>For example "۶ آبان ۱۴۰۵".</summary>
    public static string FormatLong(DateTimeOffset utc)
    {
        var d = FromUtc(utc);
        return PersianDigits.ToPersian($"{d.Day} {d.MonthName} {d.Year}");
    }

    /// <summary>For example "۱۴۰۵/۰۸/۰۶".</summary>
    public static string FormatShort(DateTimeOffset utc) => PersianDigits.ToPersian(FromUtc(utc).ToString());

    /// <summary>For example "سه‌شنبه ۶ آبان ۱۴۰۵، ساعت ۱۴:۳۰".</summary>
    public static string FormatWithWeekdayAndTime(DateTimeOffset utc)
    {
        var local = TehranTime.ToTehran(utc);
        var weekday = WeekdayNames[(int)local.DayOfWeek];
        return PersianDigits.ToPersian($"{weekday} {FormatLongLatin(utc)}، ساعت {local.ToString("HH:mm", CultureInfo.InvariantCulture)}");
    }

    private static string FormatLongLatin(DateTimeOffset utc)
    {
        var d = FromUtc(utc);
        return $"{d.Day} {d.MonthName} {d.Year}";
    }

    public static string MonthName(int month) => MonthNames[month - 1];
}

public static class PersianMoney
{
    /// <summary>Formats a Rial amount as Toman with Persian digits and the Persian thousands separator, for example "۱۲۵٬۰۰۰ تومان".</summary>
    public static string FormatToman(Money money)
    {
        var toman = money.ToToman();
        var latin = toman.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '٬');
        return PersianDigits.ToPersian(latin) + " تومان";
    }
}
