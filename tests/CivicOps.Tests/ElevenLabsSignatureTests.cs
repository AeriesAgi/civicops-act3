using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class ElevenLabsSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Secret = "test-webhook-secret";
    private const string Body = "{\"type\":\"post_call_transcription\",\"data\":{\"conversation_id\":\"synthetic\"}}";
    public static string Sign(string body, string secret = Secret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? Now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return "t=" + timestamp + ",v0=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
    }
    [Fact]
    public void ValidSdkSchemeAuthenticatesExactRawBytes() => Assert.True(ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body), Sign(Body), Secret, Now));
    [Fact]
    public void ReformattingRawBodyInvalidatesSignature() => Assert.False(ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body + "\n"), Sign(Body), Secret, Now));
    [Fact]
    public void WrongSecretInvalidatesSignature() => Assert.False(ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body), Sign(Body), "test-other-secret", Now));
    [Theory]
    [InlineData(-1801, false)] [InlineData(-1800, true)] [InlineData(-1799, true)]
    [InlineData(0, true)] [InlineData(30, true)] [InlineData(31, false)]
    public void TimestampWindowIsBounded(int offsetSeconds, bool valid) => Assert.Equal(valid,
        ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body), Sign(Body, at: Now.AddSeconds(offsetSeconds)), Secret, Now));
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("t=bad,v0=ff")] [InlineData("v0=ff")]
    [InlineData("t=1791460800,v1=ff")] [InlineData("t=1791460800,v0=not-hex")]
    public void InvalidHeadersAreRejected(string? header) => Assert.False(ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body), header, Secret, Now));
    [Fact]
    public void MissingWebhookSecretFailsClosed() => Assert.False(ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(Body), Sign(Body), null, Now));
    [Theory]
    [InlineData("test-tool-secret", "test-tool-secret", true)]
    [InlineData("test-tool-secret", "test-wrong-secret", false)]
    [InlineData(null, "", false)] [InlineData(" ", " ", false)]
    public void SharedSecretCompareFailsClosed(string? expected, string supplied, bool valid) => Assert.Equal(valid, ElevenLabsSignature.EqualSecret(expected, supplied));
}
