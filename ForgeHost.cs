using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace Omniwand;

/// <summary>What the 4GE server is doing, as Holodex sees it.</summary>
internal enum ForgeState { Stopped, Starting, Running, Failed }

/// <summary>One Library item from GET /api/library (@4ge/contracts LibraryItem). <c>Open</c> is the session to open, as sent.</summary>
internal sealed record ForgeLibraryItem(string Id, string Origin, string Title, string? Subtitle, string? Status, string Tone, string? Url, JsonElement Open);

/// <summary>One origin group (@4ge/contracts LibraryGroup).</summary>
internal sealed record ForgeLibraryGroup(string Origin, string Label, bool CanCreate, IReadOnlyList<ForgeLibraryItem> Items);

/// <summary>
/// 4GE in Holodex: the local 4GE server (forge/apps/server, Node, 127.0.0.1:17400) that Holodex starts with the user's
/// own keys in its environment, and the reads Holodex makes from it (the Library, a Smith session for an item). The
/// keys go to the child process only: never to a file, a log or the command line.
/// </summary>
internal sealed class ForgeHost : IDisposable
{
    /// <summary>@4ge/contracts PORTS.server. Never an Ark port (5300, 8788, 4200, 15300, 18788, 14200).</summary>
    public const int DefaultPort = 17400;

    /// <summary>@4ge/contracts LIBRARY_ORIGINS, in the Library's order: My projects, Repos, Mods, Augments, Adapters.</summary>
    public static readonly string[] LibraryOrigins = { "project", "repo", "mod", "augment", "adapter" };

    /// <summary>@4ge/contracts LIBRARY_GROUPS labels, used only when the server leaves a group out.</summary>
    public static readonly IReadOnlyDictionary<string, string> LibraryLabels = new Dictionary<string, string>
    {
        ["project"] = "My projects", ["repo"] = "Repos", ["mod"] = "Mods", ["augment"] = "Augments", ["adapter"] = "Adapters"
    };

    private static readonly int[] ArkPorts = { 5300, 8788, 4200, 15300, 18788, 14200 };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ArkStationClient _ark;
    private readonly IReadOnlyDictionary<string, string> _extraEnv;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _log;
    private string[] _secrets = Array.Empty<string>();

