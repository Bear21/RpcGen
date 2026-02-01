#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace RpcGen.Sample;

internal sealed class MockWebSocketTransport : IRpcTransport
{
    private readonly ConcurrentQueue<ReadOnlyMemory<byte>> _inbound = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _completed;

    public MockWebSocketTransport? Peer { get; private set; }

    public static (MockWebSocketTransport A, MockWebSocketTransport B) CreatePair()
    {
        var a = new MockWebSocketTransport();
        var b = new MockWebSocketTransport();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (Peer is null) throw new InvalidOperationException("Transport is not paired.");
        if (Peer._completed) return Task.CompletedTask;

        Peer._inbound.Enqueue(data);
        Peer._signal.Release();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_completed)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            while (_inbound.TryDequeue(out var msg))
                yield return msg;
        }
    }

    public void Complete()
    {
        _completed = true;
        _signal.Release();
    }
}