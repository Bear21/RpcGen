
namespace RpcGen;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ServerToClientAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ClientToServerAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RpcInterfaceAttribute : Attribute
{
   public RpcInterfaceAttribute(Type outbound, Type inbound) { }
}