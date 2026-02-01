#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RpcGen.Sample;

internal sealed class ServerApp : ServerAppRpcBaseClass
{
   private readonly string _serverName;
   private readonly ConcurrentDictionary<Guid, string> _channels = new();
   public string ClientName { get; set; } = "";

   public ServerApp(string serverName, IRpcTransport transport)
   {
      _serverName = serverName;
      AttachTransport(transport);
   }

   protected override Task<Guid> CreateChannel(string channel)
   {
      Guid channelId = Guid.NewGuid();
      if (!_channels.TryAdd(channelId, channel))
         throw new Exception("Unable to create channel, try again later");

      Console.WriteLine($"[{_serverName}] {ClientName} created channel '{channel}' ({channelId})");
      return Task.FromResult(channelId);
   }

   protected override Task<List<(Guid channelId, string channelName)>> GetChannelList()
   {
      return Task.FromResult((from channel in _channels
                              select (channel.Key, channel.Value)).ToList());
   }

   protected override Task JoinChannel(Guid channel)
   {
      if (_channels.TryGetValue(channel, out var channelName))
      {
         Console.WriteLine($"[{_serverName}] client {ClientName} joined '{channelName}'");

         // Server -> client notification
         ReceiveMessage(channel, "server", $"Welcome to '{channelName}' {ClientName}");
         return Task.CompletedTask;
      }
      throw new InvalidOperationException($"Channel '{channel}' does not exist");
   }


   protected override Task LeaveChannel(Guid channel)
   {
      if (_channels.TryGetValue(channel, out var channelName))
      {
         Console.WriteLine($"[{_serverName}] client {ClientName} left '{channelName}'");

         ReceiveMessage(channel, "server", $"{ClientName} Left '{channelName}'");
         return Task.CompletedTask;
      }

      throw new InvalidOperationException($"Channel '{channel}' does not exist");
   }

   protected override Task SendMessage(Guid channel, string message)
   {
      if (_channels.TryGetValue(channel, out var channelName))
      {
         Console.WriteLine($"[{_serverName}] inbound message '{channel}': {message}");

         // Echo back to client
         ReceiveMessage(channel, "client", message);
         return Task.CompletedTask;
      }
      throw new InvalidOperationException($"Channel '{channel}' does not exist");
   }
}