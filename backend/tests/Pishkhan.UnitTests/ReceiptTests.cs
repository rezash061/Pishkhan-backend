using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Services;
using SkiaSharp;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.10: receipt checks, matching masked cards and Sheba, reading the model's answer, and image hashes.</summary>
public class ReceiptTests
{
    private static readonly DateTimeOffset OrderAt = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly ReceiptOrder Order = new(Guid.NewGuid(), 4_500_000, OrderAt);
    private static readonly ReceiptAccount[] Accounts = [new("6037991234567893", null), new(null, "IR820540102680020817909002")];

    private static ReceiptFields Fields(long? amount = 4_500_000, string? card = "6037-99**-****-7893", double confidence = 0.97, string? reference = "123456789012") =>
        new(amount, OrderAt.AddMinutes(20), reference, "5022-29**-****-1111", card, null, "ملت", confidence, true);

    private static ReceiptSettings Settings(bool auto = true) => new() { AutoApprove = auto, Threshold = 0.9 };

    private static ReceiptVerdict Evaluate(ReceiptFields? fields, ReceiptDuplicates? duplicates = null, bool available = true, ReceiptSettings? settings = null, ReceiptOrder? order = null) =>
        ReceiptRules.Evaluate(fields, order ?? Order, Accounts, duplicates ?? ReceiptDuplicates.None, settings ?? Settings(), available, OrderAt.AddMinutes(30));

    private static string Result(ReceiptVerdict v, string key) => v.Checks.Single(c => c.Key == key).Result;

    [Fact]
    public void A_receipt_that_passes_every_check_is_auto_approved_only_where_allowed()
    {
        Evaluate(Fields()).Status.ShouldBe(ReceiptStatuses.AutoApproved);
        Evaluate(Fields(), available: false).Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Evaluate(Fields(), settings: Settings(auto: false)).Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Evaluate(Fields(confidence: 0.85)).Status.ShouldBe(ReceiptStatuses.NeedsReview);
    }

    [Fact]
    public void An_amount_mismatch_needs_review()
    {
        var verdict = Evaluate(Fields(amount: 4_000_000));
        verdict.Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Result(verdict, ReceiptCheckKeys.Amount).ShouldBe(ReceiptCheckResults.Fail);
        Evaluate(Fields(amount: 4_490_000), settings: new ReceiptSettings { AutoApprove = true, AmountTolerance = 10_000 }).Status.ShouldBe(ReceiptStatuses.AutoApproved);
    }

    [Fact]
    public void A_duplicate_reference_or_image_is_rejected_and_a_similar_image_needs_review()
    {
        var earlier = Guid.NewGuid();
        var byReference = Evaluate(Fields(), new ReceiptDuplicates(earlier, null, null));
        byReference.Status.ShouldBe(ReceiptStatuses.Rejected);
        byReference.DuplicateOfId.ShouldBe(earlier);
        Evaluate(Fields(), new ReceiptDuplicates(null, earlier, null)).Status.ShouldBe(ReceiptStatuses.Rejected);

        var similar = Evaluate(Fields(), new ReceiptDuplicates(null, null, earlier));
        similar.Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Result(similar, ReceiptCheckKeys.Duplicate).ShouldBe(ReceiptCheckResults.Fail);
    }

    [Fact]
    public void A_transfer_to_another_card_or_outside_the_window_needs_review()
    {
        Result(Evaluate(Fields(card: "6219-86**-****-0000")), ReceiptCheckKeys.Destination).ShouldBe(ReceiptCheckResults.Fail);
        var late = Fields() with { PaidAt = OrderAt.AddDays(5) };
        Result(ReceiptRules.Evaluate(late, Order, Accounts, ReceiptDuplicates.None, Settings(), true, OrderAt.AddDays(5)), ReceiptCheckKeys.TimeWindow).ShouldBe(ReceiptCheckResults.Fail);
        var early = Fields() with { PaidAt = OrderAt.AddHours(-3) };
        Result(Evaluate(early), ReceiptCheckKeys.TimeWindow).ShouldBe(ReceiptCheckResults.Fail);
    }

