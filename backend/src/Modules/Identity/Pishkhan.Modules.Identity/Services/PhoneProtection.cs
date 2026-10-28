using Pishkhan.Infrastructure.Security;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Identity.Services;

internal sealed class PhoneProtection(ISecretProtector protector, IBlindIndexer indexer)
{
    public const string Purpose = "identity.phone";

    public string BlindIndex(PhoneNumber phone) => indexer.Compute(phone.E164, "phone");

    public string Encrypt(PhoneNumber phone) => protector.Protect(phone.E164, Purpose);

    public PhoneNumber Decrypt(string encrypted) => PhoneNumber.Parse(protector.Unprotect(encrypted, Purpose));
}
