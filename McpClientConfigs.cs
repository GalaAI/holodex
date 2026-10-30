using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Omniwand;

/// <summary>
/// An MCP server the user already set up in another client. Only what is needed to show it and to start it the way
/// that client would: the name, the transport, the command and its arguments or the URL, and whether some sign-in or
/// key is configured. Header values and env values are never read. <see cref="Args"/> and <see cref="Url"/> stay in
/// memory for launching; anything shown or sent uses <see cref="Display"/>, which masks what looks like a secret.
/// </summary>
internal sealed record McpConfiguredServer(
    string Client,
    string Name,
    string Transport,
    string Command,
    IReadOnlyList<string> Args,
    string Url,
    bool AuthConfigured,
    string ConfigPath)
{
    public bool IsStdio => Transport == "stdio";

    /// <summary>What makes two clients' entries the same server: the exact command line, or the URL without its query.</summary>
    public string Key => IsStdio ? "stdio:" + McpClientConfigs.CommandLine(Command, Args) : "url:" + McpClientConfigs.MaskUrl(Url);

    /// <summary>The command or URL with anything secret-shaped masked, for the panel and the pipe.</summary>
    public string Display => IsStdio ? McpClientConfigs.MaskCommand(Command, Args) : McpClientConfigs.MaskUrl(Url);
}

/// <summary>One server as found across clients (the same Stream Deck bridge in Claude Desktop and in a project, say).</summary>
internal sealed record McpServerFinding(McpConfiguredServer Server, IReadOnlyList<string> Clients)
{
    public string Name => Server.Name;
}

/// <summary>Where each client keeps its MCP servers on Windows.</summary>
internal sealed record McpConfigLocations(string Home, string AppData, string LocalAppData)
{
    public static McpConfigLocations ThisUser => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

/// <summary>
/// Reads the MCP servers the user configured in Claude Desktop, Claude Code (user, local and project .mcp.json),
/// Cursor, VS Code, Codex and Gemini CLI. Read-only, and it never keeps a secret: header and env values are skipped,
/// and only whether one is configured is recorded.
/// </summary>
internal static class McpClientConfigs
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private const int MaxProjects = 60;

