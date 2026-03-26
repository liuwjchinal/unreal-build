using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Backend.Options;

namespace Backend.Services;

public sealed record BuildEventEnvelope(string EventType, Guid BuildId, object Payload, DateTimeOffset OccurredAtUtc);

public sealed class BuildEventBroker(AppOptions appOptions)
{
    private const string GlobalKey = "*";
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, appOptions.EventHeartbeatSeconds));
    private readonly int _channelCapacity = Math.Max(16, appOptions.EventChannelCapacity);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<BuildEventEnvelope>>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    public ValueTask PublishAsync(BuildEventEnvelope envelope)
    {
        PublishToKey(GlobalKey, envelope);
        PublishToKey(envelope.BuildId.ToString("N"), envelope);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<BuildEventEnvelope> Subscribe(Guid? buildId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = buildId?.ToString("N") ?? GlobalKey;
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<BuildEventEnvelope>(new BoundedChannelOptions(_channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var bucket = _subscriptions.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Channel<BuildEventEnvelope>>());
        bucket.TryAdd(subscriptionId, channel);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (channel.Reader.TryRead(out var envelope))
                {
                    yield return envelope;
                }

                var waitForDataTask = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var heartbeatTask = Task.Delay(_heartbeatInterval, cancellationToken);
                var completedTask = await Task.WhenAny(waitForDataTask, heartbeatTask);

                if (completedTask == heartbeatTask)
                {
                    yield return new BuildEventEnvelope(
                        "heartbeat",
                        buildId ?? Guid.Empty,
                        new { buildId, heartbeat = true },
                        DateTimeOffset.UtcNow);
                    continue;
                }

                if (!await waitForDataTask)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (_subscriptions.TryGetValue(key, out var subscriptions))
            {
                subscriptions.TryRemove(subscriptionId, out _);
                if (subscriptions.IsEmpty)
                {
                    _subscriptions.TryRemove(key, out _);
                }
            }
        }
    }

    private void PublishToKey(string key, BuildEventEnvelope envelope)
    {
        if (!_subscriptions.TryGetValue(key, out var subscriptions))
        {
            return;
        }

        foreach (var channel in subscriptions.Values)
        {
            channel.Writer.TryWrite(envelope);
        }
    }
}
