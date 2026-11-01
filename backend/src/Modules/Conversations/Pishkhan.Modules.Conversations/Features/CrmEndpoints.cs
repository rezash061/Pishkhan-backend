using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Conversations.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Conversations.Features;

/// <summary>A contact in the CRM. <c>lastSeenAt</c> is the customer's last message on any channel; <c>channels</c> are the accounts they wrote to.</summary>
public sealed record ContactResponse(
    Guid Id, string? DisplayName, string? Username, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<Guid> Channels,
    DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt);

public sealed record ContactConversation(Guid Id, Guid ChannelAccountId, string ChannelKind, string Status, DateTimeOffset? LastInboundAt, string? LastMessagePreview);

public sealed record ContactDetail(ContactResponse Contact, IReadOnlyList<ContactConversation> Conversations);

/// <summary>
/// Changes a contact. <c>tags</c>, when sent, replaces the whole set. <c>fields</c> is merged: a key with a value sets it, a key with null removes it.
/// A value for a defined field must fit its type (dates as yyyy-MM-dd, phones are stored as E.164, booleans as true/false).
/// </summary>
public sealed record UpdateContactRequest(string? DisplayName, bool ClearDisplayName, IReadOnlyList<string>? Tags, Dictionary<string, string?>? Fields);

/// <summary>A tag in use or defined. <c>defined</c> is false for a tag only automation or the inbox added; <c>contactCount</c> counts its contacts.</summary>
public sealed record TagResponse(string Name, string? Color, string? Description, bool Defined, int ContactCount);

public sealed record TagRequest(string? Color, string? Description);

public sealed record RenameTagRequest(string NewName);

public sealed record FieldDefinitionRequest(string? Key, string Label, string Type, IReadOnlyList<string>? Options, int SortOrder);

public sealed record FieldDefinitionResponse(Guid Id, string Key, string Label, string Type, IReadOnlyList<string> Options, int SortOrder);

public sealed record SegmentRequest(string Name, SegmentRule Rule);

public sealed record SegmentResponse(Guid Id, string Name, SegmentRule Rule, int ContactCount, DateTimeOffset UpdatedAt);

public sealed record SegmentPreviewResponse(int ContactCount);

/// <summary>The contact list's filters; <c>consent</c> keeps contacts whose latest decision for that channel (phone, sms, bale) is a grant.</summary>
public sealed record ContactFilter(string? Q, string? Tag, Guid? SegmentId, Guid? ChannelAccountId, bool? HasPhone, string? Consent);

internal static class CrmEndpoints
{
    private const int MaxPageSize = 100;

    private static readonly HashSet<string> Colors = new(StringComparer.Ordinal) { "gray", "red", "orange", "yellow", "green", "teal", "blue", "purple", "pink" };

