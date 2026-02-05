namespace RpcGen.Sample
{
   using RpcGen;



   [RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
   public abstract partial class ClientAppRpcBaseClass { }
}
