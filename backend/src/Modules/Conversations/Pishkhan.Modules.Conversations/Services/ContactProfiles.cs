using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>
/// Outbound group (it calls Instagram from the edge): reads a new customer's profile and keeps a copy of the profile picture. The picture
/// comes from Meta's CDN (host allowlist), goes through the Files pipeline like any upload (scan, re-encode) and is served from our own
/// storage, because Meta's CDN URLs expire and are often unreachable from Iran. A failed read leaves the contact as it was.
/// </summary>
internal sealed class ContactProfileHandler(
    ConversationsDbContext db, IChannelProfileReader profiles, SafeHttpClient http, IFileService files, IClock clock, ILogger<ContactProfileHandler> logger)
    : IdempotentHandler<ContactProfileRequested, ConversationsDbContext>(db)
{
    public const long MaxPictureBytes = 2 * 1024 * 1024;

    protected override async Task HandleOnceAsync(ContactProfileRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var contact = await Db.Contacts.FirstOrDefaultAsync(c => c.Id == message.ContactId, cancellationToken);
        var now = clock.UtcNow;
        if (contact is null || !contact.ProfileIsStale(now))
        {
            return;
        }

        ChannelUserProfile? profile;
        try
        {
            profile = await profiles.GetProfileAsync(message.ChannelAccountId, message.ExternalUserId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Profile of contact {ContactId} not read: {Error}", contact.Id, ex.Message);
            return;
        }

        Guid? avatar = null;
        if (profile?.ProfilePictureUrl is { } picture)
        {
            try
            {
                var download = await http.DownloadAsync(picture, MaxPictureBytes, SafeHttpClient.MetaCdnSuffixes, cancellationToken: cancellationToken);
                using (download.Content)
                {
                    avatar = await files.IngestAsync(message.WorkspaceId, FilePurposes.Avatar, download.Content, download.ContentType, "instagram-profile", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is SafeHttpException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Profile picture of contact {ContactId} not copied: {Error}", contact.Id, ex.Message);
            }
        }

        contact.LearnProfile(profile?.Name, profile?.Username, avatar, now);
    }
}
