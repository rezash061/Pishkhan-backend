using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Channels.Services;

/// <summary>Marks a Meta data deletion request completed once every module holding customer data has confirmed.</summary>
internal sealed class DataDeletionCompletedHandler(ChannelsDbContext db, IClock clock)
    : IdempotentHandler<DataDeletionCompleted, ChannelsDbContext>(db)
{
    protected override async Task HandleOnceAsync(DataDeletionCompleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = await Db.DataDeletionRequests.FirstOrDefaultAsync(r => r.Id == message.RequestId, cancellationToken);
        request?.ModuleCompleted(message.Module, message.ItemsDeleted, clock.UtcNow);
    }
}
