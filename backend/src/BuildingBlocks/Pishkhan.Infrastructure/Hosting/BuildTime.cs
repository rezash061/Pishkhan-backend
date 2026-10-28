using System.Reflection;

namespace Pishkhan.Infrastructure.Hosting;

/// <summary>True when the host is started by build tooling (OpenAPI export, dotnet-ef) rather than to serve traffic.</summary>
public static class BuildTime
{
    public static bool IsActive { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name is "GetDocument.Insider" or "ef";
}
