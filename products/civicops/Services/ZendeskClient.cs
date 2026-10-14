using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;

namespace CivicOps.Services;

// Raw HttpClient keeps request shaping visible and supports an injected fake handler.
public sealed class ZendeskClient(HttpClient http, ZendeskOptions options, TimeProvider? clock = null, DurableStorage? persistence = null)
{
    private readonly ProviderCircuit _circuit = new(clock ?? TimeProvider.System);
    public ProviderHealth Health => _circuit.Describe(options.Live);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public string Mode => options.Live ? "live" : "simulated";

    public async Task<bool> CheckConnectivityAsync(CancellationToken ct = default)
    {
        if (!options.Live) return false;
        using var response = await SendAsync(HttpMethod.Get, "/api/v2/users/me.json", null, ct);
        using var json = await ReadResponseAsync(response, ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("user", out var user)
            || user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var identifier) || identifier <= 0)
            throw new InvalidDataException("Zendesk connectivity response omitted a valid current user.");
        return true;
    }

    public static string BuildTicketRequest(Incident incident, string operation, string eventId, string? approvedText = null)
    {
        var note = operation == "approved-comment" ? approvedText ?? incident.PublicUpdate
            : $"CivicOps {incident.ReferenceNumber}\n{incident.Description}\nArea: {incident.Area}\n"
              + $"Triage: {incident.Category}; {incident.Department}; {incident.Priority}; human review: {incident.NeedsHumanReview}\n"
              + $"Agent provenance: {AgentProvenance(incident)}";
        var ticket = new JsonObject
        {
            ["external_id"] = incident.ReferenceNumber,
            ["subject"] = $"{incident.ReferenceNumber}: {incident.Category} — {incident.Area}",
            ["priority"] = MapPriority(incident.Priority), ["status"] = MapStatus(incident.Status),
            ["tags"] = new JsonArray("civicops", "civicops_origin", SafeTag(incident.Department), SafeTag(incident.Source)),
            ["comment"] = new JsonObject { ["body"] = $"{note}\n[CivicOps event {eventId}]", ["public"] = operation == "approved-comment" }
        };
        // A recovered historical communication must not revert the current downstream status or subject.
        if (operation == "approved-comment")
        {
            ticket.Remove("subject"); ticket.Remove("priority"); ticket.Remove("status"); ticket.Remove("tags");
        }
        if (operation == "create" && incident.ConsentToContact && ValidEmail(incident.ContactEmail))
            ticket["requester"] = new JsonObject { ["name"] = "CivicOps resident", ["email"] = incident.ContactEmail };
        return new JsonObject { ["ticket"] = ticket }.ToJsonString();
    }

    public async Task<ZendeskDelivery> DeliverAsync(IntegrationOutboxItem item, long? knownTicketId, CancellationToken ct)
    {
        if (!options.Live)
        {
            var directory = Path.Combine(TenantContext.DirectoryFor(options.DataDirectory), "zendesk-outbox");
            if (persistence is null) Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, item.Id + ".json");
            if (persistence is null) await AtomicJsonFile.WriteAsync(file, item.RequestJson, ct);
            else await persistence.WriteAsync(file, item.RequestJson, ct);
            return new(knownTicketId ?? SimulatedTicketId(item.CaseReference), null, true, item.Attempts > 1);
        }

        return await _circuit.ExecuteAsync(token => DeliverLiveAsync(item, knownTicketId, token), options.RequestTimeout, ct);
    }

    private async Task<ZendeskDelivery> DeliverLiveAsync(IntegrationOutboxItem item, long? knownTicketId, CancellationToken ct)
    {
        using var activity = CivicOpsTelemetry.Activities.StartActivity("provider.zendesk", System.Diagnostics.ActivityKind.Client);
        // Search before every creation attempt: external_id is durable across lost responses/restarts.
        var ticketId = knownTicketId ?? await FindByExternalIdAsync(item.CaseReference, ct);
        if (item.Operation == "create" && ticketId is not null)
            return new(ticketId.Value, TicketUrl(ticketId.Value), false, true);
        if (ticketId is not null && item.Attempts > 1 && await ContainsEventAsync(ticketId.Value, item.Id, ct))
            return new(ticketId.Value, TicketUrl(ticketId.Value), false, true);

        var method = ticketId is null ? HttpMethod.Post : HttpMethod.Put;
        var path = ticketId is null ? "/api/v2/tickets.json" : $"/api/v2/tickets/{ticketId.Value}.json";
        using var response = await SendAsync(method, path, item.RequestJson, ct);
        using var body = await ReadResponseAsync(response, ct);
        if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("ticket", out var ticket)
            || ticket.ValueKind != JsonValueKind.Object || !ticket.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var createdId) || createdId <= 0)
            throw new InvalidDataException("Zendesk response omitted a valid ticket id.");
        return new(createdId, TicketUrl(createdId), false, false);
    }

    public async Task<long?> FindByExternalIdAsync(string reference, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"type:ticket external_id:{reference}");
        string? path = $"/api/v2/search.json?query={query}";
        var matches = new HashSet<long>();
        for (var page = 0; page < 20 && path is not null; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            using var json = await ReadResponseAsync(response, ct);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Zendesk search response omitted results.");
            foreach (var ticket in results.EnumerateArray())
                if (ticket.ValueKind == JsonValueKind.Object && ticket.TryGetProperty("external_id", out var external)
                    && external.ValueKind == JsonValueKind.String && external.GetString() == reference
                    && ticket.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out var value) && value > 0) matches.Add(value);
            if (matches.Count > 1) throw new InvalidDataException("Multiple Zendesk tickets have this external_id; dispatcher reconciliation is required.");
            path = NextPage(json.RootElement);
        }
        if (path is not null) throw new InvalidDataException("Zendesk search exceeded its pagination budget.");
        return matches.Count == 0 ? null : matches.Single();
    }

    private async Task<bool> ContainsEventAsync(long ticketId, string eventId, CancellationToken ct)
    {
        string? path = $"/api/v2/tickets/{ticketId}/comments.json?sort_order=desc";
        for (var page = 0; page < 20 && path is not null; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            using var json = await ReadResponseAsync(response, ct);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Zendesk comment response omitted comments.");
            if (comments.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("body", out var b)
                && b.ValueKind == JsonValueKind.String && b.GetString()!.Contains($"[CivicOps event {eventId}]", StringComparison.Ordinal))) return true;
            path = NextPage(json.RootElement);
        }
        if (path is not null) throw new InvalidDataException("Zendesk comments exceeded their pagination budget.");
        return false;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"https://{options.Subdomain}.zendesk.com{path}");
        CivicOpsTelemetry.Propagate(request);
        request.Headers.Authorization = !string.IsNullOrWhiteSpace(options.OAuthToken)
            ? new AuthenticationHeaderValue("Bearer", options.OAuthToken)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Email}/token:{options.ApiToken}")));
        request.Headers.Accept.Add(new("application/json"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Zendesk returned HTTP {code}.", null, (System.Net.HttpStatusCode)code);
        }
        return response;
    }

    private string? NextPage(JsonElement root)
    {
        if (!root.TryGetProperty("next_page", out var next) || next.ValueKind == JsonValueKind.Null) return null;
        if (next.ValueKind != JsonValueKind.String || !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !string.Equals(uri.Host, options.Subdomain + ".zendesk.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.StartsWith("/api/v2/", StringComparison.Ordinal))
            throw new InvalidDataException("Zendesk pagination link must remain on the configured account's API.");
        return uri.PathAndQuery;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maximum = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Zendesk response exceeded its size budget.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (bounded.Length + count > maximum) throw new InvalidDataException("Zendesk response exceeded its size budget.");
            bounded.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bounded.ToArray());
    }

    public static string MapPriority(string priority) => priority.ToLowerInvariant() switch
    { "urgent" => "urgent", "high" => "high", "low" => "low", _ => "normal" };
    public static string MapStatus(IncidentStatus status) => status switch
    { IncidentStatus.New => "new", IncidentStatus.WaitingForCitizen => "pending", IncidentStatus.Resolved or IncidentStatus.Closed => "solved", _ => "open" };
    public static long SimulatedTicketId(string reference) =>
        100_000_000L + (long)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(reference)), 0) % 900_000_000);
    private string TicketUrl(long id) => $"https://{options.Subdomain}.zendesk.com/agent/tickets/{id}";
    private static string SafeTag(string value) => string.Join('_', value.ToLowerInvariant().Split([' ', '/', '-'], StringSplitOptions.RemoveEmptyEntries));
    private static bool ValidEmail(string? email) => !string.IsNullOrWhiteSpace(email) && email.Length <= 254
        && System.Net.Mail.MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
    private static string AgentProvenance(Incident incident)
    {
        var json = JsonSerializer.SerializeToElement(incident, WebJson);
        return json.TryGetProperty("agentRuns", out var runs) ? runs.GetRawText() : "No backend run recorded.";
    }
}