    /// <summary>Every configured server, grouped so a server set up in several clients is one finding.</summary>
    public static IReadOnlyList<McpServerFinding> Discover(McpConfigLocations? where = null)
    {
        var all = ReadAll(where ?? McpConfigLocations.ThisUser);
        // One finding per name and transport: the same server pinned to two versions in two clients is still one row.
        return all.GroupBy(s => s.Name + "|" + s.Transport, StringComparer.OrdinalIgnoreCase)
            .Select(g => new McpServerFinding(g.First(), g.Select(s => s.Client).Distinct().ToList()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<McpConfiguredServer> ReadAll(McpConfigLocations where)
    {
        var found = new List<McpConfiguredServer>();
        void Json(string client, string path, string container)
        {
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
                var root = doc.RootElement;
                if (container == "settings-mcp")
                {
                    if (root.TryGetProperty("mcp", out var mcp) && mcp.TryGetProperty("servers", out var servers)) AddServers(found, client, path, servers);
                }
                else if (root.TryGetProperty(container, out var servers)) AddServers(found, client, path, servers);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }

        Json("Claude Desktop", Path.Combine(where.AppData, "Claude", "claude_desktop_config.json"), "mcpServers");
        // The Store build of Claude Desktop keeps its config inside its package folder.
        foreach (string dir in SafeDirs(Path.Combine(where.LocalAppData, "Packages"), "Claude_*"))
            Json("Claude Desktop", Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"), "mcpServers");
        ReadClaudeCode(found, where.Home);
        Json("Cursor", Path.Combine(where.Home, ".cursor", "mcp.json"), "mcpServers");
        foreach (var (client, folder) in new[] { ("VS Code", "Code"), ("VS Code Insiders", "Code - Insiders") })
        {
            Json(client, Path.Combine(where.AppData, folder, "User", "mcp.json"), "servers");
            Json(client, Path.Combine(where.AppData, folder, "User", "settings.json"), "settings-mcp");
        }
        ReadCodex(found, Path.Combine(where.Home, ".codex", "config.toml"));
        Json("Gemini CLI", Path.Combine(where.Home, ".gemini", "settings.json"), "mcpServers");
        return found;
    }

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Claude Code: user servers and per-project (local) servers in ~/.claude.json, and each known project's .mcp.json.</summary>
    private static void ReadClaudeCode(List<McpConfiguredServer> found, string home)
    {
        string path = Path.Combine(home, ".claude.json");
        if (!File.Exists(path)) return;
        var projects = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            var root = doc.RootElement;
            if (root.TryGetProperty("mcpServers", out var user)) AddServers(found, "Claude Code", path, user);
            if (root.TryGetProperty("projects", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var project in map.EnumerateObject())
                {
                    projects.Add(project.Name);
                    if (project.Value.ValueKind == JsonValueKind.Object && project.Value.TryGetProperty("mcpServers", out var local))
                        AddServers(found, "Claude Code", path, local);
                }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }

        foreach (string project in projects.Take(MaxProjects))
        {
            string mcp;
            try { mcp = Path.Combine(project, ".mcp.json"); } catch (ArgumentException) { continue; }
            if (!File.Exists(mcp)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mcp), Lenient);
                if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
                    AddServers(found, $"Claude Code ({Path.GetFileName(project.TrimEnd('\\', '/'))})", mcp, servers);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
    }

    private static void AddServers(List<McpConfiguredServer> found, string client, string path, JsonElement servers)
    {
        if (servers.ValueKind != JsonValueKind.Object) return;
        foreach (var server in servers.EnumerateObject())
            if (server.Value.ValueKind == JsonValueKind.Object && FromJson(client, server.Name, server.Value, path) is { } s) found.Add(s);
    }

    /// <summary>One server entry in the shape Claude, Cursor, VS Code and Gemini share (command, args, url, type, headers, env).</summary>
    public static McpConfiguredServer? FromJson(string client, string name, JsonElement e, string path)
    {
        string Str(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string command = Str("command");
        var args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        string url = WithoutTemplate(new[] { Str("url"), Str("httpUrl"), Str("serverUrl") }.FirstOrDefault(u => u.Length > 0) ?? "");
        string type = Str("type").ToLowerInvariant();
        if (command.Length == 0 && url.Length == 0) return null;
        string transport = command.Length > 0 ? "stdio" : type == "sse" ? "sse" : "http";
        bool auth = HasKeys(e, "headers") || HasSecretNamedKeys(e, "env") || e.TryGetProperty("oauth", out _) || e.TryGetProperty("auth", out _);
        return new McpConfiguredServer(client, name, transport, command, args, url, auth, path);
    }

    /// <summary>
    /// A <c>${VAR:-default}</c> URL (Claude Code expands these) becomes its written default; the variable's value is
    /// never read. A template with no default is left as is and will not connect.
    /// </summary>
    public static string WithoutTemplate(string url)
    {
        var m = Regex.Match(url, @"^\$\{[A-Za-z_][A-Za-z0-9_]*:-(.+)\}$");
        return m.Success ? m.Groups[1].Value : url;
    }

    private static bool HasKeys(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object && v.EnumerateObject().Any();

    private static readonly Regex SecretName = new("KEY|TOKEN|SECRET|PASSWORD|PASSWD|AUTH|CREDENTIAL|COOKIE|SESSION", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when an env block names a key, token or secret. Only the names are looked at, never the values.</summary>
    private static bool HasSecretNamedKeys(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object && v.EnumerateObject().Any(p => SecretName.IsMatch(p.Name));

    /// <summary>Codex: <c>[mcp_servers.&lt;name&gt;]</c> tables with command, args and url, read with Tomlyn.</summary>
    private static void ReadCodex(List<McpConfiguredServer> found, string path)
    {
        if (!File.Exists(path)) return;
        try { found.AddRange(FromCodexToml(File.ReadAllText(path), path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static IEnumerable<McpConfiguredServer> FromCodexToml(string toml, string path)
    {
        TomlTable doc;
        try { doc = TomlSerializer.Deserialize<TomlTable>(toml) ?? new TomlTable(); }
        catch (Exception) { yield break; }
        if (!doc.TryGetValue("mcp_servers", out var servers) || servers is not TomlTable table) yield break;
        foreach (var (name, value) in table)
        {
            if (value is not TomlTable s) continue;
            string Str(string key) => s.TryGetValue(key, out var v) && v is string text ? text : "";
            var args = s.TryGetValue("args", out var a) && a is TomlArray arr ? arr.OfType<string>().ToList() : new List<string>();
            string command = Str("command"), url = Str("url");
            if (command.Length == 0 && url.Length == 0) continue;
            bool auth = s.ContainsKey("bearer_token_env_var") || s.ContainsKey("http_headers") || s.ContainsKey("env_http_headers") ||
                        (s.TryGetValue("env", out var env) && env is TomlTable envTable && envTable.Keys.Any(k => SecretName.IsMatch(k)));
            yield return new McpConfiguredServer("Codex", name, command.Length > 0 ? "stdio" : "http", command, args, url, auth, path);
        }
    }

    // ---- display ----

    /// <summary>The command line exactly as declared, quoting arguments with spaces. Shown only in the approval.</summary>
    public static string CommandLine(string command, IReadOnlyList<string> args) =>
        string.Join(' ', new[] { command }.Concat(args).Select(Quote));

    private static string Quote(string s) => s.Length == 0 || s.Contains(' ') ? $"\"{s}\"" : s;

    private static readonly Regex SecretValue = new(@"^(sk-|ghp_|github_pat_|gho_|xox[abp]-|AIza|hf_|pplx-|tvly-|re_|r8_|gsk_|eyJ)|^[A-Za-z0-9_\-]{32,}$", RegexOptions.Compiled);

    /// <summary>The command line with flag values named like secrets, secret-shaped values and URL queries masked.</summary>
    public static string MaskCommand(string command, IReadOnlyList<string> args)
    {
        var shown = new List<string> { Quote(command) };
        bool maskNext = false;
        foreach (string arg in args)
        {
            string a = arg;
            if (maskNext) { a = "***"; maskNext = false; }
            else if (Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) a = MaskUrl(a);
            else if (a.StartsWith('-') && a.Contains('=') && SecretName.IsMatch(a[..a.IndexOf('=')])) a = a[..(a.IndexOf('=') + 1)] + "***";
            else if (a.StartsWith('-') && SecretName.IsMatch(a)) maskNext = true;
            else if (a.Contains('=') && SecretName.IsMatch(a[..a.IndexOf('=')])) a = a[..(a.IndexOf('=') + 1)] + "***";
            else if (SecretValue.IsMatch(a)) a = "***";
            shown.Add(Quote(a));
        }
        return string.Join(' ', shown);
    }

    /// <summary>A URL without user info or query, which is where links carry tokens.</summary>
    public static string MaskUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath}" : url.Split('?')[0];
}
