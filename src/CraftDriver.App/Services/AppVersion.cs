using System.Reflection;

namespace CraftDriver.App.Services;

/// <summary>Versão deste build (InformationalVersion sem o hash do commit).</summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
}
