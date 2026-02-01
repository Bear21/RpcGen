namespace RpcGen.RealTest
{
   namespace RpcGen.Sample
   {
      [RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
      internal abstract partial class ServerAppRpcBaseClass : IServerRpcInterface { }

      internal class ServerAppRpc : ServerAppRpcBaseClass
      {
         protected override Task JoinChannel(string channel)
         {
            throw new NotImplementedException();
         }

         protected override Task LeaveChannel(string channel)
         {
            throw new NotImplementedException();
         }

         protected override Task SendMessage(string channel, string message)
         {
            throw new NotImplementedException();
         }
      }

      internal class UsageServerAppRpc
      {
         public async void FooAsync()
         {
            var server = new ServerAppRpc();
            var result = await server.Welcome();
         }
      }
   }
}
