using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CivicOps.Services;

public static class ElevenLabsSignature
{
    public static bool Verify(ReadOnlySpan<byte> rawBody, string? header, string? secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret)) return false;
        string? timestamp = null, signature = null;
        foreach (var item in header.Split(','))
        {
            var part = item.Trim();
            if (part.StartsWith("t=", StringComparison.Ordinal)) timestamp = part[2..];
            if (part.StartsWith("v0=", StringComparison.Ordinal)) signature = part[3..];
        }
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) || signature?.Length != 64) return false;
        // SDK rejects signatures older than 30 minutes. Also bound future signatures to prevent indefinite replay.
        if (unix < now.ToUnixTimeSeconds() - 1800 || unix > now.ToUnixTimeSeconds() + 30) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); } catch (FormatException) { return false; }
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(message, 0); rawBody.CopyTo(message.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
    public static bool EqualSecret(string? expected, string supplied) => !string.IsNullOrWhiteSpace(expected)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}
