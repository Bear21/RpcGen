namespace RpcGen.Sample
{
   using RpcGen;



   [RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
   internal abstract partial class ClientAppRpcBaseClass : IClientRpcInterface { }
}
