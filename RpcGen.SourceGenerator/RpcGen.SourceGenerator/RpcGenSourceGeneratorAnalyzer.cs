using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RpcGen.SourceGenerator
{
   [Generator(LanguageNames.CSharp)]
   public sealed class RpcGenerator : IIncrementalGenerator
   {
      public void Initialize(IncrementalGeneratorInitializationContext context)
      {
         // Emit a tiny runtime once for the compilation.
         context.RegisterPostInitializationOutput(ctx =>
         {
            ctx.AddSource("RpcRuntime.g.cs", SourceText.From(RuntimeSource, Encoding.UTF8));
            // We intentionally DO NOT emit attributes here because they live in RpcGen.Abstractions.
         });

         // Pick class decls that have attributes on them (cheap filter)
         var classWithAttrs = context.SyntaxProvider.CreateSyntaxProvider(
             predicate: static (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
             transform: static (ctx, _) =>
             {
                var cds = (ClassDeclarationSyntax)ctx.Node;
                var model = ctx.SemanticModel;
                var classSymbol = model.GetDeclaredSymbol(cds) as INamedTypeSymbol;
                if (classSymbol is null) return null;

                // Look for [RpcInterface(...)] by short name (namespace-agnostic)
                foreach (var ad in classSymbol.GetAttributes())
                {
                   var attrClass = ad.AttributeClass;
                   if (attrClass is null) continue;

                   var shortName = attrClass.Name; // e.g., "RpcInterfaceAttribute"
                   if (shortName.Equals("RpcInterfaceAttribute", StringComparison.Ordinal) ||
                          shortName.Equals("RpcInterface", StringComparison.Ordinal))
                   {
                      return new RpcClassCandidate(classSymbol, ad);
                   }
                }

                return null;
             })
             .Where(static c => c is not null)!;

         // Generate for each distinct class
         context.RegisterSourceOutput(classWithAttrs.Collect(), (spc, list) =>
         {
            var distinct = list
                .Where(x => x is not null)
                .Distinct(RpcClassCandidateComparer.Instance)!;

            foreach (var candidate in distinct)
            {
               try
               {
                  GenerateForClass(spc, candidate!);
               }
               catch (Exception ex)
               {
                  var diag = Diagnostic.Create(
                      new DiagnosticDescriptor(
                          id: "RPCGEN001",
                          title: "RPC generation error",
                          messageFormat: "Error generating RPC code for {0}: {1}",
                          category: "RpcGen",
                          defaultSeverity: DiagnosticSeverity.Error,
                          isEnabledByDefault: true),
                      candidate!.ClassSymbol.Locations.FirstOrDefault(),
                      candidate.ClassSymbol.Name,
                      ex.Message);
                  spc.ReportDiagnostic(diag);
               }
            }
         });
      }

      private static void GenerateForClass(SourceProductionContext spc, RpcClassCandidate candidate)
      {
         var classSymbol = candidate.ClassSymbol;

         // Expect exactly two typeof(...) ctor args
         if (candidate.Attribute is null || candidate.Attribute.ConstructorArguments.Length != 2)
            return;

         if (candidate.Attribute.ConstructorArguments[0].Value is not INamedTypeSymbol outboundIface ||
             candidate.Attribute.ConstructorArguments[1].Value is not INamedTypeSymbol inboundIface)
            return;

         // Gather methods
         var outboundMethods = MethodsOf(outboundIface).ToArray();
         var inboundMethods = MethodsOf(inboundIface).ToArray();

         // Prepare codegen
         var ns = classSymbol.ContainingNamespace?.IsGlobalNamespace == false
             ? classSymbol.ContainingNamespace!.ToDisplayString()
             : null;

         var sb = new StringBuilder();

         if (!string.IsNullOrEmpty(ns))
         {
            sb.AppendLine($"namespace {ns};");
         }

         sb.AppendLine($"#nullable enable");
         sb.AppendLine("using System;");
         sb.AppendLine("using System.Buffers;");
         sb.AppendLine("using System.Collections.Concurrent;");
         sb.AppendLine("using System.Text.Json;");
         sb.AppendLine("using System.Threading;");
         sb.AppendLine("using System.Threading.Tasks;");
         sb.AppendLine();

         var className = classSymbol.Name;

         // Start partial class body
         sb.AppendLine($"internal partial class {className}");
         sb.AppendLine("{");
         sb.AppendLine("    private IRpcTransport? _rpcTransport;");
         sb.AppendLine("    private long _rpcNextId = 1;");
         sb.AppendLine("    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _rpcPending = new();");
         sb.AppendLine();
         sb.AppendLine("    protected void AttachTransport(IRpcTransport transport) => _rpcTransport = transport;");
         sb.AppendLine();
         sb.AppendLine("    public async Task RunAsync(CancellationToken cancellationToken = default)");
         sb.AppendLine("    {");
         sb.AppendLine("        if (_rpcTransport is null) throw new InvalidOperationException(\"Transport not attached. Call AttachTransport first.\");");
         sb.AppendLine("        await foreach (var msgBytes in _rpcTransport.ReadAsync(cancellationToken).ConfigureAwait(false))");
         sb.AppendLine("        {");
         sb.AppendLine("            using var doc = JsonDocument.Parse(msgBytes);");
         sb.AppendLine("            var root = doc.RootElement;");
         sb.AppendLine("            var id = root.GetProperty(\"id\").GetInt64();");
         sb.AppendLine("            var kind = (RpcKind)root.GetProperty(\"k\").GetByte();");
         sb.AppendLine("            var method = root.GetProperty(\"m\").GetString()!;");
         sb.AppendLine("            var iface = root.GetProperty(\"i\").GetString()!; // not used for dispatch here, but available");
         sb.AppendLine("            var hasErr = root.TryGetProperty(\"err\", out var errProp) && errProp.ValueKind != JsonValueKind.Null;");
         sb.AppendLine("            var payload = root.GetProperty(\"p\");");
         sb.AppendLine();
         sb.AppendLine("            if (kind == RpcKind.Response)");
         sb.AppendLine("            {");
         sb.AppendLine("                if (_rpcPending.TryRemove(id, out var tcs))");
         sb.AppendLine("                {");
         sb.AppendLine("                    if (hasErr)");
         sb.AppendLine("                        tcs.TrySetException(new RpcRemoteException(errProp.GetString() ?? \"Remote error\"));");
         sb.AppendLine("                    else");
         sb.AppendLine("                        tcs.TrySetResult(payload);");
         sb.AppendLine("                }");
         sb.AppendLine("                continue;");
         sb.AppendLine("            }");
         sb.AppendLine();
         sb.AppendLine($"            // Inbound dispatch for {inboundIface.Name}");
         sb.AppendLine("            switch (method)");
         sb.AppendLine("            {");

         // Inbound switch cases
         foreach (var m in inboundMethods)
         {
            var reqType = $"{className}__{inboundIface.Name}__{m.Name}__Request";
            sb.AppendLine($"                case \"{m.Name}\":");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var req = ({reqType}?)JsonSerializer.Deserialize(payload.GetRawText(), typeof({reqType}), RpcJson.Options);");

            var argList = string.Join(", ", m.Parameters.Select(p => $"req!.{UpperFirst(p.Name)}"));

            var retKind = GetReturnKind(m.ReturnType);
            switch (retKind)
            {
               case ReturnKind.Void:
                  // void inbound -> fire and forget, no response
                  sb.AppendLine($"                    {m.Name}({argList});");
                  sb.AppendLine("                    break;");
                  break;

               case ReturnKind.Task:
                  sb.AppendLine($"                    await {m.Name}({argList}).ConfigureAwait(false);");
                  sb.AppendLine("                    await SendResponseAsync(id, null).ConfigureAwait(false);");
                  sb.AppendLine("                    break;");
                  break;

               case ReturnKind.TaskOfT:
                  var respType = $"{className}__{inboundIface.Name}__{m.Name}__Response";
                  sb.AppendLine($"                    var result = await {m.Name}({argList}).ConfigureAwait(false);");
                  sb.AppendLine($"                    await SendResponseAsync(id, new {respType} {{ Result = result }}).ConfigureAwait(false);");
                  sb.AppendLine("                    break;");
                  break;

               default:
                  sb.AppendLine("                    // Unsupported inbound return type");
                  sb.AppendLine("                    await SendErrorAsync(id, \"Unsupported inbound return type\").ConfigureAwait(false);");
                  sb.AppendLine("                    break;");
                  break;
            }

            sb.AppendLine("                }");
         }

         sb.AppendLine("                default:");
         sb.AppendLine("                    await SendErrorAsync(id, $\"Unknown inbound method '{method}'\").ConfigureAwait(false);");
         sb.AppendLine("                    break;");
         sb.AppendLine("            }");
         sb.AppendLine("        }");
         sb.AppendLine("    }");
         sb.AppendLine();

         // AwaitResponseAsync
         sb.AppendLine("    private async Task<JsonElement> AwaitResponseAsync(long id)");
         sb.AppendLine("    {");
         sb.AppendLine("        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);");
         sb.AppendLine("        _rpcPending[id] = tcs;");
         sb.AppendLine("        return await tcs.Task.ConfigureAwait(false);");
         sb.AppendLine("    }");
         sb.AppendLine();

         // SendRequestAsync
         sb.AppendLine("    private async Task SendRequestAsync(string iface, string method, long id, object? payload, Type? payloadType, bool notification = false)");
         sb.AppendLine("    {");
         sb.AppendLine("        if (_rpcTransport is null) throw new InvalidOperationException(\"No transport\");");
         sb.AppendLine("        var buffer = new System.Buffers.ArrayBufferWriter<byte>();");
         sb.AppendLine("        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))");
         sb.AppendLine("        {");
         sb.AppendLine("            writer.WriteStartObject();");
         sb.AppendLine("            writer.WriteNumber(\"id\", id);");
         sb.AppendLine("            writer.WriteString(\"i\", iface);");
         sb.AppendLine("            writer.WriteString(\"m\", method);");
         sb.AppendLine("            writer.WriteNumber(\"k\", (byte)(notification ? RpcKind.Notification : RpcKind.Request));");
         sb.AppendLine("            writer.WritePropertyName(\"p\");");
         sb.AppendLine("            if (payload is null)");
         sb.AppendLine("            {");
         sb.AppendLine("                writer.WriteStartObject(); writer.WriteEndObject();");
         sb.AppendLine("            }");
         sb.AppendLine("            else");
         sb.AppendLine("            {");
         sb.AppendLine("                var json = System.Text.Json.JsonSerializer.Serialize(payload, payloadType!, RpcJson.Options);");
         sb.AppendLine("                using var payloadDoc = System.Text.Json.JsonDocument.Parse(json);");
         sb.AppendLine("                payloadDoc.RootElement.WriteTo(writer);");
         sb.AppendLine("            }");
         sb.AppendLine("            writer.WriteEndObject();");
         sb.AppendLine("            writer.Flush();");
         sb.AppendLine("        }");
         sb.AppendLine("        await _rpcTransport.SendAsync(buffer.WrittenMemory, default).ConfigureAwait(false);");
         sb.AppendLine("    }");
         sb.AppendLine();

         // SendResponseAsync
         sb.AppendLine("    private async Task SendResponseAsync(long id, object? payload)");
         sb.AppendLine("    {");
         sb.AppendLine("        if (_rpcTransport is null) throw new InvalidOperationException(\"No transport\");");
         sb.AppendLine("        var buffer = new System.Buffers.ArrayBufferWriter<byte>();");
         sb.AppendLine("        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))");
         sb.AppendLine("        {");
         sb.AppendLine("            writer.WriteStartObject();");
         sb.AppendLine("            writer.WriteNumber(\"id\", id);");
         sb.AppendLine($"            writer.WriteString(\"i\", \"{inboundIface.Name}\");");
         sb.AppendLine("            writer.WriteString(\"m\", \"\");");
         sb.AppendLine("            writer.WriteNumber(\"k\", (byte)RpcKind.Response);");
         sb.AppendLine("            writer.WritePropertyName(\"p\");");
         sb.AppendLine("            if (payload is null)");
         sb.AppendLine("            {");
         sb.AppendLine("                writer.WriteStartObject(); writer.WriteEndObject();");
         sb.AppendLine("            }");
         sb.AppendLine("            else");
         sb.AppendLine("            {");
         sb.AppendLine("                var json = System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType(), RpcJson.Options);");
         sb.AppendLine("                using var payloadDoc = System.Text.Json.JsonDocument.Parse(json);");
         sb.AppendLine("                payloadDoc.RootElement.WriteTo(writer);");
         sb.AppendLine("            }");
         sb.AppendLine("            writer.WriteEndObject();");
         sb.AppendLine("            writer.Flush();");
         sb.AppendLine("        }");
         sb.AppendLine("        await _rpcTransport.SendAsync(buffer.WrittenMemory, default).ConfigureAwait(false);");
         sb.AppendLine("    }");
         sb.AppendLine();

         // SendErrorAsync
         sb.AppendLine("    private async Task SendErrorAsync(long id, string error)");
         sb.AppendLine("    {");
         sb.AppendLine("        if (_rpcTransport is null) throw new InvalidOperationException(\"No transport\");");
         sb.AppendLine("        var buffer = new System.Buffers.ArrayBufferWriter<byte>();");
         sb.AppendLine("        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))");
         sb.AppendLine("        {");
         sb.AppendLine("            writer.WriteStartObject();");
         sb.AppendLine("            writer.WriteNumber(\"id\", id);");
         sb.AppendLine($"            writer.WriteString(\"i\", \"{inboundIface.Name}\");");
         sb.AppendLine("            writer.WriteString(\"m\", \"\");");
         sb.AppendLine("            writer.WriteNumber(\"k\", (byte)RpcKind.Response);");
         sb.AppendLine("            writer.WriteString(\"err\", error);");
         sb.AppendLine("            writer.WritePropertyName(\"p\"); writer.WriteStartObject(); writer.WriteEndObject();");
         sb.AppendLine("            writer.WriteEndObject();");
         sb.AppendLine("            writer.Flush();");
         sb.AppendLine("        }");
         sb.AppendLine("        await _rpcTransport.SendAsync(buffer.WrittenMemory, default).ConfigureAwait(false);");
         sb.AppendLine("    }");
         sb.AppendLine();

         // OUTBOUND implementations: implement methods of outboundIface on this class
         foreach (var m in outboundMethods)
         {
            var ifaceName = outboundIface.Name;
            var reqType = $"{className}__{ifaceName}__{m.Name}__Request";
            var respType = $"{className}__{ifaceName}__{m.Name}__Response";
            var retKind = GetReturnKind(m.ReturnType);

            // Mark async only when return is Task or Task<T>
            bool makeAsync = retKind == ReturnKind.Task || retKind == ReturnKind.TaskOfT;

            // Method signature as in interface (public)
            var methodSig = BuildMethodSignature(m, isPublic: true, isAbstract: false, isProtected: false, isAsync: makeAsync);
            sb.AppendLine($"    // Outbound: {ifaceName}.{m.Name}");
            sb.AppendLine($"    {methodSig}");
            sb.AppendLine("    {");
            sb.AppendLine("        var id = Interlocked.Increment(ref _rpcNextId);");

            // payload init
            if (m.Parameters.Length == 0)
            {
               sb.AppendLine($"        var payload = new {reqType}();");
            }
            else
            {
               var inits = string.Join(", ", m.Parameters.Select(p => $"{UpperFirst(p.Name)} = {p.Name}"));
               sb.AppendLine($"        var payload = new {reqType} {{ {inits} }};");
            }
            sb.AppendLine($"        var payloadType = typeof({reqType});");

            switch (retKind)
            {
               case ReturnKind.Void:
                  // notification
                  sb.AppendLine($"        _ = SendRequestAsync(\"{ifaceName}\", \"{m.Name}\", id, payload, payloadType, notification: true);");
                  sb.AppendLine("    }");
                  break;

               case ReturnKind.Task:
                  sb.AppendLine($"        await SendRequestAsync(\"{ifaceName}\", \"{m.Name}\", id, payload, payloadType).ConfigureAwait(false);");
                  sb.AppendLine("        _ = await AwaitResponseAsync(id).ConfigureAwait(false);");
                  sb.AppendLine("    }");
                  break;

               case ReturnKind.TaskOfT:
                  sb.AppendLine($"        await SendRequestAsync(\"{ifaceName}\", \"{m.Name}\", id, payload, payloadType).ConfigureAwait(false);");
                  sb.AppendLine("        var p = await AwaitResponseAsync(id).ConfigureAwait(false);");
                  sb.AppendLine($"        var resp = ({respType}?)System.Text.Json.JsonSerializer.Deserialize(p.GetRawText(), typeof({respType}), RpcJson.Options);");
                  sb.AppendLine("        return resp!.Result;");
                  sb.AppendLine("    }");
                  break;

               default:
                  sb.AppendLine("        throw new NotSupportedException(\"Unsupported outbound return type\");");
                  sb.AppendLine("    }");
                  break;
            }

            // Request DTO
            sb.AppendLine($"    internal sealed class {reqType}");
            sb.AppendLine("    {");
            foreach (var p in m.Parameters)
            {
               var pt = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
               sb.AppendLine($"        public {pt} {UpperFirst(p.Name)} {{ get; set; }}");
            }
            sb.AppendLine("    }");

            // Response DTO for Task<T>
            if (retKind == ReturnKind.TaskOfT)
            {
               var t = ((INamedTypeSymbol)m.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
               sb.AppendLine($"    internal sealed class {respType}");
               sb.AppendLine("    {");
               sb.AppendLine($"        public {t} Result {{ get; set; }}");
               sb.AppendLine("    }");
            }

            sb.AppendLine();
         }

         // INBOUND abstract handlers + DTOs
         foreach (var m in inboundMethods)
         {
            var inboundSig = BuildMethodSignature(m, isPublic: false, isAbstract: true, isProtected: true, isAsync: false);
            sb.AppendLine($"    // Inbound handler for {inboundIface.Name}.{m.Name}");
            sb.AppendLine($"    {inboundSig};");

            // Request DTO
            var reqType = $"{className}__{inboundIface.Name}__{m.Name}__Request";
            sb.AppendLine($"    internal sealed class {reqType}");
            sb.AppendLine("    {");
            foreach (var p in m.Parameters)
            {
               var pt = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
               sb.AppendLine($"        public {pt} {UpperFirst(p.Name)} {{ get; set; }}");
            }
            sb.AppendLine("    }");

            // Response DTO (only for Task<T>)
            var rk = GetReturnKind(m.ReturnType);
            if (rk == ReturnKind.TaskOfT)
            {
               var respType = $"{className}__{inboundIface.Name}__{m.Name}__Response";
               var t = ((INamedTypeSymbol)m.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
               sb.AppendLine($"    internal sealed class {respType}");
               sb.AppendLine("    {");
               sb.AppendLine($"        public {t} Result {{ get; set; }}");
               sb.AppendLine("    }");
            }

            sb.AppendLine();
         }

         // End class
         sb.AppendLine("}");

         spc.AddSource($"{className}.Rpc.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
      }

      private static IEnumerable<IMethodSymbol> MethodsOf(INamedTypeSymbol iface) =>
          iface.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary);

      private static string UpperFirst(string s) =>
          string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

      private static string BuildMethodSignature(IMethodSymbol m, bool isPublic, bool isAbstract = false, bool isProtected = false, bool isAsync = false)
      {
         var visibility = isPublic ? "public" : isProtected ? "protected" : "private";
         var mods = visibility;
         if (isAbstract) mods += " abstract";
         if (isAsync) mods += " async";
         var ret = m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
         var name = m.Name;
         var parameters = string.Join(", ", m.Parameters.Select(p =>
         {
            var pt = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"{pt} {p.Name}";
         }));
         return $"{mods} {ret} {name}({parameters})";
      }

      private enum ReturnKind { Void, Task, TaskOfT, Other }

      private static ReturnKind GetReturnKind(ITypeSymbol type)
      {
         if (type is null) return ReturnKind.Other;
         if (type.SpecialType == SpecialType.System_Void) return ReturnKind.Void;

         if (type is INamedTypeSymbol named)
         {
            // Check Task and Task<T> robustly
            if (named.ConstructedFrom is INamedTypeSymbol cf &&
                cf.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.Tasks.Task")
            {
               return named.IsGenericType ? ReturnKind.TaskOfT : ReturnKind.Task;
            }

            // Fallback on simple name "Task"
            if (named.Name == "Task")
            {
               return named.IsGenericType ? ReturnKind.TaskOfT : ReturnKind.Task;
            }
         }

         return ReturnKind.Other;
      }

      private static readonly string RuntimeSource =
@"// <auto-generated/>
#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal interface IRpcTransport
{
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class WebSocketTransport : IRpcTransport
{
    private readonly WebSocket _ws;
    public WebSocketTransport(WebSocket ws) => _ws = ws;

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        => await _ws.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var writer = new ArrayBufferWriter<byte>();

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            writer.Clear();
            WebSocketReceiveResult? rr;
            do
            {
                var seg = new ArraySegment<byte>(buffer);
                rr = await _ws.ReceiveAsync(seg, ct).ConfigureAwait(false);
                if (rr.MessageType == WebSocketMessageType.Close) yield break;
                writer.Write(seg.AsSpan(0, rr.Count));
            }
            while (!rr.EndOfMessage);

            yield return writer.WrittenMemory;
        }
    }
}

internal static class RpcJson
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

internal enum RpcKind : byte
{
    Request = 0,
    Response = 1,
    Notification = 2
}

public sealed class RpcRemoteException : Exception
{
    public RpcRemoteException(string message) : base(message) { }
}
";

      private sealed record RpcClassCandidate(INamedTypeSymbol ClassSymbol, AttributeData Attribute);

      private sealed class RpcClassCandidateComparer : IEqualityComparer<RpcClassCandidate?>
      {
         public static readonly RpcClassCandidateComparer Instance = new();

         public bool Equals(RpcClassCandidate? x, RpcClassCandidate? y)
             => SymbolEqualityComparer.Default.Equals(x?.ClassSymbol, y?.ClassSymbol);

         public int GetHashCode(RpcClassCandidate? obj)
             => obj is null ? 0 : SymbolEqualityComparer.Default.GetHashCode(obj.ClassSymbol);
      }
   }
}