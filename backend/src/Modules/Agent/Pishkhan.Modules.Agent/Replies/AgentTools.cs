using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Replies;

/// <summary>
/// The tools the reply model may call (backend plan 10.2 step 6), scoped to the current workspace through the catalog's queries. Tool
/// results are the only source of prices and stock: every result is added to the guard's allowed facts. <c>create_order</c> saves the
/// conversation's draft order (catalog prices only) in a live conversation; in the test chat and the demo it only prices the lines.
/// </summary>
internal sealed class AgentTools
{
    public const int MaxResultLength = 4_000;
    private const int MaxProducts = 5;

    // Persian stays readable for the model and in traces (fewer tokens than \u escapes); the results are never embedded in HTML.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const int MaxOrderLines = 10;

    private readonly ICatalogQueries _catalog;
    private readonly IOrderCommands _orders;
    private readonly ConversationSnapshot _conversation;
    private readonly bool _live;

    /// <param name="live">False in the test chat and the demo: <c>create_order</c> prices the lines and saves nothing.</param>
    public AgentTools(ICatalogQueries catalog, IOrderCommands orders, ConversationSnapshot conversation, bool live)
    {
        _catalog = catalog;
        _orders = orders;
        _conversation = conversation;
        _live = live;
        Functions =
        [
            AIFunctionFactory.Create(SearchProductsAsync, "search_products", "Search the shop's active products by words from the customer's message. Returns prices in Toman and stock."),
            AIFunctionFactory.Create(GetProductAsync, "get_product", "Get one product with all its variants, prices in Toman and stock."),
            AIFunctionFactory.Create(CheckStockAsync, "check_stock", "Check whether a variant of a product is in stock."),
            AIFunctionFactory.Create(CreateOrderAsync, "create_order", "Save the customer's order as a draft for the shop to confirm, once they chose products, variants and quantities. Prices come from the catalog. Returns the order number, lines, total in Toman and what is still missing."),
            AIFunctionFactory.Create(RequestHandoff, "request_handoff", "Hand the conversation to a person when the customer needs something you cannot do."),
            AIFunctionFactory.Create(AddUnansweredQuestion, "add_unanswered_question", "Record a customer question the facts do not answer, so the owner can add the answer."),
        ];
    }

    public IList<AITool> Functions { get; }

    public List<TraceToolCall> Calls { get; } = [];

    /// <summary>Set when the model asked for a person.</summary>
    public string? HandoffReason { get; private set; }

    public string? UnansweredQuestion { get; private set; }

    /// <summary>True when a catalog tool returned at least one product.</summary>
    public bool FoundData { get; private set; }

    /// <summary>The draft order saved by <c>create_order</c> in this run, if any.</summary>
    public Guid? OrderId { get; private set; }

