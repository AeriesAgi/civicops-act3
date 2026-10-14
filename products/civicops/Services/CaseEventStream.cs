using System.Collections.Concurrent;
using System.Threading.Channels;
namespace CivicOps.Services;

public sealed record CaseStreamEvent(string Type);

// Only event names are broadcast. Operational data is fetched through authenticated routes.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a municipal event stream, not a System.IO.Stream implementation.")]
public sealed class CaseEventStream
{
    private readonly ConcurrentDictionary<Guid, (Channel<CaseStreamEvent> Channel, bool TrainingOnly, string TenantId)> _subscribers = new();
    private readonly object _subscriberGate = new();
    public const int MaximumSubscribers = 256;
    public (Guid Id, ChannelReader<CaseStreamEvent> Reader) Subscribe(bool trainingOnly = false)
    {
        if (!TrySubscribe(out var subscription, trainingOnly)) throw new InvalidOperationException("Event subscriber capacity reached.");
        return subscription;
    }
    public bool TrySubscribe(out (Guid Id, ChannelReader<CaseStreamEvent> Reader) subscription, bool trainingOnly = false)
    {
        lock (_subscriberGate)
        {
            if (_subscribers.Count >= MaximumSubscribers) { subscription = (Guid.Empty, null!); return false; }
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<CaseStreamEvent>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
            _subscribers[id] = (channel, trainingOnly, TenantContext.CurrentId); subscription = (id, channel.Reader); return true;
        }
    }
    public void Unsubscribe(Guid id) { if (_subscribers.TryRemove(id, out var subscription)) subscription.Channel.Writer.TryComplete(); }
    public void Publish(string eventType = "refresh")
    {
        if (eventType is not ("refresh" or "training.completed" or "certification.granted") && !eventType.StartsWith("sos.", StringComparison.Ordinal)) throw new ArgumentOutOfRangeException(nameof(eventType));
        foreach (var subscription in _subscribers.Values)
            // Training subscribers receive the historical training/certification events, while
            // dashboard refreshes and SOS alerts stay scoped to operational subscribers.
            if (subscription.TenantId == TenantContext.CurrentId &&
                (!subscription.TrainingOnly || (eventType != "refresh" && !eventType.StartsWith("sos.", StringComparison.Ordinal))))
                subscription.Channel.Writer.TryWrite(new(eventType));
    }
}
