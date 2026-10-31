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
}