    public async Task<string> InvokeAsync(FunctionCallContent call, CancellationToken ct)
    {
        var arguments = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), Json);
        var function = Functions.OfType<AIFunction>().FirstOrDefault(f => f.Name == call.Name);
        if (function is null)
        {
            Calls.Add(new TraceToolCall(call.Name, arguments, null, "unknown_tool"));
            return """{"error":"unknown tool"}""";
        }

        try
        {
            var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            var text = result switch
            {
                null => "null",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "null",
                JsonElement e => e.GetRawText(),
                _ => JsonSerializer.Serialize(result, Json),
            };
            Calls.Add(new TraceToolCall(call.Name, arguments, text.Length <= MaxResultLength ? text : text[..MaxResultLength], null));
            return text;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException)
        {
            Calls.Add(new TraceToolCall(call.Name, arguments, null, ex.GetType().Name));
            return JsonSerializer.Serialize(new { error = "invalid arguments" }, Json);
        }
    }

    [Description("Search products")]
    private async Task<string> SearchProductsAsync([Description("Words to search, for example «کیف چرم»")] string query, CancellationToken ct)
    {
        var products = string.IsNullOrWhiteSpace(query) ? [] : await _catalog.SearchAsync(query, MaxProducts, ct);
        FoundData |= products.Count > 0;
        return JsonSerializer.Serialize(new { products = products.Select(View) }, Json);
    }

    private async Task<string> GetProductAsync([Description("The product id from search_products")] string productId, CancellationToken ct)
    {
        var product = Guid.TryParse(productId, out var id) ? await _catalog.GetProductAsync(id, ct) : null;
        if (product is not { Status: ProductStatuses.Active })
        {
            return JsonSerializer.Serialize(new { error = "not found" }, Json);
        }

        FoundData = true;
        return JsonSerializer.Serialize(View(product), Json);
    }

    private async Task<string> CheckStockAsync(
        [Description("The product id")] string productId, [Description("The variant id, or empty for the whole product")] string? variantId, CancellationToken ct)
    {
        var product = Guid.TryParse(productId, out var id) ? await _catalog.GetProductAsync(id, ct) : null;
        if (product is not { Status: ProductStatuses.Active })
        {
            return JsonSerializer.Serialize(new { error = "not found" }, Json);
        }

        FoundData = true;
        var variant = Guid.TryParse(variantId, out var vid) ? product.Variants.FirstOrDefault(v => v.Id == vid) : null;
        return variant is null
            ? JsonSerializer.Serialize(new { productId = product.Id, product.Title, product.InStock }, Json)
            : JsonSerializer.Serialize(new { productId = product.Id, variantId = variant.Id, product.Title, variant.Attributes, variant.InStock, variant.Stock }, Json);
    }

    private async Task<string> CreateOrderAsync(
        [Description("The products to order: productId from search_products, variantId (empty when the product has no variants) and quantity")] OrderToolItem[] items,
        [Description("The customer's name, if they gave it")] string? customerName,
        [Description("The customer's mobile number, if they gave it")] string? phone,
        [Description("The delivery address as the customer wrote it, with city and postal code if given")] string? address,
        [Description("Anything else the customer asked for, such as gift wrapping")] string? note,
        CancellationToken ct)
    {
        var lines = new List<DraftOrderItem>();
        foreach (var item in (items ?? []).Take(MaxOrderLines))
        {
            if (!Guid.TryParse(item.ProductId, out var productId))
            {
                return JsonSerializer.Serialize(new { error = "product_not_found", productId = item.ProductId }, Json);
            }

            Guid? variantId = Guid.TryParse(item.VariantId, out var v) ? v : null;
            lines.Add(new DraftOrderItem(productId, variantId, item.Quantity));
        }

        if (lines.Count == 0)
        {
            return JsonSerializer.Serialize(new { error = "items_required" }, Json);
        }

        var result = _live
            ? await _orders.SaveDraftAsync(new DraftOrderRequest(_conversation.Id, _conversation.ContactId, lines, customerName, phone, address, note), ct)
            : await _orders.QuoteAsync(lines, ct);
        if (!result.Ok)
        {
            return JsonSerializer.Serialize(new { error = result.Error!.Replace("orders.", string.Empty, StringComparison.Ordinal), productId = result.ErrorDetail }, Json);
        }

        FoundData = true;
        OrderId = result.OrderId;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            saved = _live,
            orderNumber = result.Number is { } n ? PersianDigits.ToPersian(n.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            status = "draft, the shop will confirm it and send payment details",
            lines = result.Lines.Select(l => new
            {
                title = l.Title,
                variant = l.VariantLabel,
                quantity = l.Quantity,
                unitPrice = PersianMoney.FormatToman(l.UnitPrice),
                lineTotal = PersianMoney.FormatToman(l.LineTotal),
            }),
            total = PersianMoney.FormatToman(result.Total),
            totalToman = result.Total.ToToman(),
            missing = result.Missing,
        }, Json);
    }

    private string RequestHandoff([Description("Why a person is needed, in a few words")] string reason)
    {
        HandoffReason = string.IsNullOrWhiteSpace(reason) ? "ai_request" : reason.Trim();
        return """{"ok":true}""";
    }

    private string AddUnansweredQuestion([Description("The customer's question")] string question)
    {
        UnansweredQuestion = string.IsNullOrWhiteSpace(question) ? null : question.Trim();
        return """{"ok":true}""";
    }

    /// <summary>One line of <c>create_order</c>.</summary>
    internal sealed record OrderToolItem(
        [property: Description("The product id")] string ProductId,
        [property: Description("The variant id, or empty")] string? VariantId,
        [property: Description("How many, at least 1")] int Quantity);

    private static object View(ProductSummary p) => new
    {
        id = p.Id,
        title = p.Title,
        description = p.Description is { Length: > 300 } d ? d[..300] : p.Description,
        price = Price(p.MinPrice, p.MaxPrice),
        priceToman = p.MinPrice?.ToToman(),
        inStock = p.InStock,
        link = p.LinkUrl?.ToString(),
        variants = p.Variants.Take(20).Select(v => new
        {
            id = v.Id,
            attributes = v.Attributes,
            price = PersianMoney.FormatToman(v.Price),
            priceToman = v.Price.ToToman(),
            inStock = v.InStock,
            stock = v.Stock,
        }),
    };

    private static string? Price(Money? min, Money? max) => (min, max) switch
    {
        ({ } a, { } b) when a.Amount != b.Amount => $"{PersianMoney.FormatToman(a)} تا {PersianMoney.FormatToman(b)}",
        ({ } a, _) => PersianMoney.FormatToman(a),
        _ => null,
    };
}
