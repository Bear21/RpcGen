namespace RpcGen.RealTest
{
   using System.Threading.Tasks;


   namespace RpcGen.Sample
   {
      public interface IServerRpcInterface
      {
         [ServerToClient]
         Task<AppVersion> Welcome();

         [ServerToClient]
         void ReceiveMessage(string channel, string user, string message);
      }
   }
}
