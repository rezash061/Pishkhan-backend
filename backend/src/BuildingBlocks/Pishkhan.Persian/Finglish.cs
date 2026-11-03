using System.Globalization;
using System.Text;

namespace Pishkhan.Persian;

/// <summary>How a text reads: <see cref="IsFinglish"/> when it is Persian written in Latin letters.</summary>
/// <param name="LatinShare">Share of letters that are Latin (0..1).</param>
/// <param name="FinglishWords">Words found in the Finglish lexicon.</param>
/// <param name="EnglishWords">Words found in the English stop-word list.</param>
public sealed record FinglishDetection(bool IsFinglish, double LatinShare, int Words, int FinglishWords, int EnglishWords);

/// <summary>A Finglish text in Persian script. <see cref="Coverage"/> is the share of words the lexicon knew; the rest were spelled by rules.</summary>
public sealed record FinglishTransliteration(string Persian, double Coverage, IReadOnlyList<string> UnknownWords);

/// <summary>
/// Finglish (Persian written in Latin letters, «salam gheymatesh chande?») detection and rule-based transliteration (backend plan 10.4,
/// gap G14). Detection uses the character classes and two word lists; transliteration uses a lexicon of common shop words and, for other
/// words, letter rules. Callers may send texts with low <see cref="FinglishTransliteration.Coverage"/> to a model instead.
/// </summary>
public static class Finglish
{
    /// <summary>Common Finglish words of shop conversations and their Persian spelling. Variants map to the same word.</summary>
    private static readonly Dictionary<string, string> Lexicon = Build(
        """
        salam,slm,salaam=سلام
        merci,mersi,mrc,mrsi=مرسی
        mamnoon,mamnun,mamnoun,mmnon=ممنون
        mamnoonam,mamnunam=ممنونم
        lotfan,lotfn,ltfn=لطفا
        khahesh,khahes=خواهش
        mikonam,mikonm=می‌کنم
        khoobi,khubi=خوبی
        khoob,khub,khob=خوب
        chetori,chtori=چطوری
        ghimat,gheymat,qeymat,qimat,gimat,gheimat,ghymt=قیمت
        gheymatesh,ghimatesh,gheymatsh,ghimatsh,qeymatesh=قیمتش
        gheymate,ghimate=قیمت
        chand,chan,chnd=چند
        chande,chande?,chandeh,chnde=چنده
        chanie,chanieh,chaniye=چنیه
        hast,has=هست
        hastesh=هستش
        nist,nis=نیست
        dari,darin,darid=دارید
        dare,dareh=داره
        mojood,mojud,mojod,mowjood,mojoode,mojude=موجود
        mojoodi,mojudi=موجودی
        size,sayz,saiz=سایز
        rang,rng=رنگ
        rangi,rangai,rangash,rangesh=رنگ‌هاش
        mikham,mikhm,mikhastam,mikhaam=می‌خوام
        kharid,kharidam,kharidan=خرید
        sefaresh,sefaresh,sfrsh=سفارش
        ersal,ersaal,ersl=ارسال
        post,pst=پست
        peyk,paik,peik=پیک
        tehran,tehrun=تهران
        shahrestan,shahrestaan=شهرستان
        hazine,hazineh,hazinash,hazinesh=هزینه
        takhfif,takhfif,tkhfif=تخفیف
        ghest,qest,aghsat,aqsat=قسط
        kart,cart=کارت
        shomare,shomareh,shmare=شماره
        hesab,hesaab=حساب
        pardakht,prdakht,pardaakht=پرداخت
        variz,variiz=واریز
        fish,resid,reside=رسید
        adres,address,adress,aders=آدرس
        kojast,kojas,koja=کجاست
        kei,key,kay=کی
        mirese,mireseh,miresad=می‌رسه
        kodoom,kodom,kodum=کدوم
        bozorg,bozorge=بزرگ
        kuchik,koochik,kochik=کوچیک
        jens,jensesh,jensash=جنس
        asl,asli,original,orginal=اصل
        tazmin,zemanat,garanti=ضمانت
        tavizi,taviz,avaz,avazi=تعویض
        marjoo,marjooi,marjui,marjou=مرجوعی
        pas,pasesh=پس
        bedam,bdm=بدم
        bedid,bedin,bdin=بدید
        befrestid,befrestin,befres=بفرستید
        aks,akse,akss=عکس
        model,modele,modelesh=مدل
        joft,jofti=جفت
        ta,taa=تا
        dige,digeh,dg=دیگه
        alan,aln=الان
        emrooz,emruz,emroz=امروز
        farda,frda=فردا
        saat,saaat=ساعت
        nobat,noobat=نوبت
        vaght,vaqt,vakht=وقت
        doreh,dore,doore=دوره
        sabtenam,sabt,sabte=ثبت‌نام
        tala,talaa=طلا
        gram,geram,grm=گرم
        ojrat,ojrate=اجرت
        ok,okey,okay,oki=اوکی
        bale,baleh,are,areh,aaare=بله
        na,nah,nakheir=نه
        ba,baa=با
        bara,baraye,braye,bray=برای
        az,azz=از
        be,beh=به
        dar,dr=در
        va,o=و
        ham,hm=هم
        man,mn=من
        shoma,shma,shoma?=شما
        in,een=این
        oon,un,oun=اون
        chi,chie,chiye=چی
        chera,chra=چرا
        chetor,chtor=چطور
        mishe,mishe?,misheh,mish=میشه
        mitoonam,mitunam,mitonam=می‌تونم
        hamin,hmin=همین
        kheili,kheyli,khili,khyli=خیلی
        ghashang,qashang,ghashng=قشنگ
        """);

