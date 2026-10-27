using System.Threading.Channels;
using Pylon.Broker.Storage;
using Pylon.Core.Events;
using Pylon.Core.Topics;

namespace Pylon.Broker.Events;

/// <summary>
/// One live subscriber's queue. Bounded: when a subscriber stops reading, its queue fills, it's marked
/// <see cref="Overflowed"/> and closed, and it gets a resync on its way out (scope: "a consumer is slow"). The other
/// subscribers never wait for it.
/// </summary>
public sealed class Subscription : IDisposable
{
    readonly EventHub _hub;
    readonly Channel<EventRecord> _channel;

    internal Subscription(EventHub hub, EventFilter filter, int capacity)
    {
        _hub = hub;
        Filter = filter;
        _channel = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public EventFilter Filter { get; }

    public bool Overflowed { get; private set; }

    public ChannelReader<EventRecord> Reader => _channel.Reader;

    internal void Offer(EventRecord record)
    {
        if (Overflowed || !Filter.Matches(record))
            return;
        if (!_channel.Writer.TryWrite(record))
        {
            Overflowed = true;
            _channel.Writer.TryComplete();
        }
    }

    internal void Close() => _channel.Writer.TryComplete();

    public void Dispose() => _hub.Remove(this);
}

/// <summary>
/// Sequences events (through the store) and fans them out. Storing and distributing happen under one lock, so every
/// subscriber sees events in sequence order with no gaps between replay and live.
/// </summary>
public sealed class EventHub(EventStore store)
{
    readonly Lock _gate = new();
    readonly List<Subscription> _subscriptions = [];

    /// <summary>In-process listeners (the stream widget). Raised after the lock is released, on the publisher's thread.</summary>
    public event Action<IReadOnlyList<EventRecord>>? Published;

    public EventStore Store => store;

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
                return _subscriptions.Count;
        }
    }

    public IReadOnlyList<EventRecord> Publish(IReadOnlyList<EventDraft> drafts)
    {
        IReadOnlyList<EventRecord> records;
        lock (_gate)
        {
            records = store.Append(drafts);
            foreach (var subscription in _subscriptions)
                foreach (var record in records)
                    subscription.Offer(record);
        }
        if (records.Count > 0)
            Published?.Invoke(records);
        return records;
    }

    /// <summary>
    /// Subscribes and returns the sequence the subscription starts after: everything later arrives live, everything up
    /// to it must come from the store. Taken under the lock, so nothing falls between the two.
    /// </summary>
    public (Subscription Subscription, long LiveAfter) Subscribe(EventFilter filter, int capacity = 1000)
    {
        lock (_gate)
        {
            var subscription = new Subscription(this, filter, capacity);
            _subscriptions.Add(subscription);
            return (subscription, store.LatestSequence());
        }
    }

    internal void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
        subscription.Close();
    }

    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var subscription in _subscriptions)
                subscription.Close();
            _subscriptions.Clear();
        }
    }
}
