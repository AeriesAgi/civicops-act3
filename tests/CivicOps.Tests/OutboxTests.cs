using System.Net;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class OutboxTests
{
    [Fact]
    public async Task EnqueueIsDurableAndIdempotentAfterRestart()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var first = Queue(fixture);
        var a = await first.EnqueueAsync(incident, "create", default);
        var restarted = Queue(fixture);
        var b = await restarted.EnqueueAsync(incident, "create", default);
        Assert.Equal(a.Id, b.Id); Assert.Single(await restarted.ListAsync(default));
    }

    [Fact]
    public async Task StartupReconciliationRecoversSavedCaseWithoutCreateOutboxIntent()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        Assert.Empty(await queue.ListAsync(default));
        Assert.Equal(1, await queue.ReconcileMissingCreatesAsync(default));
        Assert.Equal(0, await Queue(fixture).ReconcileMissingCreatesAsync(default));
        await queue.ProcessDueAsync(default);
        Assert.NotNull((await fixture.Store.GetAsync(incident.Id, default))!.ZendeskTicketId);
    }

    [Fact]
    public async Task ReconciliationRecoversLatestStatusWithoutLoopingOnIntegrationMetadataVersions()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "create", default); await queue.ProcessDueAsync(default);
        await fixture.Store.ChangeAsync(incident.Id, null, value =>
        {
            value.Status = IncidentStatus.Resolved;
            value.Audit.Add(new("status", "Test dispatcher", "Synthetic resolution", fixture.Clock.Now));
            return null;
        }, default);
        Assert.Equal(1, await queue.ReconcileMissingIntentsAsync(default));
        await queue.ProcessDueAsync(default);
        Assert.Equal(0, await queue.ReconcileMissingIntentsAsync(default));
        var status = Assert.Single(await queue.ListAsync(default), x => x.Operation == "status");
        using var payload = JsonDocument.Parse(status.RequestJson);
        Assert.Equal("solved", payload.RootElement.GetProperty("ticket").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReconciliationPreservesEveryApprovedDraftTextAndNeverResetsCurrentStatus()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "create", default); await queue.ProcessDueAsync(default);
        await fixture.Store.ChangeAsync(incident.Id, null, value =>
        {
            value.PublicUpdate = "A newer public update that must not replace old approved drafts.";
            value.CommsDrafts.Add(new("fake-draft-one", "Approved", "en", "First approved update.", fixture.Clock.Now)
            { State = "Approved", ApprovedBy = "Test dispatcher", ApprovedAt = fixture.Clock.Now });
            value.CommsDrafts.Add(new("fake-draft-two", "Assigned", "af", "Tweede goedgekeurde opdatering.", fixture.Clock.Now)
            { State = "Approved", ApprovedBy = "Test dispatcher", ApprovedAt = fixture.Clock.Now });
            value.CommsDrafts.Add(new("fake-pending-draft", "Resolved", "en", "Unapproved text.", fixture.Clock.Now));
            return null;
        }, default);
        Assert.Equal(2, await queue.ReconcileMissingIntentsAsync(default));
        var drafts = (await queue.ListAsync(default)).Where(x => x.Operation == "approved-comment").ToArray();
        Assert.Equal(2, drafts.Length);
        Assert.Contains("First approved update.", drafts[0].RequestJson);
        Assert.Contains("Tweede goedgekeurde opdatering.", drafts[1].RequestJson);
        foreach (var draft in drafts)
        {
            using var payload = JsonDocument.Parse(draft.RequestJson); var ticket = payload.RootElement.GetProperty("ticket");
            Assert.False(ticket.TryGetProperty("status", out _));
            Assert.True(ticket.GetProperty("comment").GetProperty("public").GetBoolean());
            Assert.DoesNotContain("newer public update", draft.RequestJson);
        }
        Assert.Equal(0, await queue.ReconcileMissingIntentsAsync(default));
    }

    [Fact]
    public async Task ApprovalRecoveryRejectsDraftWithoutRecordedActorAndTimestamp()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        var draft = new CommsDraft("fake-draft", "Triaged", "en", "Unsigned approval claim.", fixture.Clock.Now) { State = "Approved" };
        incident.CommsDrafts.Add(draft);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueApprovedDraftAsync(incident, draft, default));
    }

    [Fact]
    public async Task UpdateDedupeIncludesVersionAndOperation()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "status", default);
        await queue.EnqueueAsync(incident, "status", default);
        await queue.EnqueueAsync(incident, "approved-comment", default);
        incident.Version++;
        await queue.EnqueueAsync(incident, "status", default);
        Assert.Equal(3, (await queue.ListAsync(default)).Count);
    }

    [Fact]
    public async Task SimulatedDeliveryUpdatesCaseTicketAndVerifiedAudit()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "create", default);
        Assert.Equal(1, await queue.ProcessDueAsync(default));
        var saved = await fixture.Store.GetAsync(incident.Id, default);
        Assert.NotNull(saved?.ZendeskTicketId); Assert.Null(saved.ZendeskTicketUrl);
        Assert.Contains(saved.Audit, x => x.Action == "case-record-synced" && x.Detail.Contains("simulated"));
        Assert.True((await fixture.Store.VerifyAuditAsync(default)).Valid);
        var status = await queue.GetStatusAsync(default);
        Assert.Equal("simulated", status.Mode); Assert.Equal(1, status.Delivered); Assert.Equal(0, status.Pending);
    }

    [Fact]
    public async Task ReadsReturnClonesToProtectDurableState()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        var returned = await queue.EnqueueAsync(incident, "create", default); returned.State = IntegrationDeliveryState.Delivered;
        var listed = (await queue.ListAsync(default))[0]; listed.State = IntegrationDeliveryState.DeadLetter;
        Assert.Equal(IntegrationDeliveryState.Pending, (await queue.ListAsync(default))[0].State);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("send")]
    [InlineData("assign")]
    [InlineData("")]
    public async Task RejectsUndeclaredOperations(string operation)
    {
        using var fixture = new ZendeskFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => Queue(fixture).EnqueueAsync(ZendeskFixture.Case(), operation, default));
    }

    [Fact]
    public async Task TransientFailureBacksOffAndSurvivesRestart()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var handler = new ZendeskFakeHandler((_, _) => new(HttpStatusCode.ServiceUnavailable));
        var options = ZendeskFixture.LiveOptions(fixture.Directory);
        var queue = new IntegrationOutbox(new(new(handler), options), options, fixture.Store, fixture.Clock);
        await queue.EnqueueAsync(incident, "create", default);
        Assert.Equal(1, await queue.ProcessDueAsync(default));
        var failed = Assert.Single(await queue.ListAsync(default));
        Assert.Equal(1, failed.Attempts); Assert.Equal(IntegrationDeliveryState.Pending, failed.State);
        Assert.InRange(failed.NextAttemptAt, fixture.Clock.Now.AddSeconds(5), fixture.Clock.Now.AddSeconds(6));
        Assert.Equal("Zendesk HTTP 503.", failed.LastError);
        var restarted = new IntegrationOutbox(new(new(handler), options), options, fixture.Store, fixture.Clock);
        Assert.Equal(0, await restarted.ProcessDueAsync(default));
        fixture.Clock.Now = failed.NextAttemptAt;
        Assert.Equal(1, await restarted.ProcessDueAsync(default));
        failed = Assert.Single(await restarted.ListAsync(default));
        Assert.Equal(2, failed.Attempts); Assert.InRange(failed.NextAttemptAt, fixture.Clock.Now.AddSeconds(10), fixture.Clock.Now.AddSeconds(12));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task PermanentProviderErrorsDeadLetterImmediately(int status)
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var options = ZendeskFixture.LiveOptions(fixture.Directory);
        var queue = new IntegrationOutbox(new(new(new ZendeskFakeHandler((_, _) => new((HttpStatusCode)status))), options), options, fixture.Store, fixture.Clock);
        await queue.EnqueueAsync(incident, "create", default); await queue.ProcessDueAsync(default);
        var item = Assert.Single(await queue.ListAsync(default));
        Assert.Equal(IntegrationDeliveryState.DeadLetter, item.State); Assert.Equal(1, item.Attempts);
        Assert.Equal(1, (await queue.GetStatusAsync(default)).DeadLetter);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RetryableStatusesRemainPending(int status)
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var options = ZendeskFixture.LiveOptions(fixture.Directory);
        var queue = new IntegrationOutbox(new(new(new ZendeskFakeHandler((_, _) => new((HttpStatusCode)status))), options), options, fixture.Store, fixture.Clock);
        await queue.EnqueueAsync(incident, "create", default); await queue.ProcessDueAsync(default);
        Assert.Equal(IntegrationDeliveryState.Pending, Assert.Single(await queue.ListAsync(default)).State);
    }

    [Fact]
    public async Task ExhaustedRetryBudgetDeadLettersThenHumanMayRetry()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var options = ZendeskFixture.LiveOptions(fixture.Directory, 2);
        var handler = new ZendeskFakeHandler((_, _) => new(HttpStatusCode.ServiceUnavailable));
        var queue = new IntegrationOutbox(new(new(handler), options), options, fixture.Store, fixture.Clock);
        var created = await queue.EnqueueAsync(incident, "create", default);
        await queue.ProcessDueAsync(default); fixture.Clock.Now = Assert.Single(await queue.ListAsync(default)).NextAttemptAt; await queue.ProcessDueAsync(default);
        Assert.Equal(IntegrationDeliveryState.DeadLetter, Assert.Single(await queue.ListAsync(default)).State);
        Assert.True(await queue.RetryDeadLetterAsync(created.Id, default));
        var retried = Assert.Single(await queue.ListAsync(default)); Assert.Equal(2, retried.Attempts);
        Assert.Equal(IntegrationDeliveryState.Pending, retried.State);
        Assert.False(await queue.RetryDeadLetterAsync("unknown", default));
    }

    [Fact]
    public async Task OneFailedCaseDoesNotBlockOtherCasesButPreservesItsOrder()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync();
        var second = ZendeskFixture.Case(); second.Id = "fake-case-two"; second.ReferenceNumber = "CIV-20261008-FAKE0002";
        await fixture.Store.CreateAsync(second, "fake-key-two", new DeterministicClassifier(), default);
        var options = ZendeskFixture.LiveOptions(fixture.Directory);
        var handler = new ZendeskFakeHandler((_, _) => new(HttpStatusCode.ServiceUnavailable));
        var queue = new IntegrationOutbox(new(new(handler), options), options, fixture.Store, fixture.Clock);
        await queue.EnqueueAsync(incident, "create", default); incident.Version++;
        await queue.EnqueueAsync(incident, "status", default); await queue.EnqueueAsync(second, "create", default);
        Assert.Equal(2, await queue.ProcessDueAsync(default));
        var items = await queue.ListAsync(default);
        Assert.Equal(0, items[1].Attempts); Assert.Equal(1, items[2].Attempts);
    }

    [Fact]
    public async Task MissingCaseDeadLettersWithoutProviderCall()
    {
        using var fixture = new ZendeskFixture(); var queue = Queue(fixture);
        await queue.EnqueueAsync(ZendeskFixture.Case(), "create", default);
        await queue.ProcessDueAsync(default);
        var item = Assert.Single(await queue.ListAsync(default));
        Assert.Equal(IntegrationDeliveryState.DeadLetter, item.State); Assert.Equal(0, item.Attempts);
    }

    [Fact]
    public async Task CancellationBeforeDeliveryDoesNotChargeAnAttempt()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "create", default);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.ProcessDueAsync(cancel.Token));
        Assert.Equal(0, Assert.Single(await queue.ListAsync(default)).Attempts);
    }

    [Fact]
    public async Task CorruptQueueIsNeverSilentlyReplaced()
    {
        using var fixture = new ZendeskFixture(); System.IO.Directory.CreateDirectory(fixture.Directory);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "integration-outbox.json"), "{broken");
        await Assert.ThrowsAsync<JsonException>(() => Queue(fixture).ListAsync(default));
        Assert.Equal("{broken", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "integration-outbox.json")));
    }

    [Fact]
    public async Task RestartBetweenCaseReceiptAndQueueReceiptDoesNotDuplicateAudit()
    {
        using var fixture = new ZendeskFixture(); var incident = await fixture.CreateAsync(); var queue = Queue(fixture);
        await queue.EnqueueAsync(incident, "create", default); await queue.ProcessDueAsync(default);
        var before = await fixture.Store.GetAsync(incident.Id, default);
        var path = Path.Combine(fixture.Directory, "integration-outbox.json");
        // Reconstruct the last checkpoint before queue acknowledgement, after the case receipt persisted.
        var file = await File.ReadAllTextAsync(path);
        file = file.Replace("\"state\": \"Delivered\"", "\"state\": \"Pending\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, file); fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var restarted = Queue(fixture); await restarted.ProcessDueAsync(default);
        var after = await fixture.Store.GetAsync(incident.Id, default);
        Assert.Equal(before!.Version, after!.Version);
        Assert.Single(after.Audit, x => x.Action == "case-record-synced");
        Assert.Equal(IntegrationDeliveryState.Delivered, Assert.Single(await restarted.ListAsync(default)).State);
    }

    [Fact]
    public void BackoffIsExponentialAndCapped()
    {
        using var fixture = new ZendeskFixture(); var queue = Queue(fixture);
        Assert.Equal(TimeSpan.FromSeconds(5), queue.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(10), queue.Backoff(2));
        Assert.Equal(TimeSpan.FromHours(1), queue.Backoff(100));
    }

    private static IntegrationOutbox Queue(ZendeskFixture fixture) => new(
        new ZendeskClient(new(new ZendeskFakeHandler((_, _) => throw new InvalidOperationException("No network"))), fixture.Options),
        fixture.Options, fixture.Store, fixture.Clock);
}
