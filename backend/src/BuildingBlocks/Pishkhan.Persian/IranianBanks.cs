namespace Pishkhan.Persian;

public sealed record IranianBank(string Code, string NameFa, string NameEn);

/// <summary>Reference data: card BINs and Sheba bank codes of Iranian banks.</summary>
public static class IranianBanks
{
    public static readonly IranianBank Melli = new("melli", "بانک ملی ایران", "Bank Melli Iran");
    public static readonly IranianBank Sepah = new("sepah", "بانک سپه", "Bank Sepah");
    public static readonly IranianBank Mellat = new("mellat", "بانک ملت", "Bank Mellat");
    public static readonly IranianBank Tejarat = new("tejarat", "بانک تجارت", "Tejarat Bank");
    public static readonly IranianBank Saderat = new("saderat", "بانک صادرات ایران", "Bank Saderat Iran");
    public static readonly IranianBank Keshavarzi = new("keshavarzi", "بانک کشاورزی", "Bank Keshavarzi");
    public static readonly IranianBank Maskan = new("maskan", "بانک مسکن", "Bank Maskan");
    public static readonly IranianBank RefahKargaran = new("refah", "بانک رفاه کارگران", "Refah Bank");
    public static readonly IranianBank Pasargad = new("pasargad", "بانک پاسارگاد", "Bank Pasargad");
    public static readonly IranianBank Saman = new("saman", "بانک سامان", "Saman Bank");
    public static readonly IranianBank Parsian = new("parsian", "بانک پارسیان", "Parsian Bank");
    public static readonly IranianBank EghtesadNovin = new("eghtesad_novin", "بانک اقتصاد نوین", "EN Bank");
    public static readonly IranianBank Sina = new("sina", "بانک سینا", "Sina Bank");
    public static readonly IranianBank Shahr = new("shahr", "بانک شهر", "Shahr Bank");
    public static readonly IranianBank Ayandeh = new("ayandeh", "بانک آینده", "Ayandeh Bank");
    public static readonly IranianBank Dey = new("dey", "بانک دی", "Bank Dey");
    public static readonly IranianBank ResalatQard = new("resalat", "بانک قرض‌الحسنه رسالت", "Resalat Bank");
    public static readonly IranianBank MehrIran = new("mehr_iran", "بانک قرض‌الحسنه مهر ایران", "Mehr Iran Bank");
    public static readonly IranianBank PostBank = new("post", "پست بانک ایران", "Post Bank of Iran");
    public static readonly IranianBank Tosee = new("tosee", "بانک توسعه تعاون", "Tose'e Ta'avon Bank");
    public static readonly IranianBank Karafarin = new("karafarin", "بانک کارآفرین", "Karafarin Bank");
    public static readonly IranianBank Sarmayeh = new("sarmayeh", "بانک سرمایه", "Sarmayeh Bank");
    public static readonly IranianBank Gardeshgari = new("gardeshgari", "بانک گردشگری", "Tourism Bank");
    public static readonly IranianBank Khavarmianeh = new("khavarmianeh", "بانک خاورمیانه", "Middle East Bank");
    public static readonly IranianBank Iranzamin = new("iranzamin", "بانک ایران زمین", "Iran Zamin Bank");
    public static readonly IranianBank SanatMadan = new("sanat_madan", "بانک صنعت و معدن", "Bank of Industry and Mine");
    public static readonly IranianBank ToseeSaderat = new("tosee_saderat", "بانک توسعه صادرات", "Export Development Bank");

    public static readonly IReadOnlyDictionary<string, IranianBank> ByBin = new Dictionary<string, IranianBank>(StringComparer.Ordinal)
    {
        ["603799"] = Melli,
        ["589210"] = Sepah,
        ["610433"] = Mellat,
        ["991975"] = Mellat,
        ["585983"] = Tejarat,
        ["627353"] = Tejarat,
        ["603769"] = Saderat,
        ["603770"] = Keshavarzi,
        ["639217"] = Keshavarzi,
        ["628023"] = Maskan,
        ["589463"] = RefahKargaran,
        ["502229"] = Pasargad,
        ["639347"] = Pasargad,
        ["621986"] = Saman,
        ["622106"] = Parsian,
        ["639194"] = Parsian,
        ["627884"] = Parsian,
        ["627412"] = EghtesadNovin,
        ["639346"] = Sina,
        ["502806"] = Shahr,
        ["504706"] = Shahr,
        ["636214"] = Ayandeh,
        ["502938"] = Dey,
        ["504172"] = ResalatQard,
        ["606373"] = MehrIran,
        ["627760"] = PostBank,
        ["502908"] = Tosee,
        ["627488"] = Karafarin,
        ["502910"] = Karafarin,
        ["639607"] = Sarmayeh,
        ["505416"] = Gardeshgari,
        ["585947"] = Khavarmianeh,
        ["505785"] = Iranzamin,
        ["627961"] = SanatMadan,
        ["627648"] = ToseeSaderat,
        ["207177"] = ToseeSaderat,
    };

    /// <summary>Keyed by the two-digit bank identifier at positions 5-6 of the Sheba (after "IR" + check digits + "0").</summary>
    public static readonly IReadOnlyDictionary<string, IranianBank> BySheba = new Dictionary<string, IranianBank>(StringComparer.Ordinal)
    {
        ["17"] = Melli,
        ["15"] = Sepah,
        ["12"] = Mellat,
        ["18"] = Tejarat,
        ["19"] = Saderat,
        ["16"] = Keshavarzi,
        ["14"] = Maskan,
        ["13"] = RefahKargaran,
        ["57"] = Pasargad,
        ["56"] = Saman,
        ["54"] = Parsian,
        ["55"] = EghtesadNovin,
        ["59"] = Sina,
        ["61"] = Shahr,
        ["62"] = Ayandeh,
        ["66"] = Dey,
        ["70"] = ResalatQard,
        ["60"] = MehrIran,
        ["21"] = PostBank,
        ["22"] = Tosee,
        ["53"] = Karafarin,
        ["58"] = Sarmayeh,
        ["64"] = Gardeshgari,
        ["78"] = Khavarmianeh,
        ["69"] = Iranzamin,
        ["11"] = SanatMadan,
        ["20"] = ToseeSaderat,
    };
}