    public ForgeHost(ArkStationClient ark, int port = DefaultPort, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (ArkPorts.Contains(port)) throw new ArgumentException($"Port {port} belongs to Ark; 4GE never binds it.", nameof(port));
        _ark = ark;
        Port = port;
        _extraEnv = extraEnv ?? new Dictionary<string, string>();
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public ForgeState State { get; private set; } = ForgeState.Stopped;

    /// <summary>Why the server is not running, in plain words (null while it runs).</summary>
    public string? LastError { get; private set; }

    /// <summary>True when Holodex started this server (and so stops it on exit); false for one already running.</summary>
    public bool StartedHere => _process is { HasExited: false };

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// The forge folder: HOLODEX_4GE_ROOT, else the nearest forge\ beside or above Holodex's own folder that holds the
    /// built server (forge\apps\server\dist\main.js). Null when 4GE is not on this PC.
    /// </summary>
    public static string? FindForgeRoot(string? start = null)
    {
        string? configured = Environment.GetEnvironmentVariable("HOLODEX_4GE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "forge");
            if (File.Exists(ServerEntry(candidate))) return candidate;
        }
        return null;
    }

    public static string ServerEntry(string forgeRoot) => Path.Combine(forgeRoot, "apps", "server", "dist", "main.js");

    /// <summary>node.exe: HOLODEX_NODE, else the first on PATH, else the default Node install. Null when there is none.</summary>
    public static string? FindNode()
    {
        string? configured = Environment.GetEnvironmentVariable("HOLODEX_NODE");
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    /// <summary>The 4GE data folder (@4ge/contracts dataRoot): FORGE_DATA_ROOT, else %LOCALAPPDATA%\Holodex\4ge.</summary>
    public static string DataRoot(IReadOnlyDictionary<string, string>? env = null)
    {
        string? configured = env != null && env.TryGetValue("FORGE_DATA_ROOT", out var e) ? e : Environment.GetEnvironmentVariable("FORGE_DATA_ROOT");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Holodex", "4ge");
    }

    /// <summary>
    /// The variables the server reads (SPEC "Models"): NEBIUS_API_KEY, HIVEMIND_URL and HIVEMIND_TOKEN, and FORGE_PORT
    /// when it is not the default. Empty values are left out, so the server says which model route is missing.
    /// </summary>
    internal static Dictionary<string, string> ServerEnvironment(int port, string? nebiusKey, string? hivemindUrl, string? hivemindToken)
    {
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(nebiusKey)) env["NEBIUS_API_KEY"] = nebiusKey.Trim();
        if (!string.IsNullOrWhiteSpace(hivemindToken) && !string.IsNullOrWhiteSpace(hivemindUrl))
        {
            env["HIVEMIND_URL"] = hivemindUrl.Trim();
            env["HIVEMIND_TOKEN"] = hivemindToken.Trim();
        }
        if (port != DefaultPort) env["FORGE_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }

    /// <summary>The Hivemind sign-in's access token, refreshed first when a sign-in is saved; null when signed out.</summary>
    private async Task<string?> ReadHivemindTokenAsync()
    {
        if (!HivemindClient.HasSavedSignIn) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _ark.Hivemind.EnsureConnectedAsync(cts.Token);
        }
        catch (Exception) { }
        try
        {
            string? json = ArkStationClient.TryReadWindowsCredential(HivemindClient.CredentialTarget);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TokenContainer>(json)?.AccessToken;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether a 4GE server answers on <see cref="Port"/> (GET /api/health says name "4ge"), with why not.</summary>
    public async Task<(bool Ok, string? Error)> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await _http.GetAsync($"{BaseUrl}/api/health", cts.Token);
            string body = await res.Content.ReadAsStringAsync(cts.Token);
            if (!res.IsSuccessStatusCode) return (false, $"Port {Port} answered HTTP {(int)res.StatusCode}, and it is not the 4GE server.");
            using var doc = JsonDocument.Parse(body);
            bool forge = doc.RootElement.TryGetProperty("name", out var name) && name.GetString() == "4ge";
            return forge ? (true, null) : (false, $"Port {Port} is taken by another program, not the 4GE server.");
        }
        catch (JsonException) { return (false, $"Port {Port} is taken by another program, not the 4GE server."); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (false, null); }
    }

    /// <summary>
    /// Starts the server if it is not running (or uses the one already on the port) and waits up to 30 s for it to
    /// answer. Returns true when it runs; otherwise <see cref="LastError"/> says why in plain words.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            var (ok, portError) = await ProbeAsync(ct);
            if (ok) { SetState(ForgeState.Running, null); return true; }
            if (portError != null) { SetState(ForgeState.Failed, portError); return false; }

            string? root = FindForgeRoot();
            if (root == null) { SetState(ForgeState.Failed, "4GE is not on this PC: no forge folder with a built server (forge\\apps\\server\\dist\\main.js) was found next to Holodex. Set HOLODEX_4GE_ROOT to the forge folder, or run pnpm -C forge build."); return false; }
            string? node = FindNode();
            if (node == null) { SetState(ForgeState.Failed, "Node.js 24 or newer is needed to run 4GE, and node.exe was not found. Install Node.js, or set HOLODEX_NODE to node.exe."); return false; }

            SetState(ForgeState.Starting, null);
            string? hivemindToken = await ReadHivemindTokenAsync();
            var env = ServerEnvironment(Port, _ark.ReadNebiusApiKey(), HivemindClient.Endpoint.AbsoluteUri, hivemindToken);
            foreach (var (k, v) in _extraEnv) env[k] = v;
            _secrets = new[] { env.GetValueOrDefault("NEBIUS_API_KEY"), env.GetValueOrDefault("HIVEMIND_TOKEN") }
                .Where(s => !string.IsNullOrEmpty(s) && s.Length >= 8).Cast<string>().ToArray();

            var psi = new ProcessStartInfo(node)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(ServerEntry(root));
            foreach (var (k, v) in env) psi.Environment[k] = v;
            OpenLog(env);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            process.Exited += (_, _) =>
            {
                WriteLog($"[holodex] 4GE server exited with code {SafeExitCode(process)}");
                if (State == ForgeState.Running) SetState(ForgeState.Stopped, "The 4GE server stopped.");
            };
            process.Start();
            // In a kill-on-close job, so the server never outlives Holodex, even when Holodex is ended by force.
            KillOnCloseJob.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    SetState(ForgeState.Failed, $"The 4GE server stopped as it started (exit code {SafeExitCode(process)}). {LastLogLine() ?? ""}".Trim());
                    return false;
                }
                (ok, portError) = await ProbeAsync(ct);
                if (ok) { SetState(ForgeState.Running, null); return true; }
                if (portError != null) { SetState(ForgeState.Failed, portError); return false; }
                await Task.Delay(400, ct);
            }
            SetState(ForgeState.Failed, $"The 4GE server did not answer on port {Port} within 30 seconds. Its log is {LogPath(env)}.");
            return false;
        }
        finally { _startGate.Release(); }
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    private void SetState(ForgeState state, string? error)
    {
        bool changed = State != state || LastError != error;
        State = state;
        LastError = error;
        if (changed) StateChanged?.Invoke();
    }

    // The server's own output, in the 4GE data folder's logs. It never prints the keys; any line that carries one
    // anyway has it masked before it is written.
    private static string LogPath(IReadOnlyDictionary<string, string> env) => Path.Combine(DataRoot(env), "logs", "holodex-server.log");
    private readonly List<string> _tail = new();

    private void OpenLog(IReadOnlyDictionary<string, string> env)
    {
        try
        {
            _log?.Dispose();
            string path = LogPath(env);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            _log.WriteLine($"[holodex] {DateTime.Now:O} starting the 4GE server on port {Port}");
        }
        catch (IOException) { _log = null; }
    }

    internal static string Mask(string line, IEnumerable<string> secrets)
    {
        foreach (string s in secrets) line = line.Replace(s, "[redacted]", StringComparison.Ordinal);
        return line;
    }

    private void WriteLog(string? line)
    {
        if (line == null) return;
        line = Mask(line, _secrets);
        lock (_tail)
        {
            _tail.Add(line);
            if (_tail.Count > 20) _tail.RemoveAt(0);
            try { _log?.WriteLine(line); } catch (Exception) { }
        }
    }

    private string? LastLogLine()
    {
        // Node ends a crash with its stack and then "Node.js v24..."; the line that says what went wrong names an Error.
        lock (_tail)
        {
            var lines = _tail.Where(l => !l.StartsWith("[holodex]", StringComparison.Ordinal) && l.Trim().Length > 0 && !l.TrimStart().StartsWith("at ", StringComparison.Ordinal)).ToList();
            return lines.LastOrDefault(l => l.Contains("Error", StringComparison.Ordinal) || l.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();
        }
    }

    /// <summary>The Library, grouped by origin, from GET /api/library; or why it could not be read.</summary>
    public async Task<(IReadOnlyList<ForgeLibraryGroup>? Groups, string? Error)> ReadLibraryAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync($"{BaseUrl}/api/library", ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, ApiError(body, res));
            return (ParseLibrary(body), null);
        }
        catch (JsonException ex) { return (null, $"The 4GE Library answered in a shape Holodex does not read: {ex.Message}"); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, $"The 4GE server did not answer: {ex.Message}"); }
    }

    /// <summary>
    /// Reads a LibraryResponse into the five origin groups, always in the Library's order and always all five (a group
    /// the server left out is listed empty).
    /// </summary>
    internal static IReadOnlyList<ForgeLibraryGroup> ParseLibrary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var byOrigin = new Dictionary<string, ForgeLibraryGroup>();
        if (doc.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groups.EnumerateArray())
            {
                string origin = Str(g, "origin") ?? "";
                if (!LibraryOrigins.Contains(origin)) continue;
                var items = new List<ForgeLibraryItem>();
                if (g.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var it in arr.EnumerateArray())
                    {
                        if (!it.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.Object) continue;
                        items.Add(new ForgeLibraryItem(Str(it, "id") ?? "", Str(it, "origin") ?? origin, Str(it, "title") ?? "Untitled",
                            Str(it, "subtitle"), Str(it, "status"), Str(it, "tone") ?? "rest", Str(it, "url"), open.Clone()));
                    }
                bool canCreate = g.TryGetProperty("canCreate", out var cc) && cc.ValueKind == JsonValueKind.True;
                byOrigin[origin] = new ForgeLibraryGroup(origin, Str(g, "label") ?? LibraryLabels[origin], canCreate, items);
            }
        }
        return LibraryOrigins.Select(o => byOrigin.TryGetValue(o, out var g) ? g : new ForgeLibraryGroup(o, LibraryLabels[o], o == "mod", Array.Empty<ForgeLibraryItem>())).ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The server's error envelope ({error:{code,message,hint}}) in plain words.</summary>
    internal static string ApiError(string body, HttpResponseMessage res)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                string message = Str(err, "message") ?? $"HTTP {(int)res.StatusCode}";
                string? hint = Str(err, "hint");
                return hint == null ? message : $"{message} {hint}";
            }
        }
        catch (JsonException) { }
        return $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}";
    }

    /// <summary>
    /// Opens the item's Smith session (POST /api/sessions with the item's own <c>open</c>) and returns the studio path
    /// that shows it. A build's session carries its build id.
    /// </summary>
    public async Task<(string? Path, string? Error)> OpenSessionAsync(ForgeLibraryItem item, CancellationToken ct = default)
    {
        string body = CreateSessionBody(item.Open);
        try
        {
            using var res = await _http.PostAsync($"{BaseUrl}/api/sessions", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, ApiError(text, res));
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("session", out var session)
                ? (StudioPathFor(session), null)
                : (null, "The 4GE server opened no session.");
        }
        catch (JsonException ex) { return (null, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, $"The 4GE server did not answer: {ex.Message}"); }
    }

    /// <summary>CreateSessionRequest from an item's <c>open</c>: agent, kind, task, subject, plus buildId for a build.</summary>
    internal static string CreateSessionBody(JsonElement open)
    {
        var body = new Dictionary<string, object?>();
        foreach (var p in open.EnumerateObject())
            if (p.Name is "agent" or "kind" or "task" or "subject") body[p.Name] = p.Value.Clone();
        if (open.TryGetProperty("subject", out var subject) && Str(subject, "id") is { } id &&
            Str(subject, "type") is "build" or "project" && id.StartsWith("bld_", StringComparison.Ordinal))
            body["buildId"] = id;
        return JsonSerializer.Serialize(body);
    }

    /// <summary>The studio path for a session (@4ge/contracts studioPathForSession; keep the two in step).</summary>
    internal static string StudioPathFor(JsonElement session)
    {
        string id = Str(session, "id") ?? "";
        string q = "?session=" + Uri.EscapeDataString(id);
        if (Str(session, "kind") == "imagistation") return "/imagistation/" + Uri.EscapeDataString(id);
        if (session.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.Object &&
            Str(subject, "type") == "mod" && Str(subject, "id") is { } modId)
            return "/mods/" + Uri.EscapeDataString(modId) + q;
        if (Str(session, "buildId") is { Length: > 0 } buildId) return "/ide/" + Uri.EscapeDataString(buildId) + q;
        return "/library" + q;
    }

    /// <summary>Stops the server Holodex started (a server that was already running is left alone).</summary>
    public void Dispose()
    {
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception) { }
        _process?.Dispose();
        _process = null;
        lock (_tail) { _log?.Dispose(); _log = null; }
        _http.Dispose();
    }

    /// <summary>
    /// One Win32 job for every server Holodex starts, with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: when Holodex's process
    /// ends (exit, crash or Task Manager), Windows closes the job's last handle and ends the server with it.
    /// No .NET API does this; it is the documented CreateJobObject / SetInformationJobObject / AssignProcessToJobObject.
    /// </summary>
    private static class KillOnCloseJob
    {
        private static readonly Lazy<IntPtr> Job = new(Create);

        public static void Assign(Process process)
        {
            try { if (Job.Value != IntPtr.Zero) AssignProcessToJobObject(Job.Value, process.Handle); }
            catch (Exception) { }
        }

        private static IntPtr Create()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                return SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size) ? job : IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
