#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RpcGen.Sample;

internal sealed class MockWebSocketTransport : IRpcTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

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

        return Peer._inbound.Writer.WriteAsync(data, cancellationToken).AsTask();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var msg in _inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return msg;
    }

    public void Complete()
    {
        _completed = true;
        _inbound.Writer.TryComplete();
    }
}