using System.Text.RegularExpressions;

namespace Pishkhan.Persian;

/// <summary>A province (استان) with its capital first and its other large cities.</summary>
public sealed record IranianProvince(string Code, string Name, IReadOnlyList<string> Cities)
{
    public string Capital => Cities[0];
}

/// <summary>Reference data: the 31 provinces of Iran. Cities are the capital and the larger towns, for parsing; any city is accepted.</summary>
public static class IranianProvinces
{
    public static readonly IReadOnlyList<IranianProvince> All =
    [
        new("east_azerbaijan", "آذربایجان شرقی", ["تبریز", "مراغه", "مرند", "اهر", "میانه", "بناب"]),
        new("west_azerbaijan", "آذربایجان غربی", ["ارومیه", "خوی", "مهاباد", "بوکان", "میاندوآب", "سلماس"]),
        new("ardabil", "اردبیل", ["اردبیل", "پارس‌آباد", "مشگین‌شهر"]),
        new("isfahan", "اصفهان", ["اصفهان", "کاشان", "نجف‌آباد", "خمینی‌شهر", "شاهین‌شهر", "فولادشهر"]),
        new("alborz", "البرز", ["کرج", "فردیس", "نظرآباد", "هشتگرد"]),
        new("ilam", "ایلام", ["ایلام", "دهلران"]),
        new("bushehr", "بوشهر", ["بوشهر", "برازجان", "گناوه", "کنگان"]),
        new("tehran", "تهران", ["تهران", "اسلامشهر", "شهریار", "قدس", "ملارد", "ورامین", "پاکدشت", "رباط‌کریم", "پردیس", "دماوند"]),
        new("chaharmahal_bakhtiari", "چهارمحال و بختیاری", ["شهرکرد", "بروجن", "فارسان"]),
        new("south_khorasan", "خراسان جنوبی", ["بیرجند", "قائن", "طبس"]),
        new("razavi_khorasan", "خراسان رضوی", ["مشهد", "نیشابور", "سبزوار", "تربت حیدریه", "قوچان", "کاشمر"]),
        new("north_khorasan", "خراسان شمالی", ["بجنورد", "شیروان", "اسفراین"]),
        new("khuzestan", "خوزستان", ["اهواز", "دزفول", "آبادان", "خرمشهر", "بندر ماهشهر", "اندیمشک", "شوشتر", "بهبهان"]),
        new("zanjan", "زنجان", ["زنجان", "ابهر", "خرمدره"]),
        new("semnan", "سمنان", ["سمنان", "شاهرود", "دامغان", "گرمسار"]),
        new("sistan_baluchestan", "سیستان و بلوچستان", ["زاهدان", "چابهار", "ایرانشهر", "زابل"]),
        new("fars", "فارس", ["شیراز", "مرودشت", "جهرم", "فسا", "کازرون", "لار", "داراب"]),
        new("qazvin", "قزوین", ["قزوین", "تاکستان", "آبیک"]),
        new("qom", "قم", ["قم"]),
        new("kurdistan", "کردستان", ["سنندج", "سقز", "مریوان", "بانه"]),
        new("kerman", "کرمان", ["کرمان", "رفسنجان", "سیرجان", "جیرفت", "بم", "زرند"]),
        new("kermanshah", "کرمانشاه", ["کرمانشاه", "اسلام‌آباد غرب", "کنگاور", "هرسین"]),
        new("kohgiluyeh_boyer_ahmad", "کهگیلویه و بویراحمد", ["یاسوج", "دهدشت", "گچساران"]),
        new("golestan", "گلستان", ["گرگان", "گنبد کاووس", "علی‌آباد کتول", "بندر ترکمن"]),
        new("gilan", "گیلان", ["رشت", "بندر انزلی", "لاهیجان", "لنگرود", "تالش", "آستارا"]),
        new("lorestan", "لرستان", ["خرم‌آباد", "بروجرد", "دورود", "کوهدشت", "الیگودرز"]),
        new("mazandaran", "مازندران", ["ساری", "بابل", "آمل", "قائم‌شهر", "بهشهر", "چالوس", "تنکابن", "بابلسر"]),
        new("markazi", "مرکزی", ["اراک", "ساوه", "خمین", "محلات"]),
        new("hormozgan", "هرمزگان", ["بندرعباس", "قشم", "کیش", "میناب", "بندر لنگه"]),
        new("hamadan", "همدان", ["همدان", "ملایر", "نهاوند", "تویسرکان"]),
        new("yazd", "یزد", ["یزد", "میبد", "اردکان"]),
    ];

    private static readonly IReadOnlyDictionary<string, IranianProvince> ByCode = All.ToDictionary(p => p.Code, StringComparer.Ordinal);

