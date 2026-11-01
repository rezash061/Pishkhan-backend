using Pishkhan.Modules.Content.Services;

namespace Pishkhan.UnitTests;

public sealed class SiteSignatureTests
{
    [Fact]
    public void Matches_the_websites_signPayload()
    {
        // Computed with the website's lib/hmac.ts signPayload("s3cret", "1700000000", body).
        SiteSignature.Sign("s3cret", 1_700_000_000, """{"tags":["plans"],"paths":[]}""")
            .ShouldBe("sha256=b105fcdd451f1705f99a91e1004642e9932887cf4553ccbcacb022a233886776");
    }
}
