using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

/// <summary>Release guards for the resident/staff SOS contract. All fixtures are synthetic.</summary>
public sealed class SosEndpointTests
{
    private const string Notice = "CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone.";

    [Fact]
    public async Task ResidentTriggerWithNoPointRequestsLandmarkAndLocationCompletesOnlyActiveAlert()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var resident = await RbacTests.RoleClient(app, "resident");
        var created = await Post(resident, "/api/v1/sos", Payload("synthetic-awaiting-location", location: null, address: null, includeDefaultLocation: false));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.True(created.Body.GetProperty("locationRequired").GetBoolean());
        var alert = created.Body.GetProperty("alert");
        Assert.Equal("AwaitingLocation", alert.GetProperty("status").GetString());
        Assert.Equal(Notice, created.Body.GetProperty("emergencyNotice").GetString());

        var id = alert.GetProperty("id").GetString()!;
        var location = await Post(resident, "/api/v1/sos/" + id + "/location", new
        {
            operationId = "synthetic-location-operation-01",
            location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 9,
            addressOrLandmark = "Synthetic training gate"
        });
        Assert.Equal(HttpStatusCode.OK, location.Status);
        Assert.Equal("Triggered", location.Body.GetProperty("alert").GetProperty("status").GetString());
        Assert.Equal(-29.915, location.Body.GetProperty("alert").GetProperty("location").GetProperty("latitude").GetDouble());

