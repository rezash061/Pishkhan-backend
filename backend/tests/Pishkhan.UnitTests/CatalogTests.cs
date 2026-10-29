using System.Text.Json;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Engine;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.UnitTests;

/// <summary>Catalog (BE-1.6): product rules, variants, the Excel format and storefront cards.</summary>
public class CatalogTests
{
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static VariantDefinition Variant(string? sku, string color, long? price, int? stock = 5, string policy = StockPolicies.Track) =>
        new(null, sku, new Dictionary<string, string> { ["رنگ"] = color }, price, stock, policy);

    private static ProductDefinition Definition(long? price = 2_500_000, params VariantDefinition[] variants) =>
        new("مانتو کتان", "مانتو تابستانی", null, ["تابستانه"], ProductStatuses.Active, price, [], null, variants);

    [Fact]
    public void A_valid_product_has_no_errors() =>
        CatalogValidator.Validate(Definition(2_500_000, Variant("R", "قرمز", null), Variant("B", "آبی", 2_700_000))).ShouldBeEmpty();

    [Theory]
    [InlineData("", "title", "validation.required")]
    [InlineData(null, "price", "catalog.price_invalid")]
    public void Invalid_fields_are_reported(string? title, string field, string code)
    {
        var d = Definition() with { Title = title ?? "x", Price = title is null ? -10 : 1000 };
        CatalogValidator.Validate(d).ShouldContain((field, code));
    }

    [Fact]
    public void An_active_product_needs_a_price_somewhere()
    {
        CatalogValidator.Validate(Definition(null)).ShouldContain(("price", "catalog.price_required"));
        CatalogValidator.Validate(Definition(null, Variant("R", "قرمز", 1000), Variant("B", "آبی", null))).ShouldContain(("price", "catalog.price_required"));
        CatalogValidator.Validate(Definition(null, Variant("R", "قرمز", 1000), Variant("B", "آبی", 2000))).ShouldBeEmpty();
        CatalogValidator.Validate(Definition(null) with { Status = ProductStatuses.Draft }).ShouldBeEmpty();
    }

    [Fact]
    public void Variants_need_distinct_skus_and_attributes()
    {
        CatalogValidator.Validate(Definition(1000, Variant("R", "قرمز", null), Variant("R", "آبی", null))).ShouldContain(("variants.sku", "catalog.sku_duplicate"));
        CatalogValidator.Validate(Definition(1000, Variant("R", "قرمز", null), Variant("B", "قرمز", null))).ShouldContain(("variants.attributes", "catalog.variant_duplicate"));
        CatalogValidator.Validate(Definition(1000, Variant("R", "قرمز", null), new VariantDefinition(null, "X", new Dictionary<string, string>(), null, 1, StockPolicies.Track)))
            .ShouldContain(("variants.attributes", "catalog.variant_needs_attributes"));
    }

    [Fact]
    public void Price_range_and_stock_follow_the_variants_for_sale()
    {
        var product = Product.Create(Ws, Definition(2_000_000, Variant("R", "قرمز", null, stock: 0), Variant("B", "آبی", 3_000_000), Variant("G", "سبز", 2_500_000)), Now);
        var (min, max) = product.PriceRange();
        min.ShouldBe(Money.Rial(2_500_000)); // the out-of-stock red one (product price) does not count
        max.ShouldBe(Money.Rial(3_000_000));
        product.InStock.ShouldBeTrue();

        product.Update(Definition(2_000_000, Variant("R", "قرمز", null, stock: 0)), Now.AddMinutes(1));
        product.InStock.ShouldBeFalse();
        product.PriceRange().Min.ShouldBe(Money.Rial(2_000_000));
    }

