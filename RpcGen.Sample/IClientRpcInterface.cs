namespace RpcGen.Sample
{
   using System.Threading.Tasks;


   public interface IClientRpcInterface
   {
      Task SendMessage(Guid channel, string message);
      Task<Guid> CreateChannel(string channel);
      Task JoinChannel(Guid channel);
      Task LeaveChannel(Guid channel);

      Task<List<(Guid channelId, string channelName)>> GetChannelList();
   }
}
