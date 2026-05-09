using DotnetTun.Abstractions;

namespace DotnetTun.Core;

public static class TransparentProxy
{
    public static TransparentProxyBuilder CreateBuilder() => new();
}