    [Fact]
    public void Without_an_order_or_extraction_it_never_auto_approves()
    {
        Evaluate(null).Status.ShouldBe(ReceiptStatuses.NeedsReview);
        var noOrder = ReceiptRules.Evaluate(Fields(), null, Accounts, ReceiptDuplicates.None, Settings(), true, OrderAt);
        noOrder.Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Result(noOrder, ReceiptCheckKeys.Order).ShouldBe(ReceiptCheckResults.Unknown);
        Evaluate(Fields() with { IsReceipt = false }).Status.ShouldBe(ReceiptStatuses.NeedsReview);
        Evaluate(Fields(reference: null)).Status.ShouldBe(ReceiptStatuses.NeedsReview);
    }

    [Theory]
    [InlineData("6037-99**-****-7893", true)]
    [InlineData("۶۰۳۷۹۹۱۲۳۴۵۶۷۸۹۳", true)]
    [InlineData("****7893", true)]
    [InlineData("6037 99xx xxxx 7893", true)]
    [InlineData("6037-99**-****-7894", false)]
    [InlineData("**93", false)]
    [InlineData("", false)]
    public void Masked_cards_match_on_the_printed_digits(string printed, bool matches) =>
        ReceiptRules.CardMatches(printed, "6037991234567893").ShouldBe(matches);

    [Fact]
    public void Sheba_matches_when_printed_in_full_or_masked()
    {
        ReceiptRules.ShebaMatches("IR82 0540 1026 8002 0817 9090 02", "IR820540102680020817909002").ShouldBeTrue();
        ReceiptRules.ShebaMatches("IR8205401026****0817909002", "IR820540102680020817909002").ShouldBeTrue();
        ReceiptRules.ShebaMatches("IR820540102680020817909003", "IR820540102680020817909002").ShouldBeFalse();
    }

    [Fact]
    public void References_keep_digits_only()
    {
        ReceiptRules.CleanReference("شماره پیگیری: ۱۲۳۴۵۶-۷۸۹").ShouldBe("123456789");
        ReceiptRules.CleanReference("12345").ShouldBeNull();
        ReceiptRules.CleanReference(null).ShouldBeNull();
    }

    [Fact]
    public void The_models_answer_is_read_with_toman_as_rial_and_persian_digits()
    {
        var fields = ReceiptExtractor.Parse("""
            {"is_receipt": true, "amount": "۴۵۰,۰۰۰", "currency": "toman", "paid_at": "2026-11-04T11:50:00+03:30", "reference": "۱۲۳۴۵۶۷۸۹",
             "source_card": "5022-29**-****-1111", "destination_card": "6037-99**-****-7893", "destination_sheba": null, "bank": "ملت", "confidence": 0.93}
            """)!;
        fields.Amount.ShouldBe(4_500_000);
        fields.PaidAt.ShouldBe(new DateTimeOffset(2026, 11, 4, 8, 20, 0, TimeSpan.Zero));
        fields.Reference.ShouldBe("123456789");
        fields.Confidence.ShouldBe(0.93);
        fields.IsReceipt.ShouldBeTrue();

        ReceiptExtractor.Parse("""{"is_receipt": false, "confidence": 7}""")!.ShouldSatisfyAllConditions(f => f.IsReceipt.ShouldBeFalse(), f => f.Confidence.ShouldBe(1));
        ReceiptExtractor.Parse("not json").ShouldBeNull();
        ReceiptExtractor.Parse("[]").ShouldBeNull();
    }

    [Fact]
    public void The_same_picture_resized_hashes_close_and_a_different_one_far()
    {
        var a = Png(400, 300, invert: false);
        var b = Png(200, 150, invert: false);
        var c = Png(400, 300, invert: true);
        var ha = ImageHash.Compute(a)!.Value;
        ReceiptRules.Distance(ha, ImageHash.Compute(b)!.Value).ShouldBeLessThanOrEqualTo(ReceiptRules.SimilarDistance);
        ReceiptRules.Distance(ha, ImageHash.Compute(c)!.Value).ShouldBeGreaterThan(ReceiptRules.SimilarDistance);
        ImageHash.Compute([1, 2, 3]).ShouldBeNull();
    }

    private static byte[] Png(int width, int height, bool invert)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            for (var x = 0; x < 9; x++)
            {
                var shade = (byte)(invert ? 255 - (x * 28) : x * 28);
                using var paint = new SKPaint { Color = new SKColor(shade, shade, shade) };
                canvas.DrawRect(x * width / 9f, 0, width / 9f + 1, height, paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 90).ToArray();
    }
}