    public static IranianProvince? Find(string? codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName))
        {
            return null;
        }

        if (ByCode.TryGetValue(codeOrName.Trim(), out var byCode))
        {
            return byCode;
        }

        var name = Key(codeOrName.Replace("استان", string.Empty, StringComparison.Ordinal));
        return All.FirstOrDefault(p => Key(p.Name) == name);
    }

    /// <summary>The province of a known city (the first province listing it).</summary>
    public static IranianProvince? OfCity(string? city) =>
        string.IsNullOrWhiteSpace(city) ? null : All.FirstOrDefault(p => p.Cities.Any(c => Key(c) == Key(city)));

    /// <summary>Normalized, without spaces or half-spaces, so «خرم آباد» matches «خرم‌آباد».</summary>
    internal static string Key(string text) =>
        PersianNormalizer.Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(PersianNormalizer.Zwnj.ToString(), string.Empty, StringComparison.Ordinal);
}

/// <summary>An address split into its parts. <see cref="Line"/> is the rest of the text (street, alley, plaque, unit).</summary>
public sealed record ParsedAddress(string? ProvinceCode, string? Province, string? City, string? PostalCode, string? Mobile, string Line);

/// <summary>
/// Splits a free-text Iranian address, as customers type it in a DM, into province, city, 10-digit postal code and mobile number.
/// The province comes from its name or from a known city; the postal code only when it is valid. Nothing is guessed beyond that.
/// </summary>
public static partial class AddressParser
{
    public static ParsedAddress Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedAddress(null, null, null, null, null, string.Empty);
        }

        var rest = PersianDigits.ToLatin(text.Trim());

        string? mobile = null;
        foreach (Match m in MobileRegex().Matches(rest))
        {
            if (IranianMobile.ToE164(m.Value) is { } e164)
            {
                mobile = e164;
                rest = rest.Remove(m.Index, m.Length).Insert(m.Index, " ");
                break;
            }
        }

        string? postal = null;
        foreach (Match m in PostalRegex().Matches(rest))
        {
            var digits = m.Value.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (PostalCode.IsValid(digits))
            {
                postal = digits;
                rest = rest.Remove(m.Index, m.Length).Insert(m.Index, " ");
                break;
            }
        }

        rest = LabelRegex().Replace(rest, " ");
        var words = Words(rest);

        var province = FindPhrase(words, IranianProvinces.All.Select(p => (p.Name, p)), out var provinceSpan);
        if (provinceSpan is { } ps)
        {
            words.RemoveRange(ps.Start, ps.Length);
            if (ps.Start > 0 && words.Count > ps.Start - 1 && words[ps.Start - 1] == "استان")
            {
                words.RemoveAt(ps.Start - 1);
            }
        }

        var cityCandidates = (province is null ? IranianProvinces.All : [province]).SelectMany(p => p.Cities.Select(c => (c, p)));
        var cityProvince = FindPhrase(words, cityCandidates, out var citySpan);
        string? city = null;
        if (citySpan is { } cs)
        {
            city = string.Join(' ', words.GetRange(cs.Start, cs.Length));
            city = cityProvince!.Cities.First(c => IranianProvinces.Key(c) == IranianProvinces.Key(city));
            words.RemoveRange(cs.Start, cs.Length);
            if (cs.Start > 0 && words.Count > cs.Start - 1 && words[cs.Start - 1] == "شهر")
            {
                words.RemoveAt(cs.Start - 1);
            }
        }
        else if (province is not null && Key(province.Name) == Key(province.Capital))
        {
            // «تهران» or «اصفهان» alone names the city as well as the province.
            city = province.Capital;
        }

        province ??= cityProvince;
        var line = string.Join(' ', words).Trim(' ', '،', ',', '-', '.', ':');
        return new ParsedAddress(province?.Code, province?.Name, city, postal, mobile, line);
    }

    private static string Key(string text) => IranianProvinces.Key(text);

    private static List<string> Words(string text) =>
        [.. text.Split([' ', '\n', '\t', '،', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', '-', ':'))
            .Where(w => w.Length > 0)];

    /// <summary>The longest phrase (up to four words) that equals one of the names; ties go to the earliest.</summary>
    private static T? FindPhrase<T>(List<string> words, IEnumerable<(string Name, T Value)> names, out (int Start, int Length)? span)
        where T : class
    {
        var keys = names.Select(n => (Key: Key(n.Name), n.Value)).ToList();
        for (var length = 4; length >= 1; length--)
        {
            for (var start = 0; start + length <= words.Count; start++)
            {
                var phrase = Key(string.Join(string.Empty, words.GetRange(start, length)));
                foreach (var (key, value) in keys)
                {
                    if (key == phrase)
                    {
                        span = (start, length);
                        return value;
                    }
                }
            }
        }

        span = null;
        return null;
    }

    [GeneratedRegex(@"(?<!\d)(?:\+98|0098|98|0)?9\d{2}[\s-]?\d{3}[\s-]?\d{4}(?!\d)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex MobileRegex();

    [GeneratedRegex(@"(?<!\d)\d{5}[\s-]?\d{5}(?!\d)", RegexOptions.CultureInvariant, 200)]
    private static partial Regex PostalRegex();

    [GeneratedRegex(@"(کد\s*پستی|کدپستی|موبایل|تلفن|شماره\s*تماس|آدرس|نشانی)\s*[:：]?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex LabelRegex();
}