    /// <summary>English words that, when common in a text, mean it is English rather than Finglish.</summary>
    private static readonly HashSet<string> English = new(
        ["the", "is", "are", "you", "your", "what", "how", "much", "price", "please", "do", "does", "have", "has", "can", "i", "my", "it", "this", "that",
         "for", "with", "and", "of", "to", "in", "on", "available", "shipping", "order", "want", "would", "like", "hello", "hi", "thanks", "thank"],
        StringComparer.Ordinal);

    /// <summary>Letter rules, longest first: digraphs before single letters. Vowels inside words are mostly dropped, as Persian writes them.</summary>
    private static readonly (string Latin, string Persian)[] Rules =
    [
        ("kh", "خ"), ("gh", "ق"), ("sh", "ش"), ("ch", "چ"), ("zh", "ژ"), ("aa", "ا"), ("oo", "و"), ("ou", "و"), ("ee", "ی"), ("ei", "ی"), ("ey", "ی"),
        ("b", "ب"), ("p", "پ"), ("t", "ت"), ("j", "ج"), ("d", "د"), ("r", "ر"), ("z", "ز"), ("s", "س"), ("f", "ف"), ("q", "ق"), ("k", "ک"),
        ("g", "گ"), ("l", "ل"), ("m", "م"), ("n", "ن"), ("v", "و"), ("w", "و"), ("h", "ه"), ("y", "ی"), ("x", "خ"), ("c", "ک"),
        ("u", "و"), ("o", "و"), ("i", "ی"), ("e", ""), ("a", ""),
    ];

    /// <summary>
    /// Finglish when most letters are Latin and the words look Persian: at least one lexicon word, and more lexicon words than English
    /// stop words. A text with no letters is not Finglish.
    /// </summary>
    public static FinglishDetection Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new FinglishDetection(false, 0, 0, 0, 0);
        }

        int latin = 0, persian = 0;
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                latin++;
            }
            else if (c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
            {
                persian++;
            }
        }

        var letters = latin + persian;
        var share = letters == 0 ? 0 : (double)latin / letters;
        var words = Words(text);
        var finglish = words.Count(w => Lexicon.ContainsKey(w));
        var english = words.Count(English.Contains);
        var isFinglish = letters > 0 && share >= 0.7 && finglish >= 1 && finglish > english;
        return new FinglishDetection(isFinglish, Math.Round(share, 3), words.Count, finglish, english);
    }

    /// <summary>
    /// Writes Latin-letter words in Persian script; everything else (Persian words, digits, punctuation, emoji) is kept. Lexicon words use
    /// their spelling; other words are spelled by <see cref="Rules"/> and listed in <see cref="FinglishTransliteration.UnknownWords"/>.
    /// </summary>
    public static FinglishTransliteration Transliterate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new FinglishTransliteration(string.Empty, 1, []);
        }

        var output = new StringBuilder(text.Length);
        var unknown = new List<string>();
        int known = 0, total = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsLatin(text[i]))
            {
                output.Append(text[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (IsLatin(text[i]) || text[i] == '\''))
            {
                i++;
            }

            var word = text[start..i].ToLowerInvariant().Trim('\'');
            total++;
            if (Lexicon.TryGetValue(word, out var spelled))
            {
                known++;
                output.Append(spelled);
            }
            else if (Suffixed(word) is { } withSuffix)
            {
                known++;
                output.Append(withSuffix);
            }
            else
            {
                unknown.Add(word);
                output.Append(Spell(word));
            }
        }

        return new FinglishTransliteration(output.ToString(), total == 0 ? 1 : Math.Round((double)known / total, 3), unknown);
    }

    /// <summary>Spells one word with the letter rules. An initial vowel becomes «ا» (or «آ» for "aa"), as Persian writes it.</summary>
    public static string Spell(string word)
    {
        var w = word.ToLowerInvariant();
        var sb = new StringBuilder(w.Length);
        var i = 0;
        if (w.StartsWith("aa", StringComparison.Ordinal))
        {
            sb.Append('آ');
            i = 2;
        }
        else if (w.Length > 0 && w[0] is 'a' or 'e' or 'o' or 'i' or 'u')
        {
            sb.Append('ا');
            i = w[0] is 'a' or 'e' ? 1 : 0;
        }

        while (i < w.Length)
        {
            var matched = false;
            foreach (var (latin, persian) in Rules)
            {
                if (string.CompareOrdinal(w, i, latin, 0, latin.Length) == 0)
                {
                    // A final "e" is written «ه» («khoone» → «خونه»).
                    sb.Append(latin == "e" && i == w.Length - 1 ? "ه" : persian);
                    i += latin.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                sb.Append(w[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>A lexicon word with a common suffix: -esh/-sh («its»), -ash, -am («my»), -e («the»), -ha («plural»).</summary>
    private static string? Suffixed(string word)
    {
        foreach (var (suffix, persian) in new[] { ("esh", "ش"), ("ash", "ش"), ("sh", "ش"), ("am", "م"), ("ha", "‌ها"), ("aa", "‌ها") })
        {
            if (word.Length > suffix.Length + 1 && word.EndsWith(suffix, StringComparison.Ordinal) && Lexicon.TryGetValue(word[..^suffix.Length], out var stem))
            {
                return stem + persian;
            }
        }

        return null;
    }

    private static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static List<string> Words(string text) =>
        [.. text.ToLowerInvariant().Split((char[])[' ', '\t', '\n', '\r', '?', '!', '.', ',', '؟', '،', '؛', ':', ';', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\''))
            .Where(w => w.Length > 0 && w.All(IsLatin))];

    private static Dictionary<string, string> Build(string table)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=');
            foreach (var latin in parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = latin.TrimEnd('?').ToLower(CultureInfo.InvariantCulture);
                map.TryAdd(key, parts[1]);
            }
        }

        return map;
    }
}
