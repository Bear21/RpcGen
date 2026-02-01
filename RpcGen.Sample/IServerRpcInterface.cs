using System.Threading.Tasks;
namespace RpcGen.Sample
{
   public interface IServerRpcInterface
   {
      Task<AppVersion> Welcome();

      void ReceiveMessage(Guid channel, string user, string message);
   }
}
