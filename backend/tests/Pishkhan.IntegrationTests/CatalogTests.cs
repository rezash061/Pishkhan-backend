using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using SkiaSharp;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// Catalog end to end (BE-1.6): image upload and scan, products with variants, a card set sent as a carousel by a rule, the price change
/// reaching the next carousel, the card button's product details, and the Excel export/import round trip.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class CatalogTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private const string SheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed record Seeded(Guid WorkspaceId, Guid AccountId, string AccountExternalId, HttpClient Client)
    {
        public string Catalog => $"/api/v1/workspaces/{WorkspaceId}/catalog";
    }

    private async Task<Seeded> SeedAsync()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1785" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var account = ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(Ct);
        return new Seeded(ws, account.Id, externalId, client);
    }

    private async Task DmAsync(Seeded s, string customer, string? text, string? postback = null)
    {
        object item = postback is null
            ? new { sender = new { id = customer }, recipient = new { id = s.AccountExternalId }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text } }
            : new { sender = new { id = customer }, recipient = new { id = s.AccountExternalId }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), postback = new { mid = "m_" + Guid.NewGuid().ToString("N"), title = "اطلاعات بیشتر", payload = postback } };
        var entry = JsonSerializer.SerializeToElement(new { id = s.AccountExternalId, time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), messaging = new[] { item } });
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Publish(new InstagramWebhookReceived("instagram", s.AccountExternalId, entry, DateTimeOffset.UtcNow), s.AccountExternalId);
        await db.SaveChangesAsync(Ct);
    }

    private static List<JsonElement> MessagesTo(string customer) =>
        [.. FakeInstagram.Sent.Select(b => JsonDocument.Parse(b).RootElement)
            .Where(e => e.TryGetProperty("recipient", out var r) && r.TryGetProperty("id", out var id) && id.GetString() == customer)
            .Select(e => e.GetProperty("message"))];

    private static JsonElement[] Elements(JsonElement message) =>
        [.. message.GetProperty("attachment").GetProperty("payload").GetProperty("elements").EnumerateArray()];

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    private static Task<JsonElement> PostAsync(HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        SendAsync(client, HttpMethod.Post, url, body, expected);

    private static async Task Eventually(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(200, Ct);
        }
    }

    private static Task Eventually(Func<bool> condition, TimeSpan timeout, string because) => Eventually(() => Task.FromResult(condition()), timeout, because);

    /// <summary>Uploads through the API and waits for the scan (the fake scanner passes everything).</summary>
    private static async Task<Guid> UploadAsync(Seeded s, string purpose, byte[] bytes, string contentType, string name)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "file", name);
        var response = await s.Client.PostAsync($"/api/v1/workspaces/{s.WorkspaceId}/files?purpose={purpose}", form, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        await Eventually(
            async () => (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{s.WorkspaceId}/files/{id}", Ct)).GetProperty("status").GetString() == "clean",
            TimeSpan.FromSeconds(30),
            "file scanned");
        return id;
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private async Task<long> CountAsync(string sql, object args)
    {
        await using var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<long>(sql, args);
    }

    private static object Money(long toman) => new { amount = toman * 10, currency = "IRR" };

    [Fact]
    public async Task A_rule_sends_the_card_set_with_current_prices_and_the_button_answers_from_the_catalog()
    {
        var s = await SeedAsync();
        var image = await UploadAsync(s, "product_image", Png(), "image/png", "manto.png");
        var category = await PostAsync(s.Client, $"{s.Catalog}/categories", new { name = "مانتو" });

        var manto = await PostAsync(s.Client, $"{s.Catalog}/products", new
        {
            title = "مانتو کتان",
            description = "مانتو کتان تابستانی، قد ۹۰",
            categoryId = category.GetProperty("id").GetGuid(),
            tags = new[] { "تابستانه" },
            status = "active",
            price = Money(250_000),
            imageFileIds = new[] { image },
            variants = new object[]
            {
                new { sku = "MK-R", attributes = new Dictionary<string, string> { ["رنگ"] = "قرمز" }, stock = 3, stockPolicy = "track" },
                new { sku = "MK-B", attributes = new Dictionary<string, string> { ["رنگ"] = "آبی" }, price = Money(270_000), stock = 0, stockPolicy = "track" },
            },
        });
        var mantoId = manto.GetProperty("id").GetGuid();
        manto.GetProperty("minPrice").GetProperty("amount").GetInt64().ShouldBe(2_500_000);
        manto.GetProperty("inStock").GetBoolean().ShouldBeTrue();
        manto.GetProperty("images")[0].GetProperty("status").GetString().ShouldBe("clean");

        var draft = await PostAsync(s.Client, $"{s.Catalog}/products", new { title = "شال", status = "draft" });

        // A SKU belongs to one variant in the workspace.
        await PostAsync(s.Client, $"{s.Catalog}/products", new
        {
            title = "کپی",
            price = Money(1000),
            variants = new[] { new { sku = "MK-R", attributes = new Dictionary<string, string> { ["رنگ"] = "سبز" }, stock = 1, stockPolicy = "track" } },
        }, HttpStatusCode.Conflict);

        var set = await PostAsync(s.Client, $"{s.Catalog}/storefront-cards", new
        {
            name = "پرفروش‌ها",
            items = new object[] { new { productId = mantoId, buttonTitle = "قیمت و موجودی" }, new { productId = draft.GetProperty("id").GetGuid() } },
        });
        var setId = set.GetProperty("id").GetGuid();
        var preview = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/storefront-cards/{setId}/preview", Ct);
        preview.GetProperty("cards").GetArrayLength().ShouldBe(1); // drafts are not shown

        var rule = await PostAsync(s.Client, $"/api/v1/workspaces/{s.WorkspaceId}/automation/rules", new
        {
            name = "محصولات",
            channelAccountId = s.AccountId,
            triggerKind = "dm_keyword",
            trigger = new { match = "any_of", keywords = new[] { "محصولات" } },
            actions = new object[] { new { kind = "send_message", message = new { text = "پرفروش‌های ما:" } }, new { kind = "send_cards", cardSetId = setId } },
            priority = 0,
        });
        await PostAsync(s.Client, $"/api/v1/workspaces/{s.WorkspaceId}/automation/rules", new
        {
            name = "بدون کارت",
            triggerKind = "dm_keyword",
            trigger = new { match = "any_of", keywords = new[] { "x" } },
            actions = new object[] { new { kind = "send_cards", cardSetId = Guid.NewGuid() } },
        }, HttpStatusCode.BadRequest);
        await Task.Delay(1500, Ct); // the workers' rule cache hears about the new rule

        var buyer = "b_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(s, buyer, "محصولات رو ببینم");
        await Eventually(() => MessagesTo(buyer).Count == 2, TimeSpan.FromSeconds(30), "text and carousel");
        var messages = MessagesTo(buyer);
        messages[0].GetProperty("text").GetString().ShouldBe("پرفروش‌های ما:");
        var card = Elements(messages[1]).Single();
        card.GetProperty("title").GetString().ShouldBe("مانتو کتان");
        card.GetProperty("subtitle").GetString().ShouldBe("۲۵۰٬۰۰۰ تومان");
        card.GetProperty("image_url").GetString()!.ShouldContain("/usercontent/");
        var button = card.GetProperty("buttons")[0];
        button.GetProperty("title").GetString().ShouldBe("قیمت و موجودی");
        var payload = button.GetProperty("payload").GetString()!;

        // A price change reaches the next carousel (the card cache is invalidated) and is announced for Knowledge.
        var put = await SendAsync(s.Client, HttpMethod.Put, $"{s.Catalog}/products/{mantoId}", new
        {
            title = "مانتو کتان",
            description = "مانتو کتان تابستانی، قد ۹۰",
            categoryId = category.GetProperty("id").GetGuid(),
            status = "active",
            price = Money(230_000),
            imageFileIds = new[] { image },
            variants = new object[]
            {
                new { sku = "MK-R", attributes = new Dictionary<string, string> { ["رنگ"] = "قرمز" }, stock = 3, stockPolicy = "track" },
                new { sku = "MK-B", attributes = new Dictionary<string, string> { ["رنگ"] = "آبی" }, price = Money(260_000), stock = 2, stockPolicy = "track" },
            },
        }, HttpStatusCode.OK);
        put.GetProperty("revision").GetInt32().ShouldBe(2);
        (await CountAsync("SELECT count(*) FROM platform.outbox_messages WHERE type = 'catalog.product-changed' AND key = @key", new { key = s.WorkspaceId.ToString() })).ShouldBeGreaterThanOrEqualTo(2);
        await Task.Delay(1000, Ct);

        var second = "b_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(s, second, "محصولات");
        await Eventually(() => MessagesTo(second).Count == 2, TimeSpan.FromSeconds(30), "second carousel");
        Elements(MessagesTo(second)[1]).Single().GetProperty("subtitle").GetString().ShouldBe("از ۲۳۰٬۰۰۰ تومان");

        // The card's button answers with the product's details from the catalog.
        await DmAsync(s, buyer, null, payload);
        await Eventually(() => MessagesTo(buyer).Count == 3, TimeSpan.FromSeconds(30), "product details");
        var details = MessagesTo(buyer)[2].GetProperty("text").GetString()!;
        details.ShouldStartWith("مانتو کتان");
        details.ShouldContain("رنگ: قرمز: ۲۳۰٬۰۰۰ تومان");
        details.ShouldContain("رنگ: آبی: ۲۶۰٬۰۰۰ تومان");

        // Search and filters.
        var found = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/products?q={Uri.EscapeDataString("مانتو كتان")}", Ct);
        found.GetProperty("items").GetArrayLength().ShouldBe(1);
        var drafts = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/products?status=draft", Ct);
        drafts.GetProperty("items")[0].GetProperty("title").GetString().ShouldBe("شال");
        rule.GetProperty("actions")[1].GetProperty("cardSetId").GetGuid().ShouldBe(setId);
    }

    [Fact]
    public async Task Excel_export_edited_and_imported_updates_creates_and_reports_bad_rows()
    {
        var s = await SeedAsync();
        var mug = await PostAsync(s.Client, $"{s.Catalog}/products", new
        {
            title = "ماگ سرامیکی",
            price = Money(150_000),
            variants = new object[]
            {
                new { sku = "MUG-W", attributes = new Dictionary<string, string> { ["رنگ"] = "سفید" }, stock = 10, stockPolicy = "track" },
                new { sku = "MUG-B", attributes = new Dictionary<string, string> { ["رنگ"] = "مشکی" }, stock = 4, stockPolicy = "track" },
            },
        });
        var mugId = mug.GetProperty("id").GetGuid();

        var export = await s.Client.GetAsync($"{s.Catalog}/export", Ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe(SheetType);
        var bytes = await export.Content.ReadAsByteArrayAsync(Ct);

        // The owner edits the sheet: a new price for the black mug, a new product, and a row with a bad price.
        using var edited = new MemoryStream();
        using (var workbook = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = workbook.Worksheet(1);
            sheet.Cell(1, 2).GetString().ShouldBe("عنوان");
            sheet.LastRowUsed()!.RowNumber().ShouldBe(3);
            var black = sheet.RowsUsed().Single(r => r.Cell(7).GetString() == "MUG-B");
            black.Cell(9).Value = "۱۸۰٬۰۰۰";
            sheet.Cell(4, 2).Value = "دفترچه یادداشت";
            sheet.Cell(4, 4).Value = "لوازم التحریر";
            sheet.Cell(4, 9).Value = 90_000;
            sheet.Cell(5, 2).Value = "خودکار";
            sheet.Cell(5, 9).Value = "ارزان";
            workbook.SaveAs(edited);
        }

        var fileId = await UploadAsync(s, "import_sheet", edited.ToArray(), SheetType, "catalog.xlsx");
        var import = await PostAsync(s.Client, $"{s.Catalog}/import", new { fileId }, HttpStatusCode.Accepted);
        var importId = import.GetProperty("id").GetGuid();
        JsonElement result = default;
        await Eventually(
            async () => (result = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/imports/{importId}", Ct)).GetProperty("status").GetString() == "completed",
            TimeSpan.FromSeconds(30),
            "import completed");

        result.GetProperty("created").GetInt32().ShouldBe(1);
        result.GetProperty("updated").GetInt32().ShouldBe(1);
        result.GetProperty("skipped").GetInt32().ShouldBe(1);
        var error = result.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("row").GetInt32().ShouldBe(5);
        error.GetProperty("code").GetString().ShouldBe("catalog.price_invalid");

        var updated = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/products/{mugId}", Ct);
        var variants = updated.GetProperty("variants").EnumerateArray().ToList();
        variants.Count.ShouldBe(2);
        variants.Single(v => v.GetProperty("sku").GetString() == "MUG-B").GetProperty("effectivePrice").GetProperty("amount").GetInt64().ShouldBe(1_800_000);
        variants.Single(v => v.GetProperty("sku").GetString() == "MUG-W").GetProperty("stock").GetInt32().ShouldBe(10);
        mug.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).Order()
            .ShouldBe(variants.Select(v => v.GetProperty("id").GetGuid()).Order()); // ids survive the round trip

        var notebook = (await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/products?q={Uri.EscapeDataString("دفترچه")}", Ct)).GetProperty("items").EnumerateArray().Single();
        notebook.GetProperty("price").GetProperty("amount").GetInt64().ShouldBe(900_000);
        var categories = await s.Client.GetFromJsonAsync<JsonElement>($"{s.Catalog}/categories", Ct);
        categories.EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("لوازم التحریر");
        notebook.GetProperty("categoryId").GetGuid().ShouldBe(categories[0].GetProperty("id").GetGuid());

        // A file that is not an import sheet is refused.
        var png = await UploadAsync(s, "product_image", Png(), "image/png", "x.png");
        await PostAsync(s.Client, $"{s.Catalog}/import", new { fileId = png }, HttpStatusCode.UnprocessableEntity);
    }
}
