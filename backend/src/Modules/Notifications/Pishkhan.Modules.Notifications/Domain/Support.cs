using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Notifications.Domain;

public static class TicketCategories
{
    public const string Billing = "billing";
    public const string Technical = "technical";
    public const string Instagram = "instagram";
    public const string Ai = "ai";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Billing, Technical, Instagram, Ai, Other];
}

/// <summary><c>open</c>: waiting on Pishkhan; <c>answered</c>: waiting on the seller; <c>closed</c>.</summary>
public static class TicketStatuses
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> All = [Open, Answered, Closed];
}

public static class TicketAuthors
{
    public const string Seller = "seller";
    public const string Staff = "staff";
}

/// <summary>
/// A seller's support ticket (admin plan AD-2.6, user panel UP-1.18): a subject, a category and a thread of messages between the
/// workspace's members and Pishkhan's support staff.
/// </summary>
public sealed class SupportTicket : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxSubject = 150;
    public const int MaxBody = 4_000;
    public const int MaxOpenPerWorkspace = 10;

    private SupportTicket()
    {
    }

    public Guid WorkspaceId { get; private set; }

    /// <summary>Short, human number shown on both sides ("تیکت ۱۲۴۵").</summary>
    public long Number { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string Category { get; private set; } = TicketCategories.Other;

    public string Status { get; private set; } = TicketStatuses.Open;

    /// <summary>The member who opened it; staff replies notify them.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>The staff member handling it, or null.</summary>
    public Guid? AssignedTo { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    public string LastMessageBy { get; private set; } = TicketAuthors.Seller;

    public int MessageCount { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public static IReadOnlyDictionary<string, string[]> Validate(string? subject, string? category, string? body)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(subject) || subject.Trim().Length < 3 || subject.Trim().Length > MaxSubject)
        {
            errors["subject"] = ["validation.length"];
        }

        if (category is null || !TicketCategories.All.Contains(category))
        {
            errors["category"] = ["validation.invalid"];
        }

        if (ValidateBody(body) is { } bodyError)
        {
            errors["body"] = [bodyError];
        }

        return errors;
    }

    public static string? ValidateBody(string? body) =>
        string.IsNullOrWhiteSpace(body) || body.Trim().Length > MaxBody ? "validation.length" : null;

    public static (SupportTicket Ticket, TicketMessage First) Open(
        Guid workspaceId, long number, string subject, string category, string body, Guid userId, string? authorName, DateTimeOffset now)
    {
        var ticket = new SupportTicket
        {
            Id = Uuid.NewV7(now),
            WorkspaceId = workspaceId,
            Number = number,
            Subject = subject.Trim(),
            Category = category,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (ticket, ticket.Add(TicketAuthors.Seller, userId, authorName, body, now));
    }

    /// <summary>A message from either side. A seller's message (re)opens the ticket; a staff message marks it answered.</summary>
    public TicketMessage Add(string authorKind, Guid authorId, string? authorName, string body, DateTimeOffset now)
    {
        Status = authorKind == TicketAuthors.Staff ? TicketStatuses.Answered : TicketStatuses.Open;
        ClosedAt = null;
        LastMessageAt = now;
        LastMessageBy = authorKind;
        MessageCount++;
        UpdatedAt = now;
        return TicketMessage.Create(this, authorKind, authorId, authorName, body.Trim(), now);
    }

    public void Close(DateTimeOffset now)
    {
        if (Status == TicketStatuses.Closed)
        {
            return;
        }

        Status = TicketStatuses.Closed;
        ClosedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Staff reopening a closed ticket: it waits on Pishkhan again.</summary>
    public void Reopen(DateTimeOffset now)
    {
        if (Status != TicketStatuses.Closed)
        {
            return;
        }

        Status = TicketStatuses.Open;
        ClosedAt = null;
        UpdatedAt = now;
    }

    public void Assign(Guid? staffId, DateTimeOffset now)
    {
        AssignedTo = staffId;
        UpdatedAt = now;
    }
}

public sealed class TicketMessage : Entity<Guid>, ITenantOwned
{
    private TicketMessage()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid TicketId { get; private set; }

    public string AuthorKind { get; private set; } = TicketAuthors.Seller;

    public Guid AuthorId { get; private set; }

    /// <summary>The member's name or the staff username when it was written.</summary>
    public string? AuthorName { get; private set; }

    public string Body { get; private set; } = string.Empty;

    internal static TicketMessage Create(SupportTicket ticket, string authorKind, Guid authorId, string? authorName, string body, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = ticket.WorkspaceId,
        TicketId = ticket.Id,
        AuthorKind = authorKind,
        AuthorId = authorId,
        AuthorName = authorName is { Length: > 100 } ? authorName[..100] : authorName,
        Body = body,
        CreatedAt = now,
        UpdatedAt = now,
    };
}

public static class AnnouncementSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Info, Warning, Critical];
}

/// <summary>A notice every seller sees as a banner in the panel between <see cref="StartsAt"/> and <see cref="EndsAt"/> (AD-2.6).</summary>
public sealed class Announcement : AggregateRoot<Guid>
{
    public const int MaxTitle = 120;
    public const int MaxBody = 1_000;
    public const int MaxLink = 300;

    private Announcement()
    {
    }

    public string Title { get; private set; } = string.Empty;

    public string? Body { get; private set; }

    public string Severity { get; private set; } = AnnouncementSeverities.Info;

    /// <summary>A panel path (<c>/billing</c>) or an https URL.</summary>
    public string? Link { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public static IReadOnlyDictionary<string, string[]> Validate(AnnouncementTerms terms)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(terms.Title) || terms.Title.Trim().Length > MaxTitle)
        {
            errors["title"] = ["validation.length"];
        }

        if (terms.Body is { } body && body.Trim().Length > MaxBody)
        {
            errors["body"] = ["validation.length"];
        }

        if (!AnnouncementSeverities.All.Contains(terms.Severity))
        {
            errors["severity"] = ["validation.invalid"];
        }

        if (terms.Link is { Length: > 0 } link
            && (link.Length > MaxLink || !(link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)
                || Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)))
        {
            errors["link"] = ["validation.invalid"];
        }

        if (terms.EndsAt is { } ends && ends <= terms.StartsAt)
        {
            errors["endsAt"] = ["validation.out_of_range"];
        }

        return errors;
    }

    public static Announcement Create(AnnouncementTerms terms, Guid staffId, DateTimeOffset now)
    {
        var announcement = new Announcement { Id = Uuid.NewV7(now), CreatedBy = staffId, CreatedAt = now };
        announcement.Update(terms, now);
        return announcement;
    }

    public void Update(AnnouncementTerms terms, DateTimeOffset now)
    {
        Title = terms.Title.Trim();
        Body = string.IsNullOrWhiteSpace(terms.Body) ? null : terms.Body.Trim();
        Severity = terms.Severity;
        Link = string.IsNullOrWhiteSpace(terms.Link) ? null : terms.Link.Trim();
        StartsAt = terms.StartsAt;
        EndsAt = terms.EndsAt;
        UpdatedAt = now;
    }

    /// <summary>scheduled, active or ended.</summary>
    public string StateAt(DateTimeOffset now) => now < StartsAt ? "scheduled" : EndsAt is { } end && now >= end ? "ended" : "active";
}

public sealed record AnnouncementTerms(string Title, string? Body, string Severity, string? Link, DateTimeOffset StartsAt, DateTimeOffset? EndsAt);
