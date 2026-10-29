using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Domain;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Catalog.Services;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Catalog.Features;

public sealed record CategoryRequest(string Name, Guid? ParentId, int SortOrder = 0);

public sealed record CategoryResponse(Guid Id, string Name, Guid? ParentId, int SortOrder);

/// <summary><c>id</c> keeps an existing variant (otherwise matched by SKU, then by the same attributes). <c>price</c> null uses the product's price.</summary>
public sealed record VariantRequest(Guid? Id, string? Sku, IReadOnlyDictionary<string, string>? Attributes, Money? Price, int? Stock, string? StockPolicy);

/// <summary>
/// Prices are <c>{ amount, currency }</c> in Rial. An active product needs a price, on itself or on every variant.
/// <c>variants</c> replaces the product's variants: variants left out are removed.
/// </summary>
public sealed record ProductRequest(
    string Title, string? Description, Guid? CategoryId, IReadOnlyList<string>? Tags, string? Status, Money? Price,
    IReadOnlyList<Guid>? ImageFileIds, string? LinkUrl, IReadOnlyList<VariantRequest>? Variants);

public sealed record VariantResponse(Guid Id, string? Sku, IReadOnlyDictionary<string, string> Attributes, Money? Price, Money? EffectivePrice, int? Stock, string StockPolicy, bool InStock);

/// <summary><c>url</c> is a 5-minute download URL, only once the image is clean; <c>status</c> is the scan status.</summary>
public sealed record ProductImageRequest(Guid FileId);

/// <summary>Every image of the product, in the new order.</summary>
public sealed record ProductImageOrderRequest(IReadOnlyList<Guid>? FileIds);

public sealed record ProductImageResponse(Guid FileId, string Status, string? Url);

