using RpcGen;

namespace RpcGen.Sample
{
   [RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
   internal abstract partial class ServerAppRpcBaseClass : IServerRpcInterface { }
}
