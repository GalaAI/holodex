namespace Holodex;

/// <summary>
/// The app is Holodex (operator rulings 2026-10-27 and 2026-11-05). Installs from before the rename keep their
/// state under the old name, Omniwand, which now names only one item in the Holodex inventory. Phase 1 of the rename
/// reads both names at every boundary, the current one first, and writes where the value already lives. A later
/// phase moves stored values to the current names and drops these fallbacks.
/// </summary>
internal static class HolodexNames
{
    private const string CredentialPrefix = "Holodex:";
    private const string RetiredCredentialPrefix = "Omniwand:";
    private const string EnvPrefix = "HOLODEX_";
    private const string RetiredEnvPrefix = "OMNIWAND_";

    /// <summary>The control pipe, and the id clients from before the rename still open.</summary>
    internal static readonly string[] ControlPipes = { "ArkStation.Holodex.Control", "ArkStation.Omniwand.Control" };

    /// <summary>
    /// The local state folder: %LOCALAPPDATA%\Holodex when it exists, else %LOCALAPPDATA%\Omniwand, the folder
    /// every install from before the rename already uses. Resolved once, so reads and writes land in one place.
    /// </summary>
    internal static string DataRoot { get; } = ResolveDataRoot();

    private static string ResolveDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(localAppData, "Holodex");
        return Directory.Exists(current) ? current : Path.Combine(localAppData, "Omniwand");
    }

    /// <summary>Both spellings of a Holodex: or Omniwand: credential target, current first; any other target alone.</summary>
    internal static string[] CredentialTargets(string target) => Twins(target, CredentialPrefix, RetiredCredentialPrefix);

    /// <summary>Reads a HOLODEX_ or OMNIWAND_ variable under either spelling, current first.</summary>
    internal static string? ReadEnv(string name, EnvironmentVariableTarget scope = EnvironmentVariableTarget.Process)
    {
        foreach (string candidate in Twins(name, EnvPrefix, RetiredEnvPrefix))
        {
            if (Environment.GetEnvironmentVariable(candidate, scope) is { } value) return value;
        }
        return null;
    }

    /// <summary>The tuning key under its current spelling (a holodex- key for an omniwand- one).</summary>
    internal static string CurrentTuningKey(string key) =>
        key.StartsWith("omniwand-", StringComparison.Ordinal) ? "holodex-" + key["omniwand-".Length..] : key;

    private static string[] Twins(string name, string current, string retired)
    {
        if (name.StartsWith(current, StringComparison.Ordinal)) return new[] { name, retired + name[current.Length..] };
        if (name.StartsWith(retired, StringComparison.Ordinal)) return new[] { current + name[retired.Length..], name };
        return new[] { name };
    }
}
