using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Content.Contracts;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Modules.Content.Features;
using Pishkhan.Modules.Content.Persistence;
using Pishkhan.SharedKernel;
using Quartz;

namespace Pishkhan.Modules.Content.Services;

/// <summary>
/// Publishing and taking down entries, shared by the editor endpoints and the scheduled job. Each change drops the Content cache tag
/// on every host and asks the website to revalidate the entry's paths, in the same transaction as the change (outbox).
/// </summary>
internal sealed class ContentPublisher(ContentDbContext db, ICacheInvalidator cache, IClock clock)
{
    /// <summary>A case study needs the seller's consent recorded (<c>data.consent: true</c>) before it is published.</summary>
    public static string? CannotPublish(ContentEntry entry)
    {
        if (entry.Kind == ContentKinds.CaseStudy)
        {
            using var data = System.Text.Json.JsonDocument.Parse(entry.Data);
            if (!data.RootElement.TryGetProperty("consent", out var consent) || consent.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                return "content.consent_required";
            }
        }

        return ContentKinds.CategoryKind(entry.Kind) is not null && entry.Category is null ? "content.category_required" : null;
    }

    public void Publish(ContentEntry entry, AuditActor actor, Guid staffId)
    {
        var before = entry.Published;
        var wasLive = entry.Status == ContentStatuses.Published;
        entry.Publish(ContentRules.SearchText(entry.Draft), staffId, clock.UtcNow);
        Changed(entry, before, actor, "content.publish", new { revision = entry.Revision, replacedLive = wasLive });
    }

    public void Archive(ContentEntry entry, AuditActor actor, Guid staffId, string? reason)
    {
        var wasLive = entry.Status == ContentStatuses.Published;
        entry.Archive(staffId);
        if (wasLive)
        {
            Changed(entry, entry.Published, actor, "content.archive", null, reason);
        }
        else
        {
            Audit(entry, actor, "content.archive", null, reason);
        }
    }

    /// <summary>Drop caches and revalidate the site for a change that is not to one entry (categories, redirects).</summary>
    public void SiteChanged(IReadOnlyList<string> tags, IReadOnlyList<string> paths)
    {
        cache.Enqueue(db, CacheTags.Content);
        db.Publish(new SiteRevalidationRequested(tags, paths), tags.FirstOrDefault() ?? "content");
    }

    public void Audit(ContentEntry entry, AuditActor actor, string action, object? extra, string? reason = null)
    {
        var record = AuditEntries.Create(
            actor, action, "content_entry", entry.Id.ToString(), null, clock.UtcNow,
            after: new { entry.Kind, entry.Slug, entry.Status, entry.Title, entry.Revision, extra },
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        db.Publish(record, record.PartitionKey(), workspaceId: null);
    }

    private void Changed(ContentEntry entry, EntryContent? before, AuditActor actor, string action, object? extra, string? reason = null)
    {
        // The old category's pages list it too (a category change moves it).
        var paths = ContentRules.Paths(entry.Kind, entry.Slug, entry.Published?.Category ?? entry.Category).ToList();
        if (before?.Category is { } oldCategory && oldCategory != entry.Published?.Category)
        {
            paths.AddRange(ContentRules.Paths(entry.Kind, entry.Slug, oldCategory));
        }

        paths.Add("/sitemap.xml");
        SiteChanged(ContentRules.Tags(entry.Kind), [.. paths.Distinct(StringComparer.Ordinal)]);
        Audit(entry, actor, action, extra, reason);
    }
}

/// <summary>Platform group, every minute: publishes the drafts whose scheduled time has come.</summary>
[DisallowConcurrentExecution]
internal sealed class ScheduledPublishJob(ContentDbContext db, ContentPublisher publisher, IClock clock, ILogger<ScheduledPublishJob> logger) : IJob
{
    public const int Batch = 50;

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.Entries.Where(e => e.ScheduledAt != null && e.ScheduledAt <= now).OrderBy(e => e.ScheduledAt).Take(Batch).ToListAsync(ct);
        var published = 0;
        foreach (var entry in due)
        {
            if (ContentPublisher.CannotPublish(entry) is { } problem)
            {
                logger.LogWarning("Scheduled content {EntryId} was not published: {Problem}", entry.Id, problem);
                entry.Schedule(null, entry.UpdatedBy);
                publisher.Audit(entry, AuditActor.System, "content.schedule_failed", new { problem });
                continue;
            }

            publisher.Publish(entry, AuditActor.System, entry.UpdatedBy);
            published++;
        }

        await db.SaveChangesAsync(ct);
        return published;
    }
}