    [Fact]
    public void Updating_keeps_variant_ids_by_sku_or_attributes()
    {
        var product = Product.Create(Ws, Definition(1000, Variant("R", "قرمز", null), Variant(null, "آبی", null)), Now);
        var red = product.Variants.Single(v => v.Sku == "R").Id;
        var blue = product.Variants.Single(v => v.Sku is null).Id;

        product.Update(Definition(1000, Variant(null, "آبی", 900, stock: 2), Variant("R", "قرمز", 1100)), Now.AddMinutes(1));

        var live = product.Variants.Where(v => v.DeletedAt is null).ToList();
        live.Count.ShouldBe(2);
        live.Single(v => v.Sku == "R").Id.ShouldBe(red);
        live.Single(v => v.Sku is null).Id.ShouldBe(blue);
        live.Single(v => v.Sku is null).Stock.ShouldBe(2);
        product.Revision.ShouldBe(2);

        product.Update(Definition(1000, Variant("R", "قرمز", null)), Now.AddMinutes(2));
        product.Variants.Single(v => v.Id == blue).DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Excel_export_reads_back_to_the_same_products()
    {
        var shirt = Product.Create(Ws, Definition(2_500_000, Variant("MK-R", "قرمز", null, 12), Variant("MK-B", "آبی", 2_700_000, null, StockPolicies.Unlimited)), Now);
        var mug = Product.Create(Ws, new ProductDefinition("ماگ سرامیکی", null, null, [], ProductStatuses.Draft, 450_000, [], "https://shop.example/mug", []), Now);
        using var file = new MemoryStream();
        CatalogSheet.Write(file, [.. CatalogSheet.ToRows(shirt, "مانتو"), .. CatalogSheet.ToRows(mug, null)]);
        file.Position = 0;

        var read = CatalogSheet.Read(file);
        read.Error.ShouldBeNull();
        read.Errors.ShouldBeEmpty();
        read.Rows.Count.ShouldBe(3);
        read.Rows[0].Category.ShouldBe("مانتو");
        read.Rows[0].Price.ShouldBe(2_500_000);
        read.Rows[1].Stock.ShouldBeNull();

        var errors = new List<ImportRowError>();
        var groups = CatalogSheet.Group(read.Rows, errors);
        groups.Count.ShouldBe(2);

        var again = CatalogSheet.ToDefinition(groups[0], shirt, null);
        again.Variants.Count.ShouldBe(2);
        again.Variants[0].Sku.ShouldBe("MK-R");
        again.Variants[0].Stock.ShouldBe(12);
        again.Variants[1].StockPolicy.ShouldBe(StockPolicies.Unlimited);
        again.Variants[1].Price.ShouldBe(2_700_000);
        CatalogValidator.Validate(again).ShouldBeEmpty();

        var mugAgain = CatalogSheet.ToDefinition(groups[1], null, null);
        mugAgain.Status.ShouldBe(ProductStatuses.Draft);
        mugAgain.Price.ShouldBe(450_000);
        mugAgain.LinkUrl.ShouldBe("https://shop.example/mug");
        mugAgain.Variants.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("۲۵۰٬۰۰۰", 250_000L)]
    [InlineData("250,000", 250_000L)]
    [InlineData("٢٥٠٠٠٠", 250_000L)]
    [InlineData("1 200", 1_200L)]
    [InlineData("-5", null)]
    [InlineData("دویست", null)]
    public void Sheet_numbers_accept_persian_digits_and_separators(string text, long? expected) => CatalogSheet.ParseNumber(text).ShouldBe(expected);

    [Fact]
    public void A_sheet_without_the_required_headers_fails_whole()
    {
        using var file = new MemoryStream();
        using (var workbook = new ClosedXML.Excel.XLWorkbook())
        {
            workbook.AddWorksheet("x").Cell(1, 1).Value = "name";
            workbook.SaveAs(file);
        }

        file.Position = 0;
        CatalogSheet.Read(file).Error.ShouldBe("catalog.import_headers_missing");
        CatalogSheet.Read(new MemoryStream([1, 2, 3])).Error.ShouldBe("catalog.import_unreadable");
    }

    [Fact]
    public void Cards_show_toman_prices_stock_and_fit_instagram_limits()
    {
        var product = Product.Create(Ws, Definition(2_500_000, Variant("R", "قرمز", null), Variant("B", "آبی", 2_700_000)) with { Title = new string('ک', 120) }, Now);
        var card = CardRenderer.Render(product, new CardItem(product.Id, "خرید"), null);
        card.Title.Length.ShouldBe(CardRenderer.MaxText);
        card.Subtitle.ShouldBe("از ۲۵۰٬۰۰۰ تومان");
        card.ButtonTitle.ShouldBe("خرید");
        CatalogPayloads.TryParseProduct(card.Payload, out var id).ShouldBeTrue();
        id.ShouldBe(product.Id);

        product.Update(Definition(2_500_000, Variant("R", "قرمز", null, stock: 0)), Now.AddMinutes(1));
        CardRenderer.Render(product, new CardItem(product.Id), null).Subtitle.ShouldBe("۲۵۰٬۰۰۰ تومان · ناموجود");
        CardRenderer.Render(product, new CardItem(product.Id), null).ButtonTitle.ShouldBe(CardRenderer.DefaultButton);
    }

    [Fact]
    public void Card_sets_become_a_generic_template_and_details_come_from_the_catalog()
    {
        var link = new Uri("https://shop.example/p/1");
        var set = new StorefrontCardSetContent(Guid.NewGuid(), "پرفروش‌ها", [new StorefrontCard(Guid.NewGuid(), "مانتو", "۲۵۰٬۰۰۰ تومان", null, link, "اطلاعات بیشتر", "catalog.product:x")]);
        var content = CardMessages.ToContent(set);
        content.Text.ShouldBeNull();
        content.Cards!.Single().Buttons!.Select(b => b.Type).ShouldBe(["postback", "web_url"]);

        var product = Product.Create(Ws, Definition(2_500_000, Variant("R", "قرمز", null), Variant("B", "آبی", 2_700_000, stock: 0)), Now);
        var details = CardMessages.ProductDetails(CatalogQueries.Summarize(product, null)).Text!;
        details.ShouldContain("مانتو کتان");
        details.ShouldContain("رنگ: قرمز: ۲۵۰٬۰۰۰ تومان");
        details.ShouldContain("رنگ: آبی: ۲۷۰٬۰۰۰ تومان (ناموجود)");
    }

    [Fact]
    public void Money_json_uses_the_currency_code()
    {
        var json = JsonSerializer.Serialize(Money.Rial(25_000), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.ShouldBe("""{"amount":25000,"currency":"IRR"}""");
        JsonSerializer.Deserialize<Money>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBe(Money.Rial(25_000));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Money>("""{"amount":1,"currency":"USD"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void A_flow_storefront_node_can_send_a_card_set()
    {
        var cardSetId = Guid.NewGuid();
        var graph = new FlowGraph(
            "cards",
            [new FlowNode { Id = "cards", Type = FlowNodeTypes.Storefront, CardSetId = cardSetId }, new FlowNode { Id = "end", Type = FlowNodeTypes.End }],
            [new FlowEdge("cards", "end")]);
        FlowValidator.Validate(graph).IsValid.ShouldBeTrue();
        FlowValidator.Validate(graph with { Nodes = [new FlowNode { Id = "cards", Type = FlowNodeTypes.Storefront }, graph.Nodes[1]] }).IsValid.ShouldBeFalse();

        var run = FlowRun.Start(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), ChannelKinds.Instagram, "u1", null, Now);
        var context = new FlowContext(Now, true, [], new Dictionary<string, string>(), new Dictionary<string, string?>(), BusinessHours.AlwaysOpen);
        FlowRunner.Start(graph, run, context).ShouldBe([new FlowEffect.SendCards(cardSetId)]);

        var rule = new RuleDefinition("x", null, TriggerKinds.DmKeyword, new TriggerConfig { Match = MatchModes.AnyOf, Keywords = ["a"] }, [new RuleAction { Kind = ActionKinds.SendCards }], 0, true);
        RuleValidator.Validate(rule).ShouldContain(e => e.Code == "automation.card_set_required");
    }
}
