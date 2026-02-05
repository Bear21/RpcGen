using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.IO;
using System.Reflection;

namespace RpcGen.SourceGenerator
{
   [Generator(LanguageNames.CSharp)]
   public sealed class RpcGenerator : IIncrementalGenerator
   {
      public void Initialize(IncrementalGeneratorInitializationContext context)
      {
         // Pick class decls with attributes
         var classWithAttrs = context.SyntaxProvider.CreateSyntaxProvider(
             predicate: static (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
             transform: static (ctx, _) =>
             {
                var cds = (ClassDeclarationSyntax)ctx.Node;
                var model = ctx.SemanticModel;
                var classSymbol = model.GetDeclaredSymbol(cds) as INamedTypeSymbol;
                if (classSymbol is null) return null;

                foreach (var ad in classSymbol.GetAttributes())
                {
                   var attrClass = ad.AttributeClass;
                   if (attrClass is null) continue;

                   var shortName = attrClass.Name;
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
            var distinct = list.Where(x => x is not null)
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
         sb.AppendLine("using System.Text.Json;");
         sb.AppendLine("using System.Threading;");
         sb.AppendLine("using System.Threading.Tasks;");
         sb.AppendLine("using RpcGen;");
         sb.AppendLine("");
         sb.AppendLine("#pragma warning disable CS8618");
         sb.AppendLine("#pragma warning disable CS8602");
         sb.AppendLine();

         var className = classSymbol.Name;

         // Start partial class body
         sb.AppendLine($"public abstract partial class {className}");
         sb.AppendLine("{");
         sb.AppendLine("    private IRpcTransport? _rpcTransport;");
         sb.AppendLine("    private RpcCore? _rpc = null;");
         sb.AppendLine();
         sb.AppendLine("    protected void AttachTransport(IRpcTransport transport, JsonSerializerOptions? jsonOptions = null)");
         sb.AppendLine("    {");
         sb.AppendLine("        _rpcTransport = transport;");
         sb.AppendLine("        _rpc = new RpcCore(jsonOptions);");
         sb.AppendLine("        _rpc.Handler = OnRequestAsync;");
         sb.AppendLine("    }");
         sb.AppendLine();
         sb.AppendLine("    public Task RunAsync(CancellationToken cancellationToken = default)");
         sb.AppendLine("    {");
         sb.AppendLine("        if (_rpcTransport is null || _rpc is null) throw new InvalidOperationException(\"Transport not attached. Call AttachTransport first.\");");
         sb.AppendLine("        return _rpc.RunAsync(_rpcTransport, cancellationToken);");
         sb.AppendLine("    }");
         sb.AppendLine();

         // Inbound dispatcher
         sb.AppendLine("    private async Task<bool> OnRequestAsync(long id, string iface, string method, JsonElement payload, CancellationToken ct)");
         sb.AppendLine("    {");
         sb.AppendLine($"        // Inbound dispatch for {inboundIface.Name}");
         sb.AppendLine("        switch (method)");
         sb.AppendLine("        {");

         foreach (var m in inboundMethods)
         {
            var reqType = $"{className}__{inboundIface.Name}__{m.Name}__Request";
            sb.AppendLine($"            case \"{m.Name}\":");
            sb.AppendLine("            {");
            sb.AppendLine($"                var req = ({reqType}?)JsonSerializer.Deserialize(payload.GetRawText(), typeof({reqType}), _rpc.JsonOptions);");

            var argList = string.Join(", ", m.Parameters.Select(p => $"req!.{UpperFirst(p.Name)}"));

            var retKind = GetReturnKind(m.ReturnType);
            switch (retKind)
            {
               case ReturnKind.Void:
                  sb.AppendLine($"                {m.Name}({argList});");
                  sb.AppendLine("                return true;");
                  break;

               case ReturnKind.Task:
                  sb.AppendLine($"                await {m.Name}({argList}).ConfigureAwait(false);");
                  sb.AppendLine($"                await _rpc!.SendResponseAsync(_rpcTransport!, id, null, \"{inboundIface.Name}\").ConfigureAwait(false);");
                  sb.AppendLine("                return true;");
                  break;

               case ReturnKind.TaskOfT:
                  var respType = $"{className}__{inboundIface.Name}__{m.Name}__Response";
                  sb.AppendLine($"                var result = await {m.Name}({argList}).ConfigureAwait(false);");
                  sb.AppendLine($"                await _rpc!.SendResponseAsync(_rpcTransport!, id, new {respType} {{ Result = result }}, \"{inboundIface.Name}\").ConfigureAwait(false);");
                  sb.AppendLine("                return true;");
                  break;

               default:
                  sb.AppendLine("                // Unsupported inbound return type");
                  sb.AppendLine($"                await _rpc!.SendErrorAsync(_rpcTransport!, id, \"Unsupported inbound return type\", \"{inboundIface.Name}\").ConfigureAwait(false);");
                  sb.AppendLine("                return true;");
                  break;
            }

            sb.AppendLine("            }");
         }

         sb.AppendLine("            default:");
         sb.AppendLine("                return false; // let RpcCore send default error");
         sb.AppendLine("        }");
         sb.AppendLine("    }");
         sb.AppendLine();

         // OUTBOUND implementations: implement methods of outboundIface on this class
         foreach (var m in outboundMethods)
         {
            var ifaceName = outboundIface.Name;
            var reqType = $"{className}__{ifaceName}__{m.Name}__Request";
            var respType = $"{className}__{ifaceName}__{m.Name}__Response";
            var retKind = GetReturnKind(m.ReturnType);

            bool makeAsync = retKind == ReturnKind.Task || retKind == ReturnKind.TaskOfT;

            var methodSig = BuildMethodSignature(m, isPublic: true, isAbstract: false, isProtected: false, isAsync: makeAsync);
            sb.AppendLine($"    // Outbound: {ifaceName}.{m.Name}");
            sb.AppendLine($"    {methodSig}");
            sb.AppendLine("    {");
            sb.AppendLine("        if (_rpcTransport is null || _rpc is null) throw new InvalidOperationException(\"Transport not attached. Call AttachTransport first.\");");

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

            switch (retKind)
            {
               case ReturnKind.Void:
                  sb.AppendLine($"        _ = _rpc.NotifyAsync(_rpcTransport, \"{ifaceName}\", \"{m.Name}\", payload);");
                  sb.AppendLine("    }");
                  break;

               case ReturnKind.Task:
                  sb.AppendLine($"        await _rpc.RequestAsync(_rpcTransport, \"{ifaceName}\", \"{m.Name}\", payload).ConfigureAwait(false);");
                  sb.AppendLine("    }");
                  break;

               case ReturnKind.TaskOfT:
                  sb.AppendLine($"        var resp = await _rpc.RequestAsync<{reqType}, {respType}>(_rpcTransport, \"{ifaceName}\", \"{m.Name}\", payload).ConfigureAwait(false);");
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
            if (named.ConstructedFrom is INamedTypeSymbol cf &&
                cf.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.Tasks.Task")
            {
               return named.IsGenericType ? ReturnKind.TaskOfT : ReturnKind.Task;
            }

            if (named.Name == "Task")
            {
               return named.IsGenericType ? ReturnKind.TaskOfT : ReturnKind.Task;
            }
         }

         return ReturnKind.Other;
      }

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
