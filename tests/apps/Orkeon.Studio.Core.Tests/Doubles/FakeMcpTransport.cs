using System.Threading.Channels;
using Orkeon.Infrastructure.MCP;

namespace Orkeon.Studio.Core.Tests.Doubles;

#pragma warning disable ORKEXP004 // The MCP transport is experimental in Orkeon; the test only needs an adapter to exist.

/// <summary>
/// An MCP transport that never connects: it lets an <see cref="McpToolAdapter"/> exist, as an MCP
/// server's tool the engine counts as opaque, without a server. Nothing is ever sent.
/// </summary>
public sealed class FakeMcpTransport : IMcpTransport
{
    public bool IsConnected => false;

    public ChannelReader<JsonRpcNotification> Notifications { get; } = Channel.CreateUnbounded<JsonRpcNotification>().Reader;

    public Task ConnectAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("No server behind this transport.");

    public Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("No server behind this transport.");

    public Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken ct = default) =>
        throw new NotSupportedException("No server behind this transport.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

#pragma warning restore ORKEXP004