        var staff = await RbacTests.RoleClient(app, "supervisor");
        var falseAlarm = await Post(staff, "/api/v1/sos/" + id + "/false-alarm", Action("synthetic-false-alarm-01", "Synthetic operator confirmed a false alarm."));
        Assert.Equal(HttpStatusCode.OK, falseAlarm.Status);
        Assert.Equal("FalseAlarm", falseAlarm.Body.GetProperty("status").GetString());
        var late = await Post(resident, "/api/v1/sos/" + id + "/location", new
        {
            operationId = "synthetic-location-operation-late",
            location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 9
        });
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
    }

    [Fact]
    public async Task FullHumanLifecycleAuditsEveryTransitionAndQueueRedactsContact()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var resident = await RbacTests.RoleClient(app, "resident");
        var created = await Post(resident, "/api/v1/sos", Payload("synthetic-lifecycle-01"));
        created.EnsureSuccess();
        var id = created.Body.GetProperty("alert").GetProperty("id").GetString()!;
        var staff = await RbacTests.RoleClient(app, "supervisor");
        foreach (var (route, operation) in new[] { ("acknowledge", "ack"), ("assess", "assess"), ("dispatch", "dispatch") })
        {
            var result = await Post(staff, $"/api/v1/sos/{id}/{route}", Action("synthetic-lifecycle-" + operation,
                "Synthetic named authorised human recorded " + route + "."));
            result.EnsureSuccess();
            Assert.Equal(route switch { "acknowledge" => "Acknowledged", "assess" => "Assessing", _ => "Dispatched" }, result.Body.GetProperty("status").GetString());
        }
        var resolved = await Post(staff, $"/api/v1/sos/{id}/resolve", Action("synthetic-lifecycle-resolve", "Synthetic named authorised human resolved the exercise."));
        resolved.EnsureSuccess(); Assert.Equal("Resolved", resolved.Body.GetProperty("status").GetString());
        var audit = resolved.Body.GetProperty("audit").EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToArray();
        Assert.Contains("triggered", audit); Assert.Contains("acknowledged", audit); Assert.Contains("assessing", audit);
        Assert.Contains("dispatched", audit); Assert.Contains("resolved", audit);
        Assert.All(resolved.Body.GetProperty("audit").EnumerateArray(), item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("actor").GetString())));

        var queue = await Get(staff, "/api/v1/sos/queue"); queue.EnsureSuccess();
        Assert.DoesNotContain("+27000000901", queue.Body.ToString(), StringComparison.Ordinal);
        Assert.True(queue.Body.GetProperty("escalationTargetSeconds").GetInt32() > 0);
        var detail = await Get(staff, "/api/v1/sos/" + id); detail.EnsureSuccess();
        Assert.DoesNotContain("+27000000901", detail.Body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationReplayIsIdempotentButPayloadConflictCannotRewriteAlert()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = await RbacTests.RoleClient(app, "resident");
        var first = await Post(client, "/api/v1/sos", Payload("synthetic-idempotent-01")); first.EnsureSuccess();
        var replay = await Post(client, "/api/v1/sos", Payload("synthetic-idempotent-01")); replay.EnsureSuccess();
        Assert.True(replay.Body.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.Body.GetProperty("alert").GetProperty("id").GetString(), replay.Body.GetProperty("alert").GetProperty("id").GetString());
        var conflict = await Post(client, "/api/v1/sos", Payload("synthetic-idempotent-01", channel: "crew", contact: "+27000000902"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
    }

    [Fact]
    public async Task ClosedSosRequestsRejectUnknownBiometricAndRiskFields()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var json = JsonNode.Parse(JsonSerializer.Serialize(Payload("synthetic-closed-contract-01")))!.AsObject();
        json["faceEmbedding"] = "synthetic-prohibited";
        var response = await client.PostAsJsonAsync("/api/v1/sos", json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        json.Remove("faceEmbedding"); json["personRiskScore"] = 99;
        response = await client.PostAsJsonAsync("/api/v1/sos", json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResidentCannotReadQueueOrPerformHumanActionsAndEverySurfaceCarriesEmergencyNotice()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var resident = await RbacTests.RoleClient(app, "resident");
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.GetAsync("/api/v1/sos/queue")).StatusCode);
        var created = await Post(resident, "/api/v1/sos", Payload("synthetic-rbac-01")); created.EnsureSuccess();
        var id = created.Body.GetProperty("alert").GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.PostAsJsonAsync("/api/v1/sos/" + id + "/acknowledge", Action("synthetic-rbac-action", "Synthetic unauthorised action."))).StatusCode);
        using var page = await resident.GetAsync("/"); page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var visibleText = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        Assert.Contains(Notice, visibleText, StringComparison.Ordinal);
        Assert.Contains("10111", visibleText, StringComparison.Ordinal); Assert.Contains("10177", visibleText, StringComparison.Ordinal); Assert.Contains("112", visibleText, StringComparison.Ordinal);
        using var crew = await resident.GetAsync("/crew/"); crew.EnsureSuccessStatusCode();
        var crewHtml = await crew.Content.ReadAsStringAsync();
        var crewText = WebUtility.HtmlDecode(Regex.Replace(crewHtml, "<[^>]+>", " "));
        Assert.Contains(Notice, crewText, StringComparison.Ordinal);
        Assert.Contains("10111", crewText, StringComparison.Ordinal); Assert.Contains("10177", crewText, StringComparison.Ordinal); Assert.Contains("112", crewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeywordAndUssdAdaptersAcceptOnlyAllowListedSyntheticMessages()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        foreach (var (path, payload) in new[]
        {
            ("/api/v1/sos/inbound", (object)new { channel = "sms", sender = "+27000000902", message = "SOS", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "whatsapp", sender = "+27000000903", message = "HELP ME", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "sms", sender = "+27000000906", message = "HULP MY", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "whatsapp", sender = "+27000000907", message = "NGISIZE", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "sms", sender = "+27000000908", message = "SIZA", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "whatsapp", sender = "+27000000909", message = "NGICELA USIZO", tenantId = "default" }),
            ("/api/v1/sos/inbound", (object)new { channel = "sms", sender = "+27000000910", message = "PLEASE SOS NOW", tenantId = "default" }),
            ("/api/v1/sos/ussd", (object)new { sessionId = "synthetic-ussd-01", text = "*120*SOS#", sender = "+27000000904", tenantId = "default" })
        })
        {
            var accepted = await Post(client, path, payload); accepted.EnsureSuccess();
            Assert.True(accepted.Body.TryGetProperty("alert", out _));
        }
        var rejected = await Post(client, "/api/v1/sos/inbound", new { channel = "sms", sender = "+27000000905", message = "PREDICT CRIME", tenantId = "default" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
    }

    [Fact]
    public async Task HumanActionReplayIsIdempotentAndConflictingOperationIsRejected()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var resident = await RbacTests.RoleClient(app, "resident");
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var created = await Post(resident, "/api/v1/sos", Payload("synthetic-action-idempotency-01")); created.EnsureSuccess();
        var id = created.Body.GetProperty("alert").GetProperty("id").GetString()!;
        var action = Action("synthetic-action-op-01", "Synthetic supervisor acknowledged this exercise.");
        var first = await Post(supervisor, "/api/v1/sos/" + id + "/acknowledge", action); first.EnsureSuccess();
        var replay = await Post(supervisor, "/api/v1/sos/" + id + "/acknowledge", action); replay.EnsureSuccess();
        Assert.Equal(first.Body.GetProperty("status").GetString(), replay.Body.GetProperty("status").GetString());
        var conflict = await Post(supervisor, "/api/v1/sos/" + id + "/acknowledge", Action("synthetic-action-op-01", "A different synthetic reason."));
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
    }

    [Fact]
    public async Task ResidentLocationCapabilityIsBoundToTheTriggeringSession()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var firstResident = await RbacTests.RoleClient(app, "resident");
        using var secondResident = await RbacTests.RoleClient(app, "resident");
        var created = await Post(secondResident, "/api/v1/sos", Payload("synthetic-location-owner-01", location: null, address: null, includeDefaultLocation: false));
        created.EnsureSuccess(); var id = created.Body.GetProperty("alert").GetProperty("id").GetString()!;
        var payload = new { operationId = "synthetic-location-owner-write", location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 9, addressOrLandmark = "Synthetic training gate" };
        Assert.Equal(HttpStatusCode.Forbidden, (await firstResident.PostAsJsonAsync("/api/v1/sos/" + id + "/location", payload)).StatusCode);
        var accepted = await Post(secondResident, "/api/v1/sos/" + id + "/location", payload); accepted.EnsureSuccess();
        Assert.Equal("Triggered", accepted.Body.GetProperty("alert").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RepeatedFalseAlarmsAreFlaggedForHumanReviewWithoutScoringOrPenalties()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var resident = await RbacTests.RoleClient(app, "resident");
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var first = await Post(resident, "/api/v1/sos", Payload("synthetic-false-repeat-01", contact: "+27000000941")); first.EnsureSuccess();
        var second = await Post(resident, "/api/v1/sos", Payload("synthetic-false-repeat-02", contact: "+27000000941")); second.EnsureSuccess();
        var other = await Post(resident, "/api/v1/sos", Payload("synthetic-false-repeat-03", contact: "+27000000942")); other.EnsureSuccess();
        var firstAlarm = await Post(supervisor, "/api/v1/sos/" + first.Body.GetProperty("alert").GetProperty("id").GetString() + "/false-alarm", Action("synthetic-false-repeat-act-01", "Synthetic operator confirmed the first false alarm."));
        firstAlarm.EnsureSuccess();
        Assert.False(firstAlarm.Body.GetProperty("repeatedFalseAlarmReview").GetBoolean());
        Assert.Contains("user-notified-simulated", Actions(firstAlarm.Body));
        Assert.DoesNotContain("false-alarm-human-review", Actions(firstAlarm.Body));
        Assert.Contains("No SMS, WhatsApp, push, call or penalty was sent.", firstAlarm.Body.ToString(), StringComparison.Ordinal);
        var secondAlarm = await Post(supervisor, "/api/v1/sos/" + second.Body.GetProperty("alert").GetProperty("id").GetString() + "/false-alarm", Action("synthetic-false-repeat-act-02", "Synthetic operator confirmed another false alarm from the same requester."));
        secondAlarm.EnsureSuccess();
        Assert.True(secondAlarm.Body.GetProperty("repeatedFalseAlarmReview").GetBoolean());
        Assert.Contains("false-alarm-human-review", Actions(secondAlarm.Body));
        Assert.Contains("No score, penalty, restriction or person indicator was applied.", secondAlarm.Body.ToString(), StringComparison.Ordinal);
        var unrelated = await Post(supervisor, "/api/v1/sos/" + other.Body.GetProperty("alert").GetProperty("id").GetString() + "/false-alarm", Action("synthetic-false-repeat-act-03", "Synthetic operator confirmed a different requester."));
        unrelated.EnsureSuccess();
        Assert.False(unrelated.Body.GetProperty("repeatedFalseAlarmReview").GetBoolean());
    }

    [Fact]
    public async Task EscalationLocationAndCancelWindowsFollowTheConfiguredClock()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_SOS_ACK_TARGET_SECONDS"] = "60";
        app.Settings["CIVICOPS_SOS_LOCATION_WINDOW_MINUTES"] = "30";
        app.Settings["CIVICOPS_SOS_CANCEL_WINDOW_SECONDS"] = "10";
        var start = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var clock = new SosClock(start);
        var events = new CaseEventStream();
        var reader = events.Subscribe().Reader;
        using var store = new SosStore(app.Services.GetRequiredService<IConfiguration>(), app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(),
            app.Services.GetRequiredService<DurableStorage>(), clock, events);
        var tracked = await store.CreateAsync(Request("synthetic-window-tracked", new GeoPoint(30.985, -29.915)), null, null, default);
        var waiting = await store.CreateAsync(Request("synthetic-window-cancel", null, null), null, null, default);
        clock.Now = start.AddSeconds(11);
        var expiredCancel = await Assert.ThrowsAsync<SosRequestException>(() => store.CancelAsync(waiting.Alert.Id, new("synthetic-window-cancel-late", "Synthetic late cancel"), "Synthetic resident", default));
        Assert.Equal(409, expiredCancel.Status);
        clock.Now = start.AddSeconds(61);
        var supervisor = await store.QueueAsync("Synthetic operator", default);
        Assert.Equal("supervisor", Assert.Single(supervisor.Alerts, alert => alert.Id == tracked.Alert.Id).EscalationLevel);
        Assert.Contains(supervisor.Alerts.Single(alert => alert.Id == tracked.Alert.Id).Audit, entry => entry.Action == "escalated-supervisor"
            && entry.Note!.Contains("SIMULATED SMS/push to the supervisor.", StringComparison.Ordinal)
            && entry.Note.Contains("No message was sent and no person was scored.", StringComparison.Ordinal));
        var sawQueue = false; while (reader.TryRead(out var published)) sawQueue |= published.Type == "sos.queue"; Assert.True(sawQueue);
        clock.Now = start.AddSeconds(121);
        var manager = await store.QueueAsync("Synthetic operator", default);
        Assert.Equal("duty-manager", manager.Alerts.Single(alert => alert.Id == tracked.Alert.Id).EscalationLevel);
        var acknowledged = await store.ActAsync(tracked.Alert.Id, SosStatus.Acknowledged, new("synthetic-window-ack", "Synthetic operator acknowledged the exercise."), "Synthetic operator", default);
        Assert.Equal(SosStatus.Acknowledged, acknowledged!.Status); Assert.Null(acknowledged.EscalationLevel);
        var skipped = await Assert.ThrowsAsync<SosRequestException>(() => store.ActAsync(tracked.Alert.Id, SosStatus.Resolved, new("synthetic-window-skip", "Synthetic invalid jump."), "Synthetic operator", default));
        Assert.Equal(409, skipped.Status);
        clock.Now = start.AddMinutes(31);
        var latePoint = await Assert.ThrowsAsync<SosRequestException>(() => store.AddLocationAsync(tracked.Alert.Id, new("synthetic-window-late-point", new GeoPoint(30.99, -29.92), 10, null), "Synthetic resident", default));
        Assert.Equal(409, latePoint.Status); Assert.Contains("no coordinates were retained", latePoint.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((await store.QueueAsync("Synthetic operator", default)).Alerts, alert => alert.Id == tracked.Alert.Id);
        var purged = await store.GetAsync(tracked.Alert.Id, "Synthetic operator", default);
        Assert.Null(purged!.Location); Assert.Contains(purged.Audit, entry => entry.Action == "location-purged");
    }

    [Fact]
    public async Task EverySosTransitionIsExplicitAndTerminalStatesDoNotReopen()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var store = new SosStore(app.Services.GetRequiredService<IConfiguration>(), app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(),
            app.Services.GetRequiredService<DurableStorage>(), TimeProvider.System, new CaseEventStream());
        var anonFirst = await store.CreateAsync(Request("synthetic-anon-a", null, "Synthetic gate", null), "Synthetic worker A", "water-01", default);
        var anonSecond = await store.CreateAsync(Request("synthetic-anon-b", null, "Synthetic gate", null), "Synthetic worker A", "water-01", default);
        var anonOther = await store.CreateAsync(Request("synthetic-anon-c", null, "Synthetic gate", null), "Synthetic worker B", "roads-01", default);
        Assert.False((await store.ActAsync(anonFirst.Alert.Id, SosStatus.FalseAlarm, ActionNote("synthetic-anon-a"), "Synthetic operator", default))!.RepeatedFalseAlarmReview);
        var repeatedAnon = await store.ActAsync(anonSecond.Alert.Id, SosStatus.FalseAlarm, ActionNote("synthetic-anon-b"), "Synthetic operator", default);
        Assert.True(repeatedAnon!.RepeatedFalseAlarmReview);
        Assert.Contains(repeatedAnon.Audit, entry => entry.Action == "user-notified-simulated");
        Assert.False((await store.ActAsync(anonOther.Alert.Id, SosStatus.FalseAlarm, ActionNote("synthetic-anon-c"), "Synthetic operator", default))!.RepeatedFalseAlarmReview);
        var awaiting = (await store.CreateAsync(Request("synthetic-transition-awaiting", null, null), null, null, default)).Alert;
        Assert.Equal(SosStatus.Acknowledged, (await store.ActAsync(awaiting.Id, SosStatus.Acknowledged, ActionNote("synthetic-transition-awaiting-ack"), "Synthetic operator", default))!.Status);
        foreach (var (operation, from, target) in new (string Operation, SosStatus From, SosStatus Target)[]
        {
            ("triggered-assess", SosStatus.Triggered, SosStatus.Assessing), ("triggered-dispatch", SosStatus.Triggered, SosStatus.Dispatched), ("triggered-resolve", SosStatus.Triggered, SosStatus.Resolved),
            ("acknowledged-dispatch", SosStatus.Acknowledged, SosStatus.Dispatched), ("acknowledged-resolve", SosStatus.Acknowledged, SosStatus.Resolved),
            ("assessing-resolve", SosStatus.Assessing, SosStatus.Resolved), ("dispatched-assess", SosStatus.Dispatched, SosStatus.Assessing)
        })
        {
            var alert = await Reach(store, operation, from);
            var error = await Assert.ThrowsAsync<SosRequestException>(() => store.ActAsync(alert.Id, target, ActionNote("synthetic-invalid-" + operation), "Synthetic operator", default));
            Assert.Equal(409, error.Status);
        }
        var walked = await Reach(store, "happy", SosStatus.Triggered);
        Assert.Equal(SosStatus.Acknowledged, (await store.ActAsync(walked.Id, SosStatus.Acknowledged, ActionNote("synthetic-happy-ack"), "Synthetic operator", default))!.Status);
        Assert.Equal(SosStatus.Assessing, (await store.ActAsync(walked.Id, SosStatus.Assessing, ActionNote("synthetic-happy-assess"), "Synthetic operator", default))!.Status);
        Assert.Equal(SosStatus.Dispatched, (await store.ActAsync(walked.Id, SosStatus.Dispatched, ActionNote("synthetic-happy-dispatch"), "Synthetic operator", default))!.Status);
        Assert.Equal(SosStatus.Resolved, (await store.ActAsync(walked.Id, SosStatus.Resolved, ActionNote("synthetic-happy-resolve"), "Synthetic operator", default))!.Status);
        var reopened = await Assert.ThrowsAsync<SosRequestException>(() => store.ActAsync(walked.Id, SosStatus.Acknowledged, ActionNote("synthetic-happy-reopen"), "Synthetic operator", default));
        Assert.Equal(409, reopened.Status);
        var falseAlarm = await Reach(store, "false", SosStatus.Triggered);
        Assert.Equal(SosStatus.FalseAlarm, (await store.ActAsync(falseAlarm.Id, SosStatus.FalseAlarm, ActionNote("synthetic-happy-false"), "Synthetic operator", default))!.Status);
        var cancelled = (await store.CreateAsync(Request("synthetic-transition-cancel", null, "Synthetic gate"), null, null, default)).Alert;
        Assert.Equal(SosStatus.Cancelled, (await store.CancelAsync(cancelled.Id, ActionNote("synthetic-happy-cancel"), "Synthetic resident", default))!.Status);
        var cancelClosed = await Assert.ThrowsAsync<SosRequestException>(() => store.CancelAsync(walked.Id, ActionNote("synthetic-cancel-closed"), "Synthetic resident", default));
        Assert.Equal(409, cancelClosed.Status);
    }

    private static SosCreateRequest Request(string operationId, GeoPoint? location, string? address = "Synthetic training gate", string? contact = "+27000000931") =>
        new(operationId, "resident-web", "Synthetic safety exercise", contact, address, false, location, location is null ? null : 8, location is null ? null : 70);
    private static SosActionRequest ActionNote(string operationId) => new(operationId, "Synthetic named operator recorded this exercise.");
    private static async Task<SosAlert> Reach(SosStore store, string name, SosStatus status)
    {
        var alert = (await store.CreateAsync(Request("synthetic-reach-" + name, null, "Synthetic gate"), null, null, default)).Alert;
        if (status == SosStatus.Triggered) return alert;
        alert = (await store.ActAsync(alert.Id, SosStatus.Acknowledged, ActionNote("synthetic-reach-" + name + "-ack"), "Synthetic operator", default))!;
        if (status == SosStatus.Acknowledged) return alert;
        alert = (await store.ActAsync(alert.Id, SosStatus.Assessing, ActionNote("synthetic-reach-" + name + "-assess"), "Synthetic operator", default))!;
        if (status == SosStatus.Assessing) return alert;
        return (await store.ActAsync(alert.Id, SosStatus.Dispatched, ActionNote("synthetic-reach-" + name + "-dispatch"), "Synthetic operator", default))!;
    }
    private static IEnumerable<string?> Actions(JsonElement alert) => alert.GetProperty("audit").EnumerateArray().Select(item => item.GetProperty("action").GetString());
    private sealed class SosClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private static object Payload(string operationId, GeoPoint? location = null, string? address = "Synthetic training gate",
        string channel = "resident-web", string contact = "+27000000901", bool includeDefaultLocation = true) => new
    {
        operationId, channel, note = "SYNTHETIC: safety exercise only.", contact, addressOrLandmark = address,
        silent = false, location = includeDefaultLocation && location is null ? new GeoPoint(30.985, -29.915) : location, accuracyMeters = 8, batteryPercent = 77
    };
    private static object Action(string operationId, string note) => new { operationId, note };

    private static async Task<Response> Post(HttpClient client, string path, object payload)
    {
        using var response = await client.PostAsJsonAsync(path, payload);
        return new(response.StatusCode, await Read(response));
    }
    private static async Task<Response> Get(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return new(response.StatusCode, await Read(response));
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(text).RootElement.Clone();
    }
    private sealed record Response(HttpStatusCode Status, JsonElement Body)
    {
        public void EnsureSuccess() => Assert.True((int)Status is >= 200 and < 300, $"SOS response {(int)Status}: {Body}");
    }
}
