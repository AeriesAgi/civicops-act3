using System.Security.Cryptography;
using System.Text;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class ZendeskWebhookTests
{
    private const string FakeSecret = "test-webhook-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"eventId\":\"fake-event\",\"comment\":\"Afrikaans: ontvang\"}");

    [Fact]
    public void MatchesZendeskTimestampThenRawBodyHmacScheme()
    {
        var clock = new ZendeskClock(); var timestamp = clock.Now.ToString("O");
        var signature = Sign(timestamp, Body);
        Assert.True(ZendeskWebhookSignature.Verify(signature, timestamp, Body, FakeSecret, clock));
    }

    [Fact]
    public void RawUnicodeBodyIsSignedWithoutReserializing()
    {
        var clock = new ZendeskClock(); var timestamp = clock.Now.ToString("O"); var body = Encoding.UTF8.GetBytes("{ \"comment\": \"Môre, ontvang\" }");
        Assert.True(ZendeskWebhookSignature.Verify(Sign(timestamp, body), timestamp, body, FakeSecret, clock));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("secret")]
    [InlineData("signature")]
    [InlineData("short-signature")]
    [InlineData("invalid-base64")]
    [InlineData("timestamp")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing-secret")]
    [InlineData("missing-signature")]
    [InlineData("missing-timestamp")]
    public void RejectsInvalidOrStaleSignature(string variant)
    {
        var clock = new ZendeskClock(); string? timestamp = clock.Now.ToString("O");
        string? signature = Sign(timestamp, Body); string? secret = FakeSecret; var body = Body;
        switch (variant)
        {
            case "body": body = Encoding.UTF8.GetBytes("{}"); break;
            case "secret": secret = "different-fake-secret"; break;
            case "signature": signature = Convert.ToBase64String(new byte[32]); break;
            case "short-signature": signature = Convert.ToBase64String(new byte[4]); break;
            case "invalid-base64": signature = "not base64"; break;
            case "timestamp": timestamp = "bad"; break;
            case "stale": timestamp = clock.Now.AddMinutes(-6).ToString("O"); signature = Sign(timestamp, body); break;
            case "future": timestamp = clock.Now.AddMinutes(6).ToString("O"); signature = Sign(timestamp, body); break;
            case "missing-secret": secret = null; break;
            case "missing-signature": signature = null; break;
            case "missing-timestamp": timestamp = null; break;
        }
        Assert.False(ZendeskWebhookSignature.Verify(signature, timestamp, body, secret, clock));
    }

    private static string Sign(string timestamp, byte[] body)
    {
        var data = Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray();
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(FakeSecret), data));
    }
}
