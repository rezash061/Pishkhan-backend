using System.Globalization;
using ClosedXML.Excel;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Catalog.Services;

/// <summary>One row of the sheet: a product, or one variant of it. Prices are Toman in the sheet and Rial here.</summary>
public sealed record SheetRow(
    int Row, Guid? ProductId, string? Title, string? Description, string? Category, IReadOnlyList<string> Tags, string? Status,
    string? Sku, IReadOnlyDictionary<string, string> Attributes, long? Price, int? Stock, string? LinkUrl);

public sealed record SheetReadResult(IReadOnlyList<SheetRow> Rows, IReadOnlyList<ImportRowError> Errors, string? Error);

/// <summary>
/// The catalog Excel format (backend plan 9.8): one sheet, Persian headers, one row per variant (or per product without variants).
/// Product columns repeat on every row of the product; the first row's values win. Prices are in Toman; an empty stock is unlimited.
/// </summary>
public static class CatalogSheet
{
    public const int MaxRows = 5000;
    public const string SheetName = "محصولات";

    public static class Columns
    {
        public const string ProductId = "شناسه محصول";
        public const string Title = "عنوان";
        public const string Description = "توضیحات";
        public const string Category = "دسته‌بندی";
        public const string Tags = "برچسب‌ها";
        public const string Status = "وضعیت";
        public const string Sku = "کد کالا";
        public const string Attributes = "ویژگی‌ها";
        public const string Price = "قیمت (تومان)";
        public const string Stock = "موجودی";
        public const string Link = "لینک";
    }

    public static readonly IReadOnlyList<string> Headers =
    [
        Columns.ProductId, Columns.Title, Columns.Description, Columns.Category, Columns.Tags, Columns.Status,
        Columns.Sku, Columns.Attributes, Columns.Price, Columns.Stock, Columns.Link,
    ];

    private static readonly Dictionary<string, string> StatusNames = new(StringComparer.Ordinal)
    {
        [ProductStatuses.Active] = "فعال",
        [ProductStatuses.Draft] = "پیش‌نویس",
        [ProductStatuses.Archived] = "بایگانی",
    };

    /// <summary>Product and variants as sheet rows (Rial prices).</summary>
    public static IEnumerable<SheetRow> ToRows(Product product, string? category)
    {
        var variants = product.Variants.Where(v => v.DeletedAt is null).OrderBy(v => v.SortOrder).ToList();
        if (variants.Count == 0)
        {
            yield return new SheetRow(0, product.Id, product.Title, product.Description, category, product.Tags, product.Status, null, new Dictionary<string, string>(), product.Price, null, product.LinkUrl);
            yield break;
        }

        foreach (var v in variants)
        {
            yield return new SheetRow(
                0, product.Id, product.Title, product.Description, category, product.Tags, product.Status, v.Sku, v.Attributes,
                v.Price ?? product.Price, v.StockPolicy == StockPolicies.Unlimited ? null : v.Stock ?? 0, product.LinkUrl);
        }
    }

    public static void Write(Stream output, IEnumerable<SheetRow> rows)
    {
        using var workbook = new XLWorkbook { RightToLeft = true };
        var sheet = workbook.AddWorksheet(SheetName);
        sheet.RightToLeft = true;
        for (var c = 0; c < Headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = Headers[c];
        }

        sheet.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            sheet.Cell(r, 1).Value = row.ProductId?.ToString() ?? string.Empty;
            sheet.Cell(r, 2).Value = row.Title ?? string.Empty;
            sheet.Cell(r, 3).Value = row.Description ?? string.Empty;
            sheet.Cell(r, 4).Value = row.Category ?? string.Empty;
            sheet.Cell(r, 5).Value = string.Join("، ", row.Tags);
            sheet.Cell(r, 6).Value = row.Status is { } s && StatusNames.TryGetValue(s, out var name) ? name : string.Empty;
            sheet.Cell(r, 7).Value = row.Sku ?? string.Empty;
            sheet.Cell(r, 8).Value = string.Join("، ", row.Attributes.Select(a => $"{a.Key}: {a.Value}"));
            if (row.Price is { } price)
            {
                sheet.Cell(r, 9).Value = (price + 5) / 10; // Rial to Toman, rounded half up
            }

            if (row.Stock is { } stock)
            {
                sheet.Cell(r, 10).Value = stock;
            }

            sheet.Cell(r, 11).Value = row.LinkUrl ?? string.Empty;
            r++;
        }