    public static void Map(IEndpointRouteBuilder app)
    {
        var contacts = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/contacts").WithTags("Contacts");

        contacts.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Contacts, newest first; filter by text (name, username, phone, email), tag, segment, channel account, phone or consent")
            .Produces<Page<ContactResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        contacts.MapGet("/{contactId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("A contact with their conversations")
            .Produces<ContactDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        contacts.MapPatch("/{contactId:guid}", UpdateAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Change a contact's name, tags or fields")
            .Produces<ContactResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        var crm = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/crm").WithTags("Contacts");

        crm.MapGet("/tags", ListTagsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Tags defined or in use, by name")
            .Produces<IReadOnlyList<TagResponse>>();

        crm.MapPut("/tags/{name}", PutTagAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Define a tag or change its colour and description")
            .Produces<TagResponse>()
            .WithStandardProblems();

        crm.MapPost("/tags/{name}/rename", RenameTagAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Rename a tag on every contact; renaming onto an existing tag merges them")
            .Produces<TagResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        crm.MapDelete("/tags/{name}", DeleteTagAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Remove a tag from every contact and delete its definition")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        crm.MapGet("/fields", ListFieldsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Custom fields, in display order")
            .Produces<IReadOnlyList<FieldDefinitionResponse>>();

        crm.MapPost("/fields", CreateFieldAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .Produces<FieldDefinitionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        crm.MapPut("/fields/{fieldId:guid}", UpdateFieldAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Change a field's label, type, options or order (the key stays)")
            .Produces<FieldDefinitionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        crm.MapDelete("/fields/{fieldId:guid}", DeleteFieldAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("Delete a field definition; contacts keep their values")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        crm.MapGet("/segments", ListSegmentsAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Saved segments with their current contact counts")
            .Produces<IReadOnlyList<SegmentResponse>>();

        crm.MapPost("/segments/preview", PreviewSegmentAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Count the contacts a rule matches, without saving it")
            .Produces<SegmentPreviewResponse>()
            .WithStandardProblems();

        crm.MapPost("/segments", CreateSegmentAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .Produces<SegmentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        crm.MapPut("/segments/{segmentId:guid}", UpdateSegmentAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .Produces<SegmentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        crm.MapDelete("/segments/{segmentId:guid}", DeleteSegmentAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ---- Contacts ----

    private static async Task<IResult> ListAsync(
        Guid workspaceId, string? q, string? tag, Guid? segmentId, Guid? channelAccountId, bool? hasPhone, string? consent, string? cursor, int? limit,
        ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var filtered = await FilterAsync(db, workspaceId, new ContactFilter(q, tag, segmentId, channelAccountId, hasPhone, consent), clock.UtcNow, ct);
        if (filtered.Error is { } error)
        {
            return error;
        }

        var query = filtered.Query!;
        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(c => EF.Functions.LessThan(ValueTuple.Create(c.CreatedAt, c.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        var page = rows.Take(take).ToList();
        return Results.Ok(new Page<ContactResponse>(await ToResponsesAsync(db, page, ct), next));
    }

    /// <summary>The contacts matching the list filters (also used by the Excel export), or the problem with a filter.</summary>
    internal static async Task<(IQueryable<Contact>? Query, IResult? Error)> FilterAsync(
        ConversationsDbContext db, Guid workspaceId, ContactFilter filter, DateTimeOffset now, CancellationToken ct)
    {
        var query = db.Contacts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            if (filter.Q.Length > 100)
            {
                return (null, Invalid("q", "validation.too_long"));
            }

            var q = filter.Q;
            query = query.Where(c => SearchIds(db, workspaceId, q).Contains(c.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var name = filter.Tag.Trim();
            query = query.Where(c => c.Tags.Contains(name));
        }

        var conditions = new List<SegmentCondition>();
        if (filter.SegmentId is { } sid)
        {
            var segment = await db.Segments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (segment is null)
            {
                return (null, Error.NotFound("crm.segment.not_found").ToProblem());
            }

            var rule = ReadRule(segment);
            query = query.Where(c => SegmentIds(db, workspaceId, rule, now).Contains(c.Id));
        }

        if (filter.ChannelAccountId is { } account)
        {
            query = query.Where(c => db.ChannelIdentities.Any(i => i.ContactId == c.Id && i.ChannelAccountId == account));
        }

        if (filter.HasPhone is { } phone)
        {
            conditions.Add(new SegmentCondition("has_phone", "is", Value: JsonSerializer.SerializeToElement(phone)));
        }

        if (!string.IsNullOrEmpty(filter.Consent))
        {
            if (!ConsentChannels.All.Contains(filter.Consent))
            {
                return (null, Invalid("consent", "validation.consent.channel"));
            }

            conditions.Add(new SegmentCondition("consent", "granted", Value: JsonSerializer.SerializeToElement(filter.Consent)));
        }

        if (conditions.Count > 0)
        {
            var rule = new SegmentRule("all", conditions);
            query = query.Where(c => SegmentIds(db, workspaceId, rule, now).Contains(c.Id));
        }

        return (query, null);
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid contactId, ConversationsDbContext db, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null)
        {
            return Error.NotFound().ToProblem();
        }

        var conversations = await db.Conversations.AsNoTracking().Where(c => c.ContactId == contactId)
            .OrderByDescending(c => c.LastInboundAt).ThenByDescending(c => c.Id).Take(50)
            .Select(c => new ContactConversation(c.Id, c.ChannelAccountId, c.ChannelKind, c.Status, c.LastInboundAt, c.LastMessagePreview))
            .ToListAsync(ct);
        return Results.Ok(new ContactDetail((await ToResponsesAsync(db, [contact], ct))[0], conversations));
    }

    private static async Task<IResult> UpdateAsync(Guid workspaceId, Guid contactId, UpdateContactRequest request, ConversationsDbContext db, CancellationToken ct)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null)
        {
            return Error.NotFound().ToProblem();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.DisplayName is { } name && (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200))
        {
            errors["displayName"] = ["validation.contact.name"];
        }

        List<string>? tags = null;
        if (request.Tags is { } requested)
        {
            tags = [.. requested.Select(t => t?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal)];
            if (tags.Count > Contact.MaxTags || tags.Any(t => !ValidTag(t)))
            {
                errors["tags"] = ["validation.contact.tags"];
            }
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (request.Fields is { Count: > 0 } fields)
        {
            var keys = fields.Keys.ToList();
            var definitions = await db.ContactFields.AsNoTracking().Where(f => keys.Contains(f.Key)).ToDictionaryAsync(f => f.Key, StringComparer.Ordinal, ct);
            foreach (var (key, value) in fields)
            {
                if (!SegmentSql.FieldKey().IsMatch(key))
                {
                    errors[$"fields.{key}"] = ["validation.contact.field_key"];
                }
                else if (value is null)
                {
                    values[key] = null;
                }
                else if (Normalize(definitions.GetValueOrDefault(key), value) is { } normalized)
                {
                    values[key] = normalized;
                }
                else
                {
                    errors[$"fields.{key}"] = ["validation.contact.field_value"];
                }
            }

            var count = contact.Fields.Keys.Union(values.Where(v => v.Value is not null).Select(v => v.Key), StringComparer.Ordinal)
                .Count(k => !values.TryGetValue(k, out var v) || v is not null);
            if (count > Contact.MaxFields)
            {
                errors["fields"] = ["validation.contact.too_many_fields"];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (request.ClearDisplayName)
        {
            contact.Rename(null);
        }
        else if (request.DisplayName is { } newName)
        {
            contact.Rename(newName);
        }

        if (tags is not null)
        {
            contact.SetTags(tags);
        }

        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                contact.RemoveField(key);
            }
            else
            {
                contact.SetField(key, value);
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok((await ToResponsesAsync(db, [contact], ct))[0]);
    }

    /// <summary>A value fit for the field's type, normalised; null when it does not fit. Undefined fields take any text.</summary>
    private static string? Normalize(ContactFieldDefinition? definition, string value)
    {
        value = value.Trim();
        if (value.Length is 0 or > 500)
        {
            return null;
        }

        return definition?.Type switch
        {
            null or ContactFieldTypes.Text => value,
            ContactFieldTypes.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null,
            ContactFieldTypes.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            ContactFieldTypes.Boolean => bool.TryParse(value, out var b) ? (b ? "true" : "false") : null,
            ContactFieldTypes.Email => value.Length <= 254 && value.IndexOf('@', StringComparison.Ordinal) is > 0 and var at && at < value.Length - 1 && !value.Any(char.IsWhiteSpace) ? value.ToLowerInvariant() : null,
            ContactFieldTypes.Phone => PhoneNumber.TryParse(value, out var phone) ? phone.Value.E164 : null,
            ContactFieldTypes.Select => definition.Options.Contains(value, StringComparer.Ordinal) ? value : null,
            _ => null,
        };
    }

    private static async Task<List<ContactResponse>> ToResponsesAsync(ConversationsDbContext db, IReadOnlyList<Contact> contacts, CancellationToken ct)
    {
        var ids = contacts.Select(c => c.Id).ToList();
        var channels = (await db.ChannelIdentities.AsNoTracking().Where(i => ids.Contains(i.ContactId)).Select(i => new { i.ContactId, i.ChannelAccountId }).ToListAsync(ct))
            .ToLookup(i => i.ContactId, i => i.ChannelAccountId);
        var seen = await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.ContactId))
            .GroupBy(c => c.ContactId).Select(g => new { g.Key, At = g.Max(c => c.LastInboundAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        return [.. contacts.Select(c => new ContactResponse(
            c.Id, c.DisplayName, c.Username, c.Tags, c.Fields, [.. channels[c.Id].Distinct()], seen.GetValueOrDefault(c.Id), c.CreatedAt))];
    }

    /// <summary>Contacts whose name, username or email contains the text, or whose phone is that number in any spelling.</summary>
    private static IQueryable<Guid> SearchIds(ConversationsDbContext db, Guid workspaceId, string q)
    {
        var text = q.Trim().TrimStart('@');
        var pattern = SegmentSql.Like(text);
        var phone = PhoneNumber.TryParse(text, out var parsed) ? parsed.Value : (PhoneNumber?)null;
        var national = phone?.National ?? string.Empty;
        var e164 = phone?.E164 ?? string.Empty;
        return db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM conversations.contacts
            WHERE workspace_id = {workspaceId}
              AND (display_name ILIKE {pattern} OR username ILIKE {pattern} OR fields->>'email' ILIKE {pattern}
                   OR (fields->>'phone') IN ({national}, {e164}))
            """);
    }

    internal static IQueryable<Guid> SegmentIds(ConversationsDbContext db, Guid workspaceId, SegmentRule rule, DateTimeOffset now)
    {
        var parameters = new List<NpgsqlParameter> { new("ws", workspaceId) };
        // The filter is built only from fixed SQL and @parameters (SegmentSql never inlines a value).
        var sql = "SELECT c.id AS \"Value\" FROM conversations.contacts c WHERE c.workspace_id = @ws AND " + SegmentSql.ToWhere(rule, parameters, now);
        return db.Database.SqlQueryRaw<Guid>(sql, [.. parameters]);
    }

    // ---- Tags ----

    private static bool ValidTag(string tag) => tag.Length is > 0 and <= ContactTag.MaxName && !tag.Any(char.IsControl);

    private static async Task<Dictionary<string, int>> TagCountsAsync(ConversationsDbContext db, Guid workspaceId, string? only, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT t, count(*)::int FROM conversations.contacts c, unnest(c.tags) AS t
                WHERE c.workspace_id = @ws AND (@only::text IS NULL OR t = @only) GROUP BY t
                """, connection);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.Add(new NpgsqlParameter("only", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)only ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        return counts;
    }

    private static async Task<IResult> ListTagsAsync(Guid workspaceId, ConversationsDbContext db, CancellationToken ct)
    {
        var defined = await db.ContactTags.AsNoTracking().ToDictionaryAsync(t => t.Name, StringComparer.Ordinal, ct);
        var counts = await TagCountsAsync(db, workspaceId, null, ct);
        return Results.Ok(defined.Keys.Union(counts.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(n => ToTag(n, defined.GetValueOrDefault(n), counts.GetValueOrDefault(n))).ToList());
    }

    private static TagResponse ToTag(string name, ContactTag? definition, int count) =>
        new(name, definition?.Color, definition?.Description, definition is not null, count);

    private static async Task<IResult> PutTagAsync(Guid workspaceId, string name, TagRequest request, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        name = name.Trim();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!ValidTag(name))
        {
            errors["name"] = ["validation.tag.name"];
        }

        if (request.Color is { } color && !Colors.Contains(color))
        {
            errors["color"] = ["validation.tag.color"];
        }

        if (request.Description is { Length: > 200 })
        {
            errors["description"] = ["validation.too_long"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var tag = await db.ContactTags.FirstOrDefaultAsync(t => t.Name == name, ct);
        if (tag is null)
        {
            tag = ContactTag.Create(workspaceId, name, clock.UtcNow);
            db.ContactTags.Add(tag);
        }

        tag.Color = request.Color;
        tag.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToTag(name, tag, (await TagCountsAsync(db, workspaceId, name, ct)).GetValueOrDefault(name)));
    }

    private static async Task<IResult> RenameTagAsync(Guid workspaceId, string name, RenameTagRequest request, ConversationsDbContext db, CancellationToken ct)
    {
        var to = request.NewName?.Trim() ?? string.Empty;
        if (!ValidTag(to))
        {
            return Invalid("newName", "validation.tag.name");
        }

        var tag = await db.ContactTags.FirstOrDefaultAsync(t => t.Name == name, ct);
        var used = (await TagCountsAsync(db, workspaceId, name, ct)).GetValueOrDefault(name);
        if (tag is null && used == 0)
        {
            return Error.NotFound("crm.tag.not_found").ToProblem();
        }

        if (to == name)
        {
            return Results.Ok(ToTag(name, tag, used));
        }

        // Renaming onto an existing tag merges the two: contacts carrying either keep one, and the existing definition's look wins.
        var target = await db.ContactTags.FirstOrDefaultAsync(t => t.Name == to, ct);
        await InTransactionAsync(db, async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE conversations.contacts
                SET tags = CASE WHEN {to} = ANY(tags) THEN array_remove(tags, {name}) ELSE array_replace(tags, {name}, {to}) END, updated_at = now()
                WHERE workspace_id = {workspaceId} AND {name} = ANY(tags)
                """, ct);
            if (tag is not null && target is not null)
            {
                db.ContactTags.Remove(tag);
                await db.SaveChangesAsync(ct);
            }
            else if (tag is not null)
            {
                tag.Name = to;
                await db.SaveChangesAsync(ct);
            }
        }, ct);

        tag = target ?? tag;

        return Results.Ok(ToTag(to, tag, (await TagCountsAsync(db, workspaceId, to, ct)).GetValueOrDefault(to)));
    }

    private static async Task<IResult> DeleteTagAsync(Guid workspaceId, string name, ConversationsDbContext db, CancellationToken ct)
    {
        var removed = 0;
        var deleted = 0;
        await InTransactionAsync(db, async () =>
        {
            removed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE conversations.contacts SET tags = array_remove(tags, {name}), updated_at = now()
                WHERE workspace_id = {workspaceId} AND {name} = ANY(tags)
                """, ct);
            deleted = await db.ContactTags.Where(t => t.Name == name).ExecuteDeleteAsync(ct);
        }, ct);
        return removed + deleted == 0 ? Error.NotFound("crm.tag.not_found").ToProblem() : Results.NoContent();
    }

    // ---- Custom fields ----

    private static FieldDefinitionResponse ToField(ContactFieldDefinition f) => new(f.Id, f.Key, f.Label, f.Type, f.Options, f.SortOrder);

    private static async Task<IResult> ListFieldsAsync(Guid workspaceId, ConversationsDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ContactFields.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(ct)).Select(ToField).ToList());

    private static Dictionary<string, string[]> ValidateField(FieldDefinitionRequest request, bool creating)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (creating && (request.Key is null || !SegmentSql.FieldKey().IsMatch(request.Key)))
        {
            errors["key"] = ["validation.field.key"];
        }

        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > 60)
        {
            errors["label"] = ["validation.field.label"];
        }

        if (!ContactFieldTypes.All.Contains(request.Type ?? string.Empty))
        {
            errors["type"] = ["validation.field.type"];
        }
        else if (request.Type == ContactFieldTypes.Select
            && (request.Options is not { Count: > 0 and <= 50 } options || options.Any(o => string.IsNullOrWhiteSpace(o) || o.Trim().Length > 100)
                || options.Select(o => o.Trim()).Distinct(StringComparer.Ordinal).Count() != options.Count))
        {
            errors["options"] = ["validation.field.options"];
        }

        if (request.SortOrder is < 0 or > 10_000)
        {
            errors["sortOrder"] = ["validation.field.sort_order"];
        }

        return errors;
    }

    private static void Apply(ContactFieldDefinition field, FieldDefinitionRequest request)
    {
        field.Label = request.Label.Trim();
        field.Type = request.Type;
        field.Options = request.Type == ContactFieldTypes.Select ? [.. request.Options!.Select(o => o.Trim())] : [];
        field.SortOrder = request.SortOrder;
    }

    private static async Task<IResult> CreateFieldAsync(Guid workspaceId, FieldDefinitionRequest request, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        if (ValidateField(request, creating: true) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.ContactFields.CountAsync(ct) >= Contact.MaxFields)
        {
            return Error.Conflict("crm.field.limit").ToProblem();
        }

        var field = ContactFieldDefinition.Create(workspaceId, request.Key!, clock.UtcNow);
        Apply(field, request);
        db.ContactFields.Add(field);
        if (!await TrySaveAsync(db, ct))
        {
            return Error.Conflict("crm.field.key_taken").ToProblem();
        }

        return Results.Json(ToField(field), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateFieldAsync(Guid workspaceId, Guid fieldId, FieldDefinitionRequest request, ConversationsDbContext db, CancellationToken ct)
    {
        if (ValidateField(request, creating: false) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var field = await db.ContactFields.FirstOrDefaultAsync(f => f.Id == fieldId, ct);
        if (field is null)
        {
            return Error.NotFound().ToProblem();
        }

        Apply(field, request);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToField(field));
    }

    private static async Task<IResult> DeleteFieldAsync(Guid workspaceId, Guid fieldId, ConversationsDbContext db, CancellationToken ct) =>
        await db.ContactFields.Where(f => f.Id == fieldId).ExecuteDeleteAsync(ct) == 0 ? Error.NotFound().ToProblem() : Results.NoContent();

    // ---- Segments ----

    private static SegmentRule ReadRule(ContactSegment segment) =>
        JsonSerializer.Deserialize<SegmentRule>(segment.RuleJson, EnvelopeJson.Options) ?? new SegmentRule("all", []);

    private static async Task<int> CountAsync(ConversationsDbContext db, Guid workspaceId, SegmentRule rule, DateTimeOffset now, CancellationToken ct) =>
        await SegmentIds(db, workspaceId, rule, now).CountAsync(ct);

    private static async Task<IResult> ListSegmentsAsync(Guid workspaceId, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        var segments = await db.Segments.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var result = new List<SegmentResponse>(segments.Count);
        foreach (var s in segments)
        {
            var rule = ReadRule(s);
            var count = SegmentSql.Validate(rule).Count == 0 ? await CountAsync(db, workspaceId, rule, clock.UtcNow, ct) : 0;
            result.Add(new SegmentResponse(s.Id, s.Name, rule, count, s.UpdatedAt));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> PreviewSegmentAsync(Guid workspaceId, SegmentRule rule, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        if (SegmentSql.Validate(rule) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        return Results.Ok(new SegmentPreviewResponse(await CountAsync(db, workspaceId, rule, clock.UtcNow, ct)));
    }

    private static Dictionary<string, string[]> ValidateSegment(SegmentRequest request)
    {
        var errors = SegmentSql.Validate(request.Rule);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        {
            errors["name"] = ["validation.segment.name"];
        }

        return errors;
    }

    private static async Task<IResult> CreateSegmentAsync(Guid workspaceId, SegmentRequest request, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (ValidateSegment(request) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var segment = ContactSegment.Create(workspaceId, request.Name.Trim(), JsonSerializer.Serialize(request.Rule, EnvelopeJson.Options), caller.UserId, clock.UtcNow);
        db.Segments.Add(segment);
        if (!await TrySaveAsync(db, ct))
        {
            return Error.Conflict("crm.segment.name_taken").ToProblem();
        }

        var response = new SegmentResponse(segment.Id, segment.Name, request.Rule, await CountAsync(db, workspaceId, request.Rule, clock.UtcNow, ct), segment.UpdatedAt);
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateSegmentAsync(Guid workspaceId, Guid segmentId, SegmentRequest request, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        if (ValidateSegment(request) is { Count: > 0 } errors)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var segment = await db.Segments.FirstOrDefaultAsync(s => s.Id == segmentId, ct);
        if (segment is null)
        {
            return Error.NotFound().ToProblem();
        }

        segment.Name = request.Name.Trim();
        segment.RuleJson = JsonSerializer.Serialize(request.Rule, EnvelopeJson.Options);
        if (!await TrySaveAsync(db, ct))
        {
            return Error.Conflict("crm.segment.name_taken").ToProblem();
        }

        return Results.Ok(new SegmentResponse(segment.Id, segment.Name, request.Rule, await CountAsync(db, workspaceId, request.Rule, clock.UtcNow, ct), segment.UpdatedAt));
    }

    private static async Task<IResult> DeleteSegmentAsync(Guid workspaceId, Guid segmentId, ConversationsDbContext db, CancellationToken ct) =>
        await db.Segments.Where(s => s.Id == segmentId).ExecuteDeleteAsync(ct) == 0 ? Error.NotFound().ToProblem() : Results.NoContent();

    private static Task InTransactionAsync(ConversationsDbContext db, Func<Task> work, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });

    private static async Task<bool> TrySaveAsync(ConversationsDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation(code, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] }).ToProblem();
}
