namespace RpcGen.RealTest
{
   using System.Threading.Tasks;


   namespace RpcGen.Sample
   {
      public interface IClientRpcInterface
      {
         [ClientToServer]
         Task SendMessage(string channel, string message);
         [ClientToServer]
         Task JoinChannel(string channel);
         [ClientToServer]
         Task LeaveChannel(string channel);
      }
   }
}
