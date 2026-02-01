#nullable enable
using System;
using System.Threading.Tasks;

namespace RpcGen.Sample;

internal sealed class ClientApp : ClientAppRpcBaseClass
{
    private readonly string _userName;

   public ClientApp(string userName, IRpcTransport transport)
   {
      _userName = userName;
      AttachTransport(transport);
   }

    protected override Task<AppVersion> Welcome()
        => Task.FromResult(new AppVersion
        {
            Version = "RealTest/1.0",
            BuildDate = DateTime.UtcNow,
            UserName = _userName
        });

    protected override void ReceiveMessage(Guid channel, string user, string message)
        => Console.WriteLine($"[client:{_userName}] #{channel} {user}: {message}");
}