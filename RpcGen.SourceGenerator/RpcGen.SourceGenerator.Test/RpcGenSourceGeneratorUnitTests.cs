using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;

namespace RpcGen.SourceGenerator.Test
{
   [TestClass]
   public class RpcGenSourceGeneratorTests
   {
      [TestMethod]
      public async Task Generates_Server_and_Client_stubs_and_compiles()
      {
         var test = new CSharpSourceGeneratorTest<RpcGen.SourceGenerator.RpcGenerator, DefaultVerifier>
         {
            TestState =
                {
                    Sources =
                    {
"""
using System;
using System.Threading.Tasks;
using RpcGen;

namespace RpcGen.Sample
{
    public class AppVersion
    {
        public string Version { get; set; } = "";
        public DateTime BuildDate { get; set; }
        public string UserName { get; set; } = "";
    }

    public interface IServerRpcInterface
    {
        Task<AppVersion> Welcome();

        void ReceiveMessage(string channel, string user, string message);
    }

    public interface IClientRpcInterface
    {
        Task SendMessage(string channel, string message);
        Task JoinChannel(string channel);
        Task LeaveChannel(string channel);
    }

    [RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
    internal abstract partial class ServerAppRpcBaseClass : IServerRpcInterface { }

    [RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
    internal abstract partial class ClientAppRpcBaseClass : IClientRpcInterface { }
}
"""
                    },

                    // Ensure modern BCL is available (WebSocket, System.Text.Json, etc.)
                    ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                },

            // We only assert it compiles; we don't snapshot generated sources here.
            TestBehaviors = TestBehaviors.SkipGeneratedSourcesCheck,

         };
         //test.TestState.AdditionalReferences.Add(
         //    MetadataReference.CreateFromFile(typeof(RpcGen.RpcInterfaceAttribute).Assembly.Location)
         //);

         await test.RunAsync();
      }
   }
}
