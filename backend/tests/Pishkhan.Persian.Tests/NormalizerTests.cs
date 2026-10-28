using Pishkhan.Persian;

namespace Pishkhan.Persian.Tests;

public class NormalizerTests
{
    [Theory]
    // Arabic yeh and kaf to Persian
    [InlineData("علي", "علی")]
    [InlineData("كتاب", "کتاب")]
    [InlineData("يك", "یک")]
    [InlineData("كيف", "کیف")]
    [InlineData("مشكي", "مشکی")]
    [InlineData("ليمويي", "لیمویی")]
    [InlineData("كفش زنانه", "کفش زنانه")]
    [InlineData("تيشرت", "تیشرت")]
    [InlineData("قيمت", "قیمت")]
    [InlineData("موجودي", "موجودی")]
    [InlineData("كرم", "کرم")]
    [InlineData("مانتو كتان", "مانتو کتان")]
    [InlineData("سلامتي", "سلامتی")]
    [InlineData("ارسال رايگان", "ارسال رایگان")]
    [InlineData("سايز", "سایز")]
    [InlineData("مصطفى", "مصطفی")]
    [InlineData("موسى", "موسی")]
    [InlineData("ى", "ی")]
    [InlineData("ي", "ی")]
    [InlineData("ك", "ک")]
    // Teh marbuta and heh with yeh
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("خانۀ", "خانه")]
    [InlineData("ة", "ه")]
    [InlineData("جامعة", "جامعه")]
    [InlineData("نامۀ من", "نامه من")]
    // Alef variants and hamza seats
    [InlineData("أحمد", "احمد")]
    [InlineData("إيران", "ایران")]
    [InlineData("ٱلله", "الله")]
    [InlineData("مؤسسه", "موسسه")]
    [InlineData("مسئول", "مسیول")]
    [InlineData("آب", "آب")]
    [InlineData("آرایشی", "آرایشی")]
    public void Arabic_letters_become_Persian(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("۱۲۳", "123")]
    [InlineData("١٢٣", "123")]
    [InlineData("۰۹۱۲۰۰۰۱۲۳۴", "09120001234")]
    [InlineData("٠٩١٢٠٠٠١٢٣٤", "09120001234")]
    [InlineData("قیمت ۲۵۰ هزار", "قیمت 250 هزار")]
    [InlineData("سایز ۳۸", "سایز 38")]
    [InlineData("۱۰۰۰", "1000")]
    [InlineData("۱۱۱۱", "1111")]
    [InlineData("۵۰٪ تخفیف", "50% تخفیف")]
    [InlineData("٪", "%")]
    [InlineData("۲٫۵", "2.5")]
    [InlineData("۱٬۲۵۰٬۰۰۰", "1,250,000")]
    [InlineData("۱۲3", "123")]
    [InlineData("کد ۴۵۶۷", "کد 4567")]
    [InlineData("۹۹/۱۲/۲۹", "99/12/29")]
    [InlineData("۰", "0")]
    [InlineData("۹", "9")]
    [InlineData("٩", "9")]
    [InlineData("٥", "5")]
    [InlineData("۵", "5")]
    [InlineData("سفارش ۱۲۳۴۵", "سفارش 12345")]
    [InlineData("۳ عدد", "3 عدد")]
    [InlineData("۲۲۲", "222")]
    [InlineData("1404", "1404")]
    [InlineData("4444", "4444")]
    public void Digits_become_ascii(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Digits_kept_when_requested() =>
        PersianNormalizer.Normalize("۱۲۳", NormalizerOptions.Default with { LatinDigits = false }).ShouldBe("۱۲۳");

    [Theory]
    [InlineData("سلاااام", "سلاام")]
    [InlineData("مرسیییی", "مرسیی")]
    [InlineData("خیلیییییی خوبه", "خیلیی خوبه")]
    [InlineData("عالیییی", "عالیی")]
    [InlineData("واااای", "واای")]
    [InlineData("ممنووووون", "ممنوون")]
    [InlineData("سلام", "سلام")]
    [InlineData("الله", "الله")]
    [InlineData("آره", "آره")]
    [InlineData("hiiiii", "hii")]
    [InlineData("noooo", "noo")]
    [InlineData("!!!!", "!!!!")]
    [InlineData("؟؟؟؟", "????")]
    [InlineData("ههههههه", "هه")]
    [InlineData("ققققیمت", "ققیمت")]
    [InlineData("بلههههه", "بلهه")]
    [InlineData("کیییییف", "کییف")]
    [InlineData("ززززز", "زز")]
    [InlineData("ok", "ok")]
    [InlineData("oo", "oo")]
    public void Repeated_letters_collapse_to_two(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("سلامـــــ", "سلام")]
    [InlineData("بـــــله", "بله")]
    [InlineData("کـــیف", "کیف")]
    [InlineData("مـمـنـون", "ممنون")]
    [InlineData("ـــ", "")]
    [InlineData("قیمـت", "قیمت")]
    [InlineData("تخفیـــف", "تخفیف")]
    [InlineData("عـالـی", "عالی")]
    [InlineData("سـ", "س")]
    [InlineData("پـسـتـی", "پستی")]
    public void Kashida_is_removed(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("كِتاب", "کتاب")]
    [InlineData("مُحَمَّد", "محمد")]
    [InlineData("سَلام", "سلام")]
    [InlineData("دُرُست", "درست")]
    [InlineData("قِیمَت", "قیمت")]
    [InlineData("اً", "ا")]
    [InlineData("حتماً", "حتما")]
    [InlineData("فوراً", "فورا")]
    [InlineData("لطفاً", "لطفا")]
    [InlineData("واقعاً", "واقعا")]
    [InlineData("مَن", "من")]
    [InlineData("عِشق", "عشق")]
    [InlineData("رَحمان", "رحمان")]
    [InlineData("مٰ", "م")]
    [InlineData("بِسْمِ", "بسم")]
    public void Diacritics_are_removed(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("می‌خواهم", "می‌خواهم")]
    [InlineData("می‌‌خواهم", "می‌خواهم")]
    [InlineData("کتاب‌ها", "کتاب‌ها")]
    [InlineData("‌سلام", "سلام")]
    [InlineData("سلام‌", "سلام")]
    [InlineData("سلام ‌خوبی", "سلام خوبی")]
    [InlineData("سلام‌ خوبی", "سلام خوبی")]
    [InlineData("abc‌def", "abcdef")]
    [InlineData("می‍خواهم", "میخواهم")]
    [InlineData("می​خواهم", "میخواهم")]
    [InlineData("﻿سلام", "سلام")]
    [InlineData("سل­ام", "سلام")]
    [InlineData("‏سلام‎", "سلام")]
    [InlineData("‫سلام‬", "سلام")]
    [InlineData("⁧سلام⁩", "سلام")]
    [InlineData("خانه‌ی", "خانه‌ی")]
    [InlineData("بزرگ‌ترین", "بزرگ‌ترین")]
    [InlineData("نمی‌دانم", "نمی‌دانم")]
    [InlineData("۱۲‌۳", "123")]
    [InlineData("پیراهن‌های", "پیراهن‌های")]
    public void Zwnj_is_normalized(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("  سلام  ", "سلام")]
    [InlineData("سلام    خوبی", "سلام خوبی")]
    [InlineData("سلام\tخوبی", "سلام خوبی")]
    [InlineData("سلام\nخوبی", "سلام خوبی")]
    [InlineData("سلام\r\nخوبی", "سلام خوبی")]
    [InlineData("سلام خوبی", "سلام خوبی")]
    [InlineData("سلام خوبی", "سلام خوبی")]
    [InlineData("سلام　خوبی", "سلام خوبی")]
    [InlineData("\n\n", "")]
    [InlineData(" ", "")]
    [InlineData("", "")]
    [InlineData("a  b   c", "a b c")]
    [InlineData("سلام خوبی", "سلام خوبی")]
    [InlineData("سلام خوبی", "سلام خوبی")]
    [InlineData("\t\tقیمت\t", "قیمت")]
    public void Whitespace_is_collapsed_and_trimmed(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("قیمت؟", "قیمت?")]
    [InlineData("سلام، خوبی؟", "سلام, خوبی?")]
    [InlineData("الف؛ ب", "الف; ب")]
    [InlineData("«کیف»", "\"کیف\"")]
    [InlineData("“quoted”", "\"quoted\"")]
    [InlineData("‘a’", "'a'")]
    [InlineData("الف–ب", "الف-ب")]
    [InlineData("الف—ب", "الف-ب")]
    [InlineData("۱۰−۵", "10-5")]
    [InlineData("پایان۔", "پایان.")]
    public void Punctuation_is_unified(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Punctuation_kept_when_requested() =>
        PersianNormalizer.Normalize("قیمت؟", NormalizerOptions.Default with { UnifyPunctuation = false }).ShouldBe("قیمت؟");

    [Theory]
    [InlineData("ﻻ", "لا")]
    [InlineData("ﺑﻪ", "به")]
    [InlineData("ﺳﻼم", "سلام")]
    [InlineData("ﻛﺘﺎب", "کتاب")]
    [InlineData("ﻳﻚ", "یک")]
    public void Presentation_forms_are_folded(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("Price", "price")]
    [InlineData("HELLO", "hello")]
    [InlineData("Salam Khubi", "salam khubi")]
    [InlineData("iPhone ۱۵", "iphone 15")]
    [InlineData("SIZE XL", "size xl")]
    public void Latin_is_lowercased(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("قيمت اين مانتو چنده؟", "قیمت این مانتو چنده?")]
    [InlineData("سلااااام وقت بخير، كيف مشكي سايز ۳۸ داريد؟", "سلاام وقت بخیر, کیف مشکی سایز 38 دارید?")]
    [InlineData("ميخوام سفارش بدم ‌", "میخوام سفارش بدم")]
    [InlineData("آدرس: تهران، خيابان وليعصر، پلاك ۱۲", "آدرس: تهران, خیابان ولیعصر, پلاک 12")]
    [InlineData("شماره كارت ۶۰۳۷-۹۹۱۲-۳۴۵۶-۷۸۹۰", "شماره کارت 6037-9912-3456-7890")]
    [InlineData("مرسييييي 🙏🙏", "مرسیی 🙏🙏")]
    [InlineData("تخفيـــف داريد؟؟", "تخفیف دارید??")]
    [InlineData("ارسال به شيراز چند روز طول ميكشه", "ارسال به شیراز چند روز طول میکشه")]
    [InlineData("كرم ضد آفتاب SPF ۵۰", "کرم ضد آفتاب spf 50")]
    [InlineData("طلای ۱۸ عيار گرمی چنده", "طلای 18 عیار گرمی چنده")]
    [InlineData("نوبت ويزيت براي فردا", "نوبت ویزیت برای فردا")]
    [InlineData("دوره آموزشي آنلاين", "دوره آموزشی آنلاین")]
    [InlineData("  ", "")]
    [InlineData("😀", "😀")]
    [InlineData("salam gheymat chande?", "salam gheymat chande?")]
    public void Realistic_messages(string input, string expected) =>
        PersianNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Null_is_empty() => PersianNormalizer.Normalize(null).ShouldBe(string.Empty);

    [Fact]
    public void Control_characters_are_removed() => PersianNormalizer.Normalize("سل\u0001ام\u0007").ShouldBe("سلام");

    [Fact]
    public void Normalization_is_idempotent()
    {
        string[] samples = ["قيمت اين مانتو چنده؟", "سلااااام", "می‌خواهم", "۱۲۳٬۴۵۶", "كِتاب‌ها"];
        foreach (var sample in samples)
        {
            var once = PersianNormalizer.Normalize(sample);
            PersianNormalizer.Normalize(once).ShouldBe(once);
        }
    }

    [Theory]
    [InlineData("سلام خوبی", true)]
    [InlineData("salam khubi", false)]
    [InlineData("سلام hi", true)]
    [InlineData("123", false)]
    [InlineData("iphone قیمت", false)]
    public void Script_detection(string input, bool expected) => PersianNormalizer.IsMostlyPersianScript(input).ShouldBe(expected);
}
