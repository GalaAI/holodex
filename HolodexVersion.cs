using System.Reflection;

namespace Omniwand;

/// <summary>
/// The version Holodex shows (tray, drawer, toasts, MCP client info), read from the assembly so it can never drift
/// from the build: the csproj Version is the one place it is set.
/// </summary>
internal static class HolodexVersion
{
    /// <summary>"0.4.2": the informational version without the "+commit" build suffix.</summary>
    public static string Number { get; } = Read();

    /// <summary>"v0.4.2".</summary>
    public static string Display => "v" + Number;

    private static string Read()
    {
        var asm = typeof(HolodexVersion).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var v = asm.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }
}
