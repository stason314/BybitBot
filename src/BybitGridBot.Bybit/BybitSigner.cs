using System.Security.Cryptography;
using System.Text;

namespace BybitGridBot.Bybit;

public sealed class BybitSigner
{
    public string Sign(string payload, string apiSecret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
