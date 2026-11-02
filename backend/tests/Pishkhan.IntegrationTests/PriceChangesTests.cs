using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-2.6 bulk price update: the preview is exactly what apply does, a change in between is refused, undo puts back the prices nobody
/// has touched since, and the AI's knowledge follows the new prices.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PriceChangesTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _ = workers;
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static object Money(long toman) => new { amount = toman * 10, currency = "IRR" };

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    private static long Toman(JsonElement money) => money.GetProperty("amount").GetInt64() / 10;

    [Fact]
    public async Task Preview_equals_apply_a_stale_preview_is_refused_and_undo_restores_untouched_prices()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var catalog = $"/api/v1/workspaces/{ws}/catalog";
        var changes = $"{catalog}/price-changes";
        Task<JsonElement> Post(string url, object body, HttpStatusCode expected = HttpStatusCode.Created) => SendAsync(client, HttpMethod.Post, url, body, expected);
        async Task<JsonElement> Product(Guid id) => await SendAsync(client, HttpMethod.Get, $"{catalog}/products/{id}", null, HttpStatusCode.OK);

        var women = (await Post($"{catalog}/categories", new { name = "زنانه" })).GetProperty("id").GetGuid();
        var coats = (await Post($"{catalog}/categories", new { name = "مانتو", parentId = women })).GetProperty("id").GetGuid();
        var men = (await Post($"{catalog}/categories", new { name = "مردانه" })).GetProperty("id").GetGuid();
        var scarf = (await Post($"{catalog}/products", new { title = "شال نخی", status = "active", categoryId = women, price = Money(1_234_000) })).GetProperty("id").GetGuid();
        var coat = await Post($"{catalog}/products", new
        {
            title = "مانتو کتان",
            status = "active",
            categoryId = coats,
            price = Money(2_500_000),
            variants = new object[]
            {
                new { attributes = new Dictionary<string, string> { ["سایز"] = "M" }, stock = 2, stockPolicy = "track" },
                new { attributes = new Dictionary<string, string> { ["سایز"] = "XL" }, price = Money(2_740_000), stock = 2, stockPolicy = "track" },
            },
        });
        var coatId = coat.GetProperty("id").GetGuid();
        var xl = coat.GetProperty("variants")[1].GetProperty("id").GetGuid();
        var shirt = (await Post($"{catalog}/products", new { title = "پیراهن", status = "active", categoryId = men, price = Money(900_000) })).GetProperty("id").GetGuid();

        // Bad requests: no scope, a percent out of range, an unknown rounding step.
        await Post($"{changes}/preview", new { kind = "percent", percent = 10 }, HttpStatusCode.BadRequest);
        await Post($"{changes}/preview", new { scope = new { all = true }, kind = "percent", percent = 900 }, HttpStatusCode.BadRequest);
        await Post($"{changes}/preview", new { scope = new { all = true }, kind = "percent", percent = 10, roundTo = 3 }, HttpStatusCode.BadRequest);

        // +10% on the women's category and its subcategory, to the nearest 1,000 Toman: the shirt is left out.
        var request = new { scope = new { categoryId = women }, kind = "percent", percent = 10, roundTo = 1_000, roundMode = "nearest" };
        var preview = await Post($"{changes}/preview", request, HttpStatusCode.OK);
        preview.GetProperty("productCount").GetInt32().ShouldBe(2);
        preview.GetProperty("priceCount").GetInt32().ShouldBe(3);
        preview.GetProperty("invalid").GetArrayLength().ShouldBe(0);
        var planned = preview.GetProperty("items").EnumerateArray()
            .ToDictionary(i => (i.GetProperty("productId").GetGuid(), i.GetProperty("variantId").ValueKind == JsonValueKind.Null ? (Guid?)null : i.GetProperty("variantId").GetGuid()), i => Toman(i.GetProperty("newPrice")));
        planned[(scarf, null)].ShouldBe(1_357_000);
        planned[(coatId, null)].ShouldBe(2_750_000);
        planned[(coatId, xl)].ShouldBe(3_014_000);
        (await Product(scarf)).GetProperty("price").GetProperty("amount").GetInt64().ShouldBe(12_340_000); // a preview saves nothing

        // A product changes after the preview: applying that preview is refused.
        await SendAsync(client, HttpMethod.Put, $"{catalog}/products/{scarf}", new { title = "شال نخی", status = "active", categoryId = women, price = Money(1_300_000) }, HttpStatusCode.OK);
        (await Post(changes, new { request.scope, request.kind, request.percent, request.roundTo, request.roundMode, fingerprint = preview.GetProperty("fingerprint").GetString() }, HttpStatusCode.Conflict))
            .GetProperty("code").GetString().ShouldBe("catalog.price_change_stale");

        // A fresh preview, applied: the catalog has exactly the previewed prices.
        preview = await Post($"{changes}/preview", request, HttpStatusCode.OK);
        var batch = await Post(changes, new { request.scope, request.kind, request.percent, request.roundTo, request.roundMode, fingerprint = preview.GetProperty("fingerprint").GetString() });
        batch.GetProperty("priceCount").GetInt32().ShouldBe(3);
        batch.GetProperty("canUndo").GetBoolean().ShouldBeTrue();
        var batchId = batch.GetProperty("id").GetGuid();
        Toman((await Product(scarf)).GetProperty("price")).ShouldBe(1_430_000);
        var coatNow = await Product(coatId);
        Toman(coatNow.GetProperty("price")).ShouldBe(2_750_000);
        Toman(coatNow.GetProperty("variants")[1].GetProperty("price")).ShouldBe(3_014_000);
        coatNow.GetProperty("variants")[0].GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Null); // still follows the product's price
        Toman((await Product(shirt)).GetProperty("price")).ShouldBe(900_000);

        // The AI's knowledge follows the new price.
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var sources = await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/knowledge/sources?kind=product", null, HttpStatusCode.OK);
            if (sources.GetProperty("items").EnumerateArray().Any(s => s.GetProperty("productId").ValueKind == JsonValueKind.String && s.GetProperty("productId").GetGuid() == scarf
                    && s.GetProperty("body").GetString()!.Contains("۱٬۴۳۰٬۰۰۰ تومان", StringComparison.Ordinal)))
            {
                break;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "knowledge has the new price");
            await Task.Delay(250, Ct);
        }

        // History and detail.
        var history = await SendAsync(client, HttpMethod.Get, changes, null, HttpStatusCode.OK);
        history[0].GetProperty("id").GetGuid().ShouldBe(batchId);
        history[0].GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Null);
        var detail = await SendAsync(client, HttpMethod.Get, $"{changes}/{batchId}", null, HttpStatusCode.OK);
        detail.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("variantId").ValueKind == JsonValueKind.String).GetProperty("variantLabel").GetString().ShouldBe("سایز: XL");

        // The seller changes the coat by hand; undo restores the scarf and the XL price and leaves the coat's own price alone.
        var edited = coatNow.GetProperty("variants").EnumerateArray().Select(v => new
        {
            id = v.GetProperty("id").GetGuid(),
            attributes = v.GetProperty("attributes").Deserialize<Dictionary<string, string>>(),
            price = v.GetProperty("price").ValueKind == JsonValueKind.Null ? null : (object)new { amount = v.GetProperty("price").GetProperty("amount").GetInt64(), currency = "IRR" },
            stock = 2,
            stockPolicy = "track",
        }).ToArray();
        await SendAsync(client, HttpMethod.Put, $"{catalog}/products/{coatId}", new { title = "مانتو کتان", status = "active", categoryId = coats, price = Money(2_800_000), variants = edited }, HttpStatusCode.OK);
        var undone = await Post($"{changes}/{batchId}/undo", new { }, HttpStatusCode.OK);
        undone.GetProperty("undoSkipped").GetInt32().ShouldBe(1);
        undone.GetProperty("canUndo").GetBoolean().ShouldBeFalse();
        Toman((await Product(scarf)).GetProperty("price")).ShouldBe(1_300_000);
        coatNow = await Product(coatId);
        Toman(coatNow.GetProperty("price")).ShouldBe(2_800_000);
        Toman(coatNow.GetProperty("variants")[1].GetProperty("price")).ShouldBe(2_740_000);
        (await Post($"{changes}/{batchId}/undo", new { }, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("catalog.price_change_undone");
    }

    [Fact]
    public async Task A_change_that_would_reach_zero_or_changes_nothing_is_refused()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var catalog = $"/api/v1/workspaces/{ws}/catalog";
        var cheap = (await SendAsync(client, HttpMethod.Post, $"{catalog}/products", new { title = "جوراب", status = "active", tags = new[] { "حراج" }, price = Money(50_000) }, HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await SendAsync(client, HttpMethod.Post, $"{catalog}/products", new { title = "کیف", status = "active", tags = new[] { "حراج" }, price = Money(800_000) }, HttpStatusCode.Created);

        var lower = new { scope = new { tag = "حراج" }, kind = "amount", amount = Money(-100_000) };
        var preview = await SendAsync(client, HttpMethod.Post, $"{catalog}/price-changes/preview", lower, HttpStatusCode.OK);
        preview.GetProperty("invalid").EnumerateArray().Single().GetProperty("productId").GetGuid().ShouldBe(cheap);
        preview.GetProperty("priceCount").GetInt32().ShouldBe(1);
        (await SendAsync(client, HttpMethod.Post, $"{catalog}/price-changes", lower, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("catalog.price_change_below_zero");

        // Rounding 50,000 and 800,000 Toman to the nearest 1,000 after +0.01% changes nothing.
        var none = new { scope = new { tag = "حراج" }, kind = "percent", percent = 0.01m, roundTo = 1_000 };
        (await SendAsync(client, HttpMethod.Post, $"{catalog}/price-changes", none, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("catalog.price_change_empty");
    }
}
