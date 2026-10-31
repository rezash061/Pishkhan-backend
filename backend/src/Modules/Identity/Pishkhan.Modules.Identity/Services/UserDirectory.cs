using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Identity.Services;

internal sealed class UserDirectory(IdentityDbContext db, PhoneProtection phones) : IUserDirectory
{
    public Task<string?> GetPhoneBlindIndexAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PhoneBidx).FirstOrDefaultAsync(cancellationToken);

    public string PhoneBlindIndex(PhoneNumber phone) => phones.BlindIndex(phone);

    public async Task<UserSummary?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : new UserSummary(user.Id, user.DisplayName, phones.Decrypt(user.PhoneE164Enc).Masked);
    }

    public async Task<PhoneNumber?> GetPhoneAsync(Guid userId, CancellationToken cancellationToken)
    {
        var encrypted = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PhoneE164Enc).FirstOrDefaultAsync(cancellationToken);
        return encrypted is null ? null : phones.Decrypt(encrypted);
    }

    public async Task<Guid?> FindByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken)
    {
        var index = phones.BlindIndex(phone);
        var id = await db.Users.AsNoTracking().Where(u => u.PhoneBidx == index).Select(u => u.Id).FirstOrDefaultAsync(cancellationToken);
        return id == Guid.Empty ? null : id;
    }

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserSummary>();
        }

        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(cancellationToken);
        return users.ToDictionary(u => u.Id, u => new UserSummary(u.Id, u.DisplayName, phones.Decrypt(u.PhoneE164Enc).Masked));
    }
}