public sealed record ProductResponse(
    Guid Id, string Title, string? Description, Guid? CategoryId, IReadOnlyList<string> Tags, string Status, string PricingMode, Money? Price,
    Money? MinPrice, Money? MaxPrice, bool InStock, IReadOnlyList<ProductImageResponse> Images, string? LinkUrl, IReadOnlyList<VariantResponse> Variants,
    int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CardItemRequest(Guid ProductId, string? ButtonTitle);

public sealed record CardSetRequest(string Name, IReadOnlyList<CardItemRequest>? Items);

public sealed record CardSetResponse(Guid Id, string Name, IReadOnlyList<CardItem> Items, DateTimeOffset UpdatedAt);

public sealed record ImportRequest(Guid FileId);

public sealed record ImportResponse(
    Guid Id, Guid FileId, string Status, int Rows, int Created, int Updated, int Skipped, IReadOnlyList<ImportRowError> Errors, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

internal static class CatalogEndpoints
{
    private const string SheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int MaxExportProducts = 20_000;
    private static readonly TimeSpan ImageUrlLifetime = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        var root = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/catalog").WithTags("Catalog");

        root.MapGet("/categories", ListCategoriesAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .Produces<IReadOnlyList<CategoryResponse>>();

        root.MapPost("/categories", CreateCategoryAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapPut("/categories/{categoryId:guid}", UpdateCategoryAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces<CategoryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapDelete("/categories/{categoryId:guid}", DeleteCategoryAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Delete a category; its products keep no category")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapGet("/products", ListProductsAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("Products, most recently changed first")
            .WithDescription("Filters: `q` (title, description and tags, Persian normalized), `categoryId`, `tag`, `status`. Cursor pagination.")
            .Produces<Page<ProductResponse>>();

        root.MapPost("/products", CreateProductAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapGet("/products/{productId:guid}", GetProductAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/products/{productId:guid}", UpdateProductAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Replace a product and its variants")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        root.MapDelete("/products/{productId:guid}", DeleteProductAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPost("/products/{productId:guid}/images", AddImageAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Add an image to the end of a product's images")
            .WithDescription("`fileId` is a Files upload with purpose `product_image`. Adding an image the product already has changes nothing. At most 10 images.")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapDelete("/products/{productId:guid}/images/{fileId:guid}", RemoveImageAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Remove an image from a product")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/products/{productId:guid}/images/order", ReorderImagesAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Reorder a product's images")
            .WithDescription("`fileIds` must list every image of the product exactly once; the first is the cover.")
            .Produces<ProductResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapGet("/export", ExportAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("The catalog as an Excel sheet (Persian headers, one row per variant, prices in Toman)")
            .Produces(StatusCodes.Status200OK, contentType: SheetContentType);

        root.MapGet("/import-template", TemplateAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("An empty import sheet with the headers and one example row")
            .Produces(StatusCodes.Status200OK, contentType: SheetContentType);

        root.MapPost("/import", ImportAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Import an Excel sheet uploaded with purpose `import_sheet`")
            .WithDescription("The file must be clean (scanned). Rows are matched by `شناسه محصول`, then by `کد کالا`; other rows create products. Rows with errors are skipped and listed in the result. Poll `GET /catalog/imports/{id}`.")
            .Produces<ImportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        root.MapGet("/imports/{importId:guid}", GetImportAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .Produces<ImportResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapGet("/storefront-cards", ListCardSetsAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .Produces<IReadOnlyList<CardSetResponse>>();

        root.MapPost("/storefront-cards", CreateCardSetAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .WithSummary("Create a card set: up to 10 products sent as a carousel by rules (`send_cards`)")
            .Produces<CardSetResponse>(StatusCodes.Status201Created)
            .WithStandardProblems();

        root.MapGet("/storefront-cards/{cardSetId:guid}", GetCardSetAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .Produces<CardSetResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/storefront-cards/{cardSetId:guid}", UpdateCardSetAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces<CardSetResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapDelete("/storefront-cards/{cardSetId:guid}", DeleteCardSetAsync)
            .RequireWorkspacePermission(Permissions.CatalogWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapGet("/storefront-cards/{cardSetId:guid}/preview", PreviewCardSetAsync)
            .RequireWorkspacePermission(Permissions.CatalogRead)
            .WithSummary("The cards as customers will see them: active products only, texts cut to Instagram's limits")
            .Produces<StorefrontCardSetContent>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static CategoryResponse ToResponse(Category c) => new(c.Id, c.Name, c.ParentId, c.SortOrder);

    private static CardSetResponse ToResponse(StorefrontCardSet s) => new(s.Id, s.Name, s.Items, s.UpdatedAt);

    private static ImportResponse ToResponse(CatalogImport i) =>
        new(i.Id, i.FileId, i.Status, i.Rows, i.Created, i.Updated, i.Skipped, i.Errors, i.Error, i.CreatedAt, i.CompletedAt);

    private static async Task<IReadOnlyList<ProductResponse>> ToResponsesAsync(IReadOnlyList<Product> products, IFileService files, CancellationToken ct)
    {
        var ids = products.SelectMany(p => p.ImageFileIds).Distinct().ToList();
        var urls = await files.GetCleanUrlsAsync(ids, ImageUrlLifetime, ct);
        return [.. products.Select(p =>
        {
            var (min, max) = p.PriceRange();
            return new ProductResponse(
                p.Id, p.Title, p.Description, p.CategoryId, p.Tags, p.Status, p.PricingMode, p.Price is { } price ? Money.Rial(price) : null, min, max, p.InStock,
                [.. p.ImageFileIds.Select(f => urls.TryGetValue(f, out var u) ? new ProductImageResponse(f, FileStatuses.Clean, u.ToString()) : new ProductImageResponse(f, FileStatuses.PendingScan, null))],
                p.LinkUrl,
                [.. p.Variants.Where(v => v.DeletedAt is null).OrderBy(v => v.SortOrder).Select(v => new VariantResponse(
                    v.Id, v.Sku, v.Attributes, v.Price is { } vp ? Money.Rial(vp) : null, p.PriceOf(v), v.Stock, v.StockPolicy, v.InStock))],
                p.Revision, p.CreatedAt, p.UpdatedAt);
        })];
    }

    private static async Task<IResult> ListCategoriesAsync(Guid workspaceId, CatalogDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Take(1000).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> CreateCategoryAsync(Guid workspaceId, CategoryRequest request, CatalogDbContext db, IClock clock, CancellationToken ct)
    {
        if (await ValidateCategoryAsync(request, null, db, ct) is { } invalid)
        {
            return invalid;
        }

        var category = Category.Create(workspaceId, request.Name, request.ParentId, request.SortOrder, clock.UtcNow);
        db.Categories.Add(category);
        return await SaveAsync(db, ct) ?? Results.Json(ToResponse(category), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid workspaceId, Guid categoryId, CategoryRequest request, CatalogDbContext db, IClock clock, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (await ValidateCategoryAsync(request, categoryId, db, ct) is { } invalid)
        {
            return invalid;
        }

        category.Update(request.Name, request.ParentId, request.SortOrder, clock.UtcNow);
        return await SaveAsync(db, ct) ?? Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> DeleteCategoryAsync(Guid workspaceId, Guid categoryId, CatalogDbContext db, IClock clock, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        category.DeletedAt = now;
        await db.Products.Where(p => p.CategoryId == categoryId).ExecuteUpdateAsync(u => u.SetProperty(p => p.CategoryId, (Guid?)null), ct);
        await db.Categories.Where(c => c.ParentId == categoryId).ExecuteUpdateAsync(u => u.SetProperty(c => c.ParentId, (Guid?)null), ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateCategoryAsync(CategoryRequest request, Guid? self, CatalogDbContext db, CancellationToken ct)
    {
        var errors = CatalogValidator.ValidateCategory(request.Name).ToList();
        if (request.ParentId is { } parent && (parent == self || !await db.Categories.AnyAsync(c => c.Id == parent, ct)))
        {
            errors.Add(("parentId", "catalog.category_not_found"));
        }

        return errors.Count == 0 ? null : Invalid(errors);
    }

    private static async Task<IResult> ListProductsAsync(
        Guid workspaceId, string? q, Guid? categoryId, string? tag, string? status, string? cursor, int? limit, CatalogDbContext db, IFileService files, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            foreach (var word in PersianNormalizer.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5))
            {
                query = query.Where(p => p.SearchText.Contains(word));
            }
        }

        if (categoryId is { } category)
        {
            query = query.Where(p => p.CategoryId == category);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(p => p.Tags.Contains(tag.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status == status);
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(p => EF.Functions.LessThan(ValueTuple.Create(p.UpdatedAt, p.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id).Take(take + 1).AsSplitQuery().ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].UpdatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<ProductResponse>(await ToResponsesAsync([.. rows.Take(take)], files, ct), next));
    }

    private static async Task<IResult> GetProductAsync(Guid workspaceId, Guid productId, CatalogDbContext db, IFileService files, CancellationToken ct) =>
        await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct) is { } product
            ? Results.Ok((await ToResponsesAsync([product], files, ct))[0])
            : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateProductAsync(
        Guid workspaceId, ProductRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, IClock clock, CancellationToken ct)
    {
        var definition = Definition(request);
        if (await ValidateProductAsync(definition, db, ct) is { } invalid)
        {
            return invalid;
        }

        var product = Product.Create(workspaceId, definition, clock.UtcNow);
        db.Products.Add(product);
        CatalogChanges.Product(db, invalidator, product, ProductChangeKinds.Created);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.Json((await ToResponsesAsync([product], files, ct))[0], statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateProductAsync(
        Guid workspaceId, Guid productId, ProductRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, IClock clock,
        CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return Error.NotFound().ToProblem();
        }

        var definition = Definition(request);
        if (await ValidateProductAsync(definition, db, ct) is { } invalid)
        {
            return invalid;
        }

        product.Update(definition, clock.UtcNow);
        CatalogChanges.Product(db, invalidator, product, ProductChangeKinds.Updated);
        if (await SaveAsync(db, ct) is { } conflict)
        {
            return conflict;
        }

        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.Ok((await ToResponsesAsync([product], files, ct))[0]);
    }

    private static async Task<IResult> DeleteProductAsync(Guid workspaceId, Guid productId, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IClock clock, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return Error.NotFound().ToProblem();
        }

        product.Delete(clock.UtcNow);
        CatalogChanges.Product(db, invalidator, product, ProductChangeKinds.Deleted);
        await db.SaveChangesAsync(ct);
        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.NoContent();
    }

    private static Task<IResult> AddImageAsync(
        Guid workspaceId, Guid productId, ProductImageRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, IClock clock,
        CancellationToken ct) =>
        ChangeImagesAsync(workspaceId, productId, db, invalidator, cache, files, ct, product =>
            request.FileId == Guid.Empty ? Invalid([("fileId", "catalog.image_required")])
            : product.AddImage(request.FileId, clock.UtcNow) ? null
            : Invalid([("fileId", "catalog.too_many_images")]));

    private static Task<IResult> RemoveImageAsync(
        Guid workspaceId, Guid productId, Guid fileId, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, IClock clock, CancellationToken ct) =>
        ChangeImagesAsync(workspaceId, productId, db, invalidator, cache, files, ct, product =>
            product.RemoveImage(fileId, clock.UtcNow) ? null : Error.NotFound().ToProblem());

    private static Task<IResult> ReorderImagesAsync(
        Guid workspaceId, Guid productId, ProductImageOrderRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, IClock clock,
        CancellationToken ct) =>
        ChangeImagesAsync(workspaceId, productId, db, invalidator, cache, files, ct, product =>
            product.ReorderImages(request.FileIds ?? [], clock.UtcNow) ? null : Invalid([("fileIds", "catalog.images_mismatch")]));

    /// <summary>Applies an image change (which returns a problem to refuse it); a real change emits <c>catalog.product-changed.v1</c> like any update.</summary>
    private static async Task<IResult> ChangeImagesAsync(
        Guid workspaceId, Guid productId, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IFileService files, CancellationToken ct, Func<Product, IResult?> change)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
        {
            return Error.NotFound().ToProblem();
        }

        var revision = product.Revision;
        if (change(product) is { } refused)
        {
            return refused;
        }

        if (product.Revision != revision)
        {
            CatalogChanges.Product(db, invalidator, product, ProductChangeKinds.Updated);
            await db.SaveChangesAsync(ct);
            await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        }

        return Results.Ok((await ToResponsesAsync([product], files, ct))[0]);
    }

    private static ProductDefinition Definition(ProductRequest r) => new(
        r.Title ?? string.Empty,
        r.Description,
        r.CategoryId,
        r.Tags ?? [],
        r.Status ?? ProductStatuses.Active,
        r.Price?.Amount,
        r.ImageFileIds ?? [],
        r.LinkUrl,
        [.. (r.Variants ?? []).Select(v => new VariantDefinition(
            v.Id, v.Sku, v.Attributes ?? new Dictionary<string, string>(), v.Price?.Amount, v.Stock, v.StockPolicy ?? StockPolicies.Track))]);

    private static async Task<IResult?> ValidateProductAsync(ProductDefinition definition, CatalogDbContext db, CancellationToken ct)
    {
        var errors = CatalogValidator.Validate(definition).ToList();
        if (definition.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, ct))
        {
            errors.Add(("categoryId", "catalog.category_not_found"));
        }

        return errors.Count == 0 ? null : Invalid(errors);
    }

    private static async Task<IResult> ExportAsync(Guid workspaceId, CatalogDbContext db, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().OrderBy(p => p.CreatedAt).Take(MaxExportProducts).AsSplitQuery().ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var output = new MemoryStream();
        CatalogSheet.Write(output, products.SelectMany(p => CatalogSheet.ToRows(p, p.CategoryId is { } c ? categories.GetValueOrDefault(c) : null)));
        output.Position = 0;
        return Results.File(output, SheetContentType, $"catalog-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    private static IResult TemplateAsync(Guid workspaceId)
    {
        var output = new MemoryStream();
        CatalogSheet.Write(output, [
            new SheetRow(0, null, "مانتو کتان", "مانتو کتان تابستانی، قد ۹۰", "مانتو", ["تابستانه"], ProductStatuses.Active, "MK-RED-M",
                new Dictionary<string, string> { ["رنگ"] = "قرمز", ["سایز"] = "M" }, 2_500_000, 12, null),
        ]);
        output.Position = 0;
        return Results.File(output, SheetContentType, "catalog-template.xlsx");
    }

    private static async Task<IResult> ImportAsync(Guid workspaceId, ImportRequest request, CatalogDbContext db, IFileService files, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var file = await files.GetCleanAsync(request.FileId, ct);
        if (file is null)
        {
            return Error.Unprocessable("catalog.import_file_not_clean").ToProblem();
        }

        if (file.Purpose != FilePurposes.ImportSheet || file.ContentType != SheetContentType)
        {
            return Error.Unprocessable("catalog.import_file_invalid").ToProblem();
        }

        var import = CatalogImport.Queue(workspaceId, file.Id, caller.UserId, clock.UtcNow);
        db.Imports.Add(import);
        db.Publish(new CatalogImportRequested(import.Id), import.Id.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(import), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetImportAsync(Guid workspaceId, Guid importId, CatalogDbContext db, CancellationToken ct) =>
        await db.Imports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, ct) is { } import ? Results.Ok(ToResponse(import)) : Error.NotFound().ToProblem();

    private static async Task<IResult> ListCardSetsAsync(Guid workspaceId, CatalogDbContext db, CancellationToken ct) =>
        Results.Ok((await db.CardSets.AsNoTracking().OrderBy(c => c.Name).Take(500).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> GetCardSetAsync(Guid workspaceId, Guid cardSetId, CatalogDbContext db, CancellationToken ct) =>
        await db.CardSets.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardSetId, ct) is { } set ? Results.Ok(ToResponse(set)) : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateCardSetAsync(
        Guid workspaceId, CardSetRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IClock clock, CancellationToken ct)
    {
        var items = Items(request);
        if (await ValidateCardSetAsync(request.Name, items, db, ct) is { } invalid)
        {
            return invalid;
        }

        var set = StorefrontCardSet.Create(workspaceId, request.Name, items, clock.UtcNow);
        db.CardSets.Add(set);
        CatalogChanges.Cards(db, invalidator, workspaceId);
        await db.SaveChangesAsync(ct);
        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.Json(ToResponse(set), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateCardSetAsync(
        Guid workspaceId, Guid cardSetId, CardSetRequest request, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IClock clock, CancellationToken ct)
    {
        var set = await db.CardSets.FirstOrDefaultAsync(c => c.Id == cardSetId, ct);
        if (set is null)
        {
            return Error.NotFound().ToProblem();
        }

        var items = Items(request);
        if (await ValidateCardSetAsync(request.Name, items, db, ct) is { } invalid)
        {
            return invalid;
        }

        set.Update(request.Name, items, clock.UtcNow);
        CatalogChanges.Cards(db, invalidator, workspaceId);
        await db.SaveChangesAsync(ct);
        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.Ok(ToResponse(set));
    }

    private static async Task<IResult> DeleteCardSetAsync(
        Guid workspaceId, Guid cardSetId, CatalogDbContext db, ICacheInvalidator invalidator, HybridCache cache, IClock clock, CancellationToken ct)
    {
        var set = await db.CardSets.FirstOrDefaultAsync(c => c.Id == cardSetId, ct);
        if (set is null)
        {
            return Error.NotFound().ToProblem();
        }

        set.DeletedAt = clock.UtcNow;
        CatalogChanges.Cards(db, invalidator, workspaceId);
        await db.SaveChangesAsync(ct);
        await CatalogChanges.InvalidateLocalAsync(cache, workspaceId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PreviewCardSetAsync(Guid workspaceId, Guid cardSetId, CatalogDbContext db, IFileService files, CancellationToken ct)
    {
        var loaded = await CatalogQueries.LoadCardSetAsync(db, files, cardSetId, ct);
        return loaded.Found ? Results.Ok(loaded.Content) : Error.NotFound().ToProblem();
    }

    private static List<CardItem> Items(CardSetRequest request) => [.. (request.Items ?? []).Select(i => new CardItem(i.ProductId, string.IsNullOrWhiteSpace(i.ButtonTitle) ? null : i.ButtonTitle.Trim()))];

    private static async Task<IResult?> ValidateCardSetAsync(string? name, IReadOnlyList<CardItem> items, CatalogDbContext db, CancellationToken ct)
    {
        var errors = CatalogValidator.ValidateCardSet(name, items).ToList();
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        if (ids.Count > 0 && await db.Products.CountAsync(p => ids.Contains(p.Id), ct) != ids.Count)
        {
            errors.Add(("items", "catalog.product_not_found"));
        }

        return errors.Count == 0 ? null : Invalid(errors);
    }

    /// <summary>Saves; a unique violation (category name, SKU) is a 409.</summary>
    private static async Task<IResult?> SaveAsync(CatalogDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            return Error.Conflict(pg.ConstraintName?.Contains("sku", StringComparison.Ordinal) == true ? "catalog.sku_taken" : "catalog.category_exists").ToProblem();
        }
    }

    private static IResult Invalid(IReadOnlyList<(string Field, string Code)> errors) =>
        Error.Validation(errors[0].Code, errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray(), StringComparer.Ordinal)).ToProblem();
}
