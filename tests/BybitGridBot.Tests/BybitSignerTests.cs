using BybitGridBot.Bybit;

namespace BybitGridBot.Tests;

public sealed class BybitSignerTests
{
    [Fact]
    public void Sign_ReturnsExpectedHmacHex()
    {
        var signer = new BybitSigner();
        var payload = "1658384314791XXXXXXXXXX5000category=option&symbol=BTC-29JUL22-25000-C";

        var signature = signer.Sign(payload, "secret");

        Assert.Equal("02e9182e346177050f199ce1e0703d738589e3763805ed71590ced65539a73a7", signature);
    }

    [Fact]
    public void Sign_IsDeterministic()
    {
        var signer = new BybitSigner();
        var payload = "1658384314791XXXXXXXXXX5000category=option&symbol=BTC-29JUL22-25000-C";

        var signature1 = signer.Sign(payload, "secret");
        var signature2 = signer.Sign(payload, "secret");

        Assert.Equal(signature1, signature2);
    }
}
