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
        [ServerToClient]
        Task<AppVersion> Welcome();

        [ServerToClient]
        void ReceiveMessage(string channel, string user, string message);
    }

    public interface IClientRpcInterface
    {
        [ClientToServer]
        Task SendMessage(string channel, string message);
        [ClientToServer]
        Task JoinChannel(string channel);
        [ClientToServer]
        Task LeaveChannel(string channel);
    }

    [RpcInterface(typeof(IServerRpcInterface), typeof(IClientRpcInterface))]
    internal abstract partial class ServerAppRpcBaseClass { }

    [RpcInterface(typeof(IClientRpcInterface), typeof(IServerRpcInterface))]
    internal abstract partial class ClientAppRpcBaseClass { }
}
"""
                    },

                    // Ensure modern BCL is available (WebSocket, System.Text.Json, etc.)
                    ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                },

            // We only assert it compiles; we don't snapshot generated sources here.
            TestBehaviors = TestBehaviors.SkipGeneratedSourcesCheck,

         };
         test.TestState.AdditionalReferences.Add(
             MetadataReference.CreateFromFile(typeof(RpcGen.ServerToClientAttribute).Assembly.Location)
         );

         try
         {
            foreach (var src in test.TestState.Sources)
            {
               Console.WriteLine("===== Source =====");
               Console.WriteLine(src.content.ToString());
            }

            await test.RunAsync();
         }
         catch (Exception ex)
         {
            Console.WriteLine("Diagnostics:");
            foreach (var d in test.TestState.ExpectedDiagnostics)
               Console.WriteLine(d.ToString());
            foreach (var generated in test.TestState.GeneratedSources)
            {
               Console.WriteLine($"===== {generated.filename} =====");
               Console.WriteLine(generated.content.ToString());
            }
            Assert.Fail($"Source generator test failed: {ex}");
         }
         foreach (var generated in test.TestState.GeneratedSources)
         {
            Console.WriteLine($"===== {generated.filename} =====");
            Console.WriteLine(generated.content.ToString());
         }
      }
   }
}
