using System.Text.Json;

namespace CivicOps.Services;

// Explicit CLI only: normal application startup never calls the connectivity endpoint.
public static class ZendeskCheck
{
    public static bool IsCommand(string[] args) => args.Length >= 1 && args[0] == "zendesk";

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration,
        TextWriter? output = null, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        output ??= Console.Out;
        if (args.Length != 2 || args[1] != "--check")
        { await output.WriteLineAsync("Usage: zendesk --check (explicit, read-only GET /api/v2/users/me.json)"); return 2; }
        var options = ZendeskOptions.FromConfiguration(configuration, "");
        if (!options.Live)
        {
            await output.WriteLineAsync("CHECK unavailable: set CIVICOPS_DEMO_MODE=false, ZENDESK_SUBDOMAIN and either ZENDESK_OAUTH_TOKEN or ZENDESK_EMAIL + ZENDESK_API_TOKEN. No request was sent.");
            return 2;
        }
        using var ownedClient = httpClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : null;
        try
        {
            await new ZendeskClient(httpClient ?? ownedClient!, options).CheckConnectivityAsync(ct);
            await output.WriteLineAsync("OK: Zendesk current-user connectivity verified with one GET. No identity, credentials or provider payload printed; no tickets changed.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            await output.WriteLineAsync(ex.StatusCode is null ? "CHECK failed: Zendesk transport unavailable." : $"CHECK failed: Zendesk HTTP {(int)ex.StatusCode}.");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or TaskCanceledException)
        { await output.WriteLineAsync("CHECK failed: invalid, oversized or timed-out Zendesk response."); return 1; }
    }
}
