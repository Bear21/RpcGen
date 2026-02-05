using RpcGen;

namespace RpcGen.Sample
{
   [RpcGen.RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
   public abstract partial class ServerAppRpcBaseClass : IServerRpcInterface { }
}