        sheet.Column(9).Style.NumberFormat.Format = "#,##0";
        sheet.Columns().AdjustToContents(1, Math.Min(r, 200));
        sheet.SheetView.FreezeRows(1);
        workbook.SaveAs(output);
    }

    public static SheetReadResult Read(Stream input)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(input);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
        {
            return new SheetReadResult([], [], "catalog.import_unreadable");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null)
            {
                return new SheetReadResult([], [], "catalog.import_unreadable");
            }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cell in sheet.Row(1).CellsUsed())
            {
                map.TryAdd(NormalizeHeader(cell.GetString()), cell.Address.ColumnNumber);
            }

            if (!map.ContainsKey(NormalizeHeader(Columns.Title)) || !map.ContainsKey(NormalizeHeader(Columns.Price)))
            {
                return new SheetReadResult([], [], "catalog.import_headers_missing");
            }

            var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (last - 1 > MaxRows)
            {
                return new SheetReadResult([], [], "catalog.import_too_many_rows");
            }

            var rows = new List<SheetRow>();
            var errors = new List<ImportRowError>();
            for (var r = 2; r <= last; r++)
            {
                var row = sheet.Row(r);
                if (row.IsEmpty())
                {
                    continue;
                }

                string? Text(string column) =>
                    map.TryGetValue(NormalizeHeader(column), out var c) && row.Cell(c).GetFormattedString().Trim() is { Length: > 0 } t ? t : null;

                var rowErrors = new List<ImportRowError>();
                Guid? id = null;
                if (Text(Columns.ProductId) is { } idText)
                {
                    if (Guid.TryParse(PersianDigits.ToLatin(idText), out var parsed))
                    {
                        id = parsed;
                    }
                    else
                    {
                        rowErrors.Add(new ImportRowError(r, Columns.ProductId, "catalog.import_id_invalid"));
                    }
                }

                string? status = null;
                if (Text(Columns.Status) is { } statusText)
                {
                    status = ParseStatus(statusText);
                    if (status is null)
                    {
                        rowErrors.Add(new ImportRowError(r, Columns.Status, "catalog.status_invalid"));
                    }
                }

                long? price = null;
                if (Text(Columns.Price) is { } priceText)
                {
                    if (ParseNumber(priceText) is { } toman && toman >= 0 && toman <= CatalogValidator.MaxPrice / 10)
                    {
                        price = toman * 10;
                    }
                    else
                    {
                        rowErrors.Add(new ImportRowError(r, Columns.Price, "catalog.price_invalid"));
                    }
                }

                int? stock = null;
                if (Text(Columns.Stock) is { } stockText)
                {
                    if (ParseNumber(stockText) is { } n && n >= 0 && n <= CatalogValidator.MaxStock)
                    {
                        stock = (int)n;
                    }
                    else
                    {
                        rowErrors.Add(new ImportRowError(r, Columns.Stock, "catalog.stock_invalid"));
                    }
                }

                var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Text(Columns.Attributes) is { } attributeText)
                {
                    foreach (var part in SplitList(attributeText))
                    {
                        var colon = part.IndexOfAny([':', '：', '=']);
                        if (colon <= 0 || colon == part.Length - 1 || !attributes.TryAdd(part[..colon].Trim(), part[(colon + 1)..].Trim()))
                        {
                            rowErrors.Add(new ImportRowError(r, Columns.Attributes, "catalog.attributes_invalid"));
                            break;
                        }
                    }
                }

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    continue;
                }

                rows.Add(new SheetRow(
                    r, id, Text(Columns.Title), Text(Columns.Description), Text(Columns.Category),
                    Text(Columns.Tags) is { } tags ? SplitList(tags) : [], status, Text(Columns.Sku), attributes, price, stock, Text(Columns.Link)));
            }

            return new SheetReadResult(rows, errors, null);
        }
    }

    /// <summary>Groups rows into products: by product id when present, otherwise by title. Rows without either are errors.</summary>
    public static IReadOnlyList<IReadOnlyList<SheetRow>> Group(IEnumerable<SheetRow> rows, List<ImportRowError> errors)
    {
        var groups = new List<List<SheetRow>>();
        var byKey = new Dictionary<string, List<SheetRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = row.ProductId is { } id ? "id:" + id.ToString("N") : row.Title is { } t ? "title:" + PersianNormalizer.Normalize(t) : null;
            if (key is null)
            {
                errors.Add(new ImportRowError(row.Row, Columns.Title, "validation.required"));
                continue;
            }

            if (!byKey.TryGetValue(key, out var group))
            {
                group = [];
                byKey[key] = group;
                groups.Add(group);
            }

            group.Add(row);
        }

        return groups;
    }

    /// <summary>
    /// The product a group of rows describes. One row without SKU or attributes is a product without variants; otherwise
    /// every row is a variant. <paramref name="existing"/> keeps fields the sheet leaves empty and the variants' ids.
    /// </summary>
    public static ProductDefinition ToDefinition(IReadOnlyList<SheetRow> group, Product? existing, Guid? categoryId)
    {
        var first = group[0];
        var title = group.Select(r => r.Title).FirstOrDefault(t => t is not null) ?? existing?.Title ?? string.Empty;
        var status = group.Select(r => r.Status).FirstOrDefault(s => s is not null) ?? existing?.Status ?? ProductStatuses.Active;
        var tags = group.FirstOrDefault(r => r.Tags.Count > 0)?.Tags ?? existing?.Tags ?? [];
        var description = group.Select(r => r.Description).FirstOrDefault(d => d is not null) ?? existing?.Description;
        var link = group.Select(r => r.LinkUrl).FirstOrDefault(l => l is not null) ?? existing?.LinkUrl;
        var images = existing?.ImageFileIds ?? [];

        if (group.Count == 1 && first.Sku is null && first.Attributes.Count == 0)
        {
            var single = existing?.Variants.Where(v => v.DeletedAt is null).ToList() is [var only] ? only : null;
            IReadOnlyList<VariantDefinition> variants = first.Stock is null && single is null
                ? []
                : [new VariantDefinition(single?.Id, single?.Sku, single?.Attributes ?? new Dictionary<string, string>(), null, first.Stock, first.Stock is null ? StockPolicies.Unlimited : StockPolicies.Track)];
            return new ProductDefinition(title, description, categoryId, tags, status, first.Price ?? existing?.Price, images, link, variants);
        }

        // With variants the product price is the lowest one, so the product always has a price to show.
        var basePrice = group.Where(r => r.Price is not null).Select(r => r.Price).Min() ?? existing?.Price;
        var defs = group.Select(r => new VariantDefinition(
            null, r.Sku, r.Attributes, r.Price, r.Stock, r.Stock is null ? StockPolicies.Unlimited : StockPolicies.Track)).ToList();
        return new ProductDefinition(title, description, categoryId, tags, status, basePrice, images, link, defs);
    }

    private static string? ParseStatus(string text) => PersianNormalizer.Normalize(text) switch
    {
        var t when t == PersianNormalizer.Normalize("فعال") || t == "active" => ProductStatuses.Active,
        var t when t == PersianNormalizer.Normalize("پیش‌نویس") || t == PersianNormalizer.Normalize("پیشنویس") || t == "draft" => ProductStatuses.Draft,
        var t when t == PersianNormalizer.Normalize("بایگانی") || t == PersianNormalizer.Normalize("غیرفعال") || t == "archived" => ProductStatuses.Archived,
        _ => null,
    };

    /// <summary>Persian or Latin digits with any thousands separator.</summary>
    internal static long? ParseNumber(string text)
    {
        var latin = PersianDigits.ToLatin(text).Replace(",", string.Empty, StringComparison.Ordinal).Replace("٬", string.Empty, StringComparison.Ordinal)
            .Replace("،", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (latin.EndsWith(".0", StringComparison.Ordinal))
        {
            latin = latin[..^2];
        }

        return long.TryParse(latin, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static List<string> SplitList(string text) =>
        [.. text.Split(['،', ',', '|', '؛', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string NormalizeHeader(string header) => PersianNormalizer.Normalize(header).Replace(" ", string.Empty, StringComparison.Ordinal);
}
