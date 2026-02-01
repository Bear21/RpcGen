namespace RpcGen.RealTest
{
   using RpcGen;


   namespace RpcGen.Sample
   {

      [RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
      internal abstract partial class ClientAppRpcBaseClass { }
   }
}
