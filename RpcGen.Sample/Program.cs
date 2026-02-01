#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RpcGen.Sample;

internal static class Program
{
   public static async Task Main()
   {
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

      var (serverTransport, clientTransport) = MockWebSocketTransport.CreatePair();

      var server = new ServerApp("server-1", serverTransport);
      var client = new ClientApp("user1", clientTransport);

      var serverLoop = server.RunAsync(cts.Token);
      var clientLoop = client.RunAsync(cts.Token);

      var response = await server.Welcome();
      server.ClientName = response.UserName;

      var generalChannel = await client.CreateChannel("general");

      await client.JoinChannel(generalChannel);
      await client.SendMessage(generalChannel, "hello over mocked websocket");
      await client.SendMessage(generalChannel, "second message");
      var channels = await client.GetChannelList();
      Console.WriteLine("Channels: " + string.Join(", ", channels));

      await client.LeaveChannel(generalChannel);

      // let notifications drain
      await Task.Delay(100, cts.Token);

      cts.Cancel();

      try
      {
         await Task.WhenAll(serverLoop, clientLoop);
      }
      catch (OperationCanceledException)
      {
         // expected
      }
   }
}