using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Omniwand;

public enum HivemindState
{
    /// <summary>No saved sign-in; nothing has been tried.</summary>
    NotSignedIn,
    /// <summary>A saved sign-in that has not been tried yet (startup, before the first call).</summary>
    Connecting,
    /// <summary>The browser sign-in is open and Omniwand is waiting for its redirect.</summary>
    SigningIn,
    Connected,
    /// <summary>Signed in before, but the endpoint or the saved sign-in did not work just now (see LastError).</summary>
    Unreachable
}

/// <summary>An agent from Hivemind's live roster (<c>ark_agents</c>, the Runtime Gateway's /v1/agents).</summary>
public sealed record HivemindAgent(string Id, string Name, string Role, string Provider, bool DispatchReady);

/// <summary>A mission receipt from <c>dispatch_agent_mission</c> or <c>agent_mission_status</c>.</summary>
public sealed record HivemindMission(string ExecutionId, string AgentId, string Status, string Model, string Result, string Error)
{
    public bool Finished => Status is "completed" or "failed" or "cancelled" or "timed_out";

    /// <summary>
    /// The result as a reply: agents answer a mission with a short receipt in sections (Objective, Inputs,
    /// Constraints, Validation). Seen live in four shapes: "## Name" heading lines, bare section-name lines, and
    /// inline "Name: text; Name: text" or "Name: text | Name: text". The answer is the Objective (or Answer, Result, Summary) section, the verdict
    /// is the Validation section's first line, and Constraints is kept as a one-line note; Inputs echoes the mission
    /// and is dropped. A result without sections is the answer.
    /// </summary>
    public HivemindReply Reply()
    {
        // Inline "Name: text" segments (at a line start or after ; | · or •) become heading lines, so one parse reads all.
        string text = System.Text.RegularExpressions.Regex.Replace(Result.Replace("\r", ""),
            @"(^|[;|·•])[ \t]*(?:#{1,6}[ \t]*)?(?<name>objective|answer|result|summary|inputs|constraints|validation|verdict)[ \t]*:[ \t]*",
            "\n## ${name}\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        var sections = new List<(string Name, List<string> Lines)>();
        foreach (string raw in text.Split('\n'))
        {
            var head = System.Text.RegularExpressions.Regex.Match(raw.Trim(),
                @"^(?:#{1,6}\s*(?<name>[A-Za-z][A-Za-z ]{0,30})|(?<name>objective|answer|result|summary|inputs|constraints|validation|verdict))\s*:?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (head.Success) { sections.Add((head.Groups["name"].Value.Trim().ToLowerInvariant(), new List<string>())); continue; }
            if (sections.Count == 0) sections.Add(("", new List<string>()));
            sections[^1].Lines.Add(raw);
        }
        string Text(params string[] names) => string.Join("\n", sections.Where(s => names.Contains(s.Name)).SelectMany(s => s.Lines)).Trim();
        string First(params string[] names) => Text(names).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        if (sections.All(s => s.Name.Length == 0)) return new HivemindReply(Result.Trim(), "", "");
        string answer = FirstNonEmpty(Text("objective", "answer", "result", "summary", "response"), Text(""));
        return new HivemindReply(answer, First("validation", "verdict"), First("constraints"));
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}

/// <summary>A mission result read as a reply: the answer, the validation verdict, and the constraints note.</summary>
public sealed record HivemindReply(string Answer, string Validation, string Constraints);

/// <summary>A saved Hivemind Session as <c>list_sessions</c> reports it: the room the web app and Holodex both show.</summary>
public sealed record HivemindSessionSummary(string Id, string Title, string Status, IReadOnlyList<string> Participants, DateTimeOffset UpdatedAt, string Error);

/// <summary>
/// One transcript entry from <c>read_session</c>: the owner (<c>user</c>), an agent turn (<c>agent</c>, with its model),
/// or a connected client's post (<c>client</c>, Holodex's own included) under that client's name.
/// </summary>
public sealed record HivemindSessionMessage(string Id, string Role, string AgentId, string Model, string ClientName, string Content, DateTimeOffset CreatedAt, string ExecutionId = "");

/// <summary>An <c>ask_agent</c> receipt: the Session asked in, the owner message it holds, and the room's status then.</summary>
public sealed record HivemindAsk(string SessionId, string MessageId, string Status);

/// <summary>A Session with its transcript, oldest message first.</summary>
public sealed record HivemindSession(HivemindSessionSummary Summary, IReadOnlyList<HivemindSessionMessage> Messages);

/// <summary>
/// Omniwand's connection to the real Hivemind: the remote MCP server at hivemind.arkomnitek.com/mcp, through the
/// official MCP C# SDK (ModelContextProtocol.Core) and its OAuth 2.1 client. Sign-in is the server's public-client
/// flow: dynamic registration, PKCE S256, and a browser sign-in that redirects to a fixed loopback port
/// (Hivemind matches redirect URIs exactly, so the port never changes). Tokens and the registered client id live in
/// the Windows Credential Manager slot <c>Omniwand:HivemindToken</c>; the SDK refreshes them, so the browser opens
/// only on the sign-in the operator starts (<see cref="SignInAsync"/>), never from a background call.
/// </summary>
public sealed class HivemindClient : IAsyncDisposable
{
    /// <summary>The MCP endpoint under the Hivemind connector slot's address (OMNIWAND_HIVEMIND_URL overrides it).</summary>
    public static readonly Uri Endpoint = new(RealmConnectors.HivemindDefault.ResolvedBaseUrl + "/mcp");
    public static readonly Uri RedirectUri = new("http://127.0.0.1:47613/hivemind/callback/");
    public const string CredentialTarget = "Omniwand:HivemindToken";

    /// <summary>The Hivemind web app's Sessions view (it addresses views, not single rooms).</summary>
    public static readonly Uri WebSessionsView = new(RealmConnectors.HivemindDefault.ResolvedBaseUrl + "/?view=sessions");

    /// <summary>"Open in Hivemind": hands over to the web app's Sessions, where every Holodex Session lives too.</summary>
    public static void OpenWebSessions()
    {
        try { Process.Start(new ProcessStartInfo(WebSessionsView.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show("Hivemind did not open", ex.Message, "HOLODEX // HIVEMIND", 3600, ToastTone.Warning); }
    }

    /// <summary>
    /// Read the roster, mission status and saved Sessions (runtime.read); dispatch missions and post into Sessions
    /// (runtime.execute). Nothing else is requested.
    /// </summary>
    private static readonly string[] RequestedScopes = { "runtime.read", "runtime.execute" };
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public HivemindState State { get; private set; } = HasSavedSignIn ? HivemindState.Connecting : HivemindState.NotSignedIn;
    public string LastError { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public static bool HasSavedSignIn => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(CredentialTarget));

    /// <summary>
    /// Opens the browser sign-in (Hivemind's /login, then Authorize) and waits for its redirect. Started only by
    /// the operator: the Holodex "Sign in to Hivemind" button.
    /// </summary>
    public Task<bool> SignInAsync(CancellationToken ct = default) => ConnectAsync(interactive: true, ct);

    /// <summary>
    /// Signs Omniwand out of Hivemind: drops the connection and deletes the saved tokens and client id from
    /// <c>Omniwand:HivemindToken</c>. The next Sign in registers again and opens the browser.
    /// </summary>
    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DropClientAsync();
            ArkStationClient.TryDeleteWindowsCredential(CredentialTarget);
            SetState(HivemindState.NotSignedIn, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects with the saved sign-in only; false (and no browser) when there is none or it no longer works.</summary>
    public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => ConnectAsync(interactive: false, ct);

    public async Task<IReadOnlyList<HivemindAgent>?> ListAgentsAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("ark_agents", new Dictionary<string, object?>(), ct);
        if (data is not JsonElement root) return null;
        var agents = FindArray(root, "agents");
        if (agents is not JsonElement list) return Array.Empty<HivemindAgent>();
        return list.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object && Str(a, "id").Length > 0)
            .Select(a => new HivemindAgent(
                Str(a, "id"),
                FirstNonEmpty(Str(a, "displayName"), Str(a, "name"), Str(a, "id")),
                Str(a, "role"),
                Str(a, "provider"),
                !a.TryGetProperty("dispatchReady", out var ready) || ready.ValueKind != JsonValueKind.False))
            .ToList();
    }

    /// <summary>Dispatches a mission to <paramref name="agentId"/> (Hivemind's default execution mode, discussion).</summary>
    public async Task<HivemindMission?> DispatchAsync(string agentId, string objective, CancellationToken ct = default)
    {
        var data = await CallAsync("dispatch_agent_mission", new Dictionary<string, object?>
        {
            ["objective"] = objective,
            ["targetAgentId"] = agentId,
            ["submissionId"] = Guid.NewGuid().ToString()
        }, ct);
        return data is JsonElement root ? ReadMission(root, agentId) : null;
    }

    public async Task<HivemindMission?> MissionStatusAsync(string executionId, CancellationToken ct = default)
    {
        var data = await CallAsync("agent_mission_status", new Dictionary<string, object?> { ["executionId"] = executionId }, ct);
        return data is JsonElement root ? ReadMission(root, string.Empty) : null;
    }

    /// <summary>The owner's saved Sessions, newest first (<c>list_sessions</c>, runtime.read).</summary>
    public async Task<IReadOnlyList<HivemindSessionSummary>?> ListSessionsAsync(CancellationToken ct = default)
    {
        var data = await CallAsync("list_sessions", new Dictionary<string, object?>(), ct);
        if (data is not JsonElement root) return null;
        if (FindArray(root, "sessions") is not JsonElement list) return Array.Empty<HivemindSessionSummary>();
        return list.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.Object && Str(s, "id").Length > 0)
            .Select(ReadSessionSummary)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// <summary>One Session and its transcript (<c>read_session</c>, runtime.read).</summary>
    public async Task<HivemindSession?> ReadSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var data = await CallAsync("read_session", new Dictionary<string, object?> { ["sessionId"] = sessionId }, ct);
        if (data is not JsonElement root) return null;
        var session = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("session", out var s) ? s : root;
        var messages = new List<HivemindSessionMessage>();
        if (session.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string model = m.TryGetProperty("execution", out var ex) ? Str(ex, "model") : "";
                string client = m.TryGetProperty("sourceClient", out var sc) ? Str(sc, "name") : "";
                messages.Add(new HivemindSessionMessage(Str(m, "id"), Str(m, "role"), Str(m, "agentId"), model, client, Str(m, "content"), Date(m, "createdAt"), Str(m, "executionId")));
            }
        }
        return new HivemindSession(ReadSessionSummary(session), messages.OrderBy(m => m.CreatedAt).ToList());
    }

    /// <summary>
    /// Posts <paramref name="content"/> into a Session as Holodex (<c>post_session_message</c>, runtime.execute). Hivemind
    /// attributes it to this connection's client, not to an agent, and it starts no agent. The submission id makes a
    /// retry safe: Hivemind keeps one message per id.
    /// </summary>
    public async Task<bool> PostSessionMessageAsync(string sessionId, string content, Guid submissionId, CancellationToken ct = default)
    {
        var data = await CallAsync("post_session_message", new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["submissionId"] = submissionId.ToString(),
            ["content"] = content
        }, ct);
        return data != null;
    }

    /// <summary>
    /// Asks <paramref name="agentId"/> in a Hivemind Session (<c>ask_agent</c>, runtime.execute): the owner's message,
    /// addressed to that agent, which takes the next turn with its receipt; the Session pauses after the reply. Without
    /// <paramref name="sessionId"/> Hivemind opens a Session with that agent; with one it continues it. The reply is
    /// read with <see cref="ReadSessionAsync"/>. The submission id makes a retry return the first ask.
    /// </summary>
    public async Task<HivemindAsk?> AskAgentAsync(string? sessionId, string agentId, string content, Guid submissionId, string? title = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["submissionId"] = submissionId.ToString(),
            ["content"] = content,
            ["agentId"] = agentId
        };
        if (!string.IsNullOrEmpty(sessionId)) args["sessionId"] = sessionId;
        if (!string.IsNullOrEmpty(title)) args["title"] = title;
        var data = await CallAsync("ask_agent", args, ct);
        if (data is not JsonElement root || Str(root, "sessionId").Length == 0) return null;
        return new HivemindAsk(Str(root, "sessionId"), Str(root, "messageId"), Str(root, "status"));
    }

    private static HivemindSessionSummary ReadSessionSummary(JsonElement s)
    {
        var participants = s.TryGetProperty("participants", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        return new HivemindSessionSummary(Str(s, "id"), FirstNonEmpty(Str(s, "title"), "Untitled session"), Str(s, "status"), participants, Date(s, "updatedAt"), Str(s, "error"));
    }

    private static DateTimeOffset Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue;

    private async Task<bool> ConnectAsync(bool interactive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client != null) return true;
            if (!interactive && !HasSavedSignIn)
            {
                SetState(HivemindState.NotSignedIn, string.Empty);
                return false;
            }
            if (interactive)
            {
                // A browser sign-in registers afresh: the saved client id keeps the name it was registered under
                // (Omniwand, before the rename), and Hivemind's Authorize page and client lists show that name.
                ArkStationClient.TryDeleteWindowsCredential(CredentialTarget);
                SetState(HivemindState.SigningIn, string.Empty);
            }

            _client = await CreateClientAsync(interactive
                ? BrowserSignInAsync
                : (_, _) => throw new InvalidOperationException("Hivemind needs a new sign-in. Use Sign in to Hivemind in Holodex."), ct);
            SetState(HivemindState.Connected, string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _client = null;
            SetState(HasSavedSignIn ? HivemindState.Unreachable : HivemindState.NotSignedIn, Describe(ex));
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<McpClient> CreateClientAsync(Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>> authorize, CancellationToken ct, ITokenCache? tokenCache = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "Hivemind",
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = RedirectUri,
                ScopeSelector = offered => RequestedScopes.Where(scope => offered?.Contains(scope) ?? true).ToArray(),
                Scopes = RequestedScopes,
                // Hivemind names the client from this registration: Session posts and connected-client lists show "Holodex".
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Holodex", ApplicationType = "native" },
                TokenCache = tokenCache ?? new WinCredTokenCache(CredentialTarget),
                AuthorizationCallbackHandler = authorize
            }
        }, _http);
        return McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "holodex", Title = "Holodex", Version = "0.3.0" }
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Runs the sign-in up to the browser and stops there (pipe <c>hivemind:probe</c>): discovery, dynamic
    /// registration, PKCE and scope selection all happen against the live server, and the authorization URL the
    /// browser would open comes back with its state and challenge redacted. The probe registers into a throwaway
    /// cache, so the saved sign-in is neither used nor touched, nothing is saved and no browser opens.
    /// </summary>
    public async Task<string> ProbeSignInAsync(CancellationToken ct = default)
    {
        Uri? seen = null;
        try
        {
            await using var client = await CreateClientAsync((context, _) =>
            {
                seen = context.AuthorizationUri;
                throw new OperationCanceledException("probe stops before the browser");
            }, ct, new ThrowawayTokenCache());
            return "connected without a sign-in (unexpected)";
        }
        catch (Exception ex)
        {
            if (seen == null) return $"stopped before authorization: {Describe(ex)}";
            var q = System.Web.HttpUtility.ParseQueryString(seen.Query);
            foreach (string key in new[] { "state", "code_challenge" })
                if (q[key] != null) q[key] = $"<{q[key]!.Length} chars>";
            return $"{seen.GetLeftPart(UriPartial.Path)}?{q}";
        }
    }

    /// <summary>
    /// Calls a Hivemind tool and returns the envelope's <c>data</c> (Hivemind answers with one text block holding
    /// <c>{ok, data, error, message}</c>), or null with <see cref="LastError"/> set.
    /// </summary>
    private async Task<JsonElement?> CallAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct)) return null;
        try
        {
            var result = await _client!.CallToolAsync(tool, args, cancellationToken: ct);
            string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (result.IsError == true || !ok)
            {
                LastError = FirstNonEmpty(Str(root, "message"), Str(root, "error"), text.Length > 0 ? text : $"{tool} failed");
                return null;
            }
            LastError = string.Empty;
            return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
        }
        catch (Exception ex)
        {
            // A dropped session or an expired sign-in: the next call reconnects (silently, with the saved tokens).
            await DropClientAsync();
            SetState(HasSavedSignIn ? HivemindState.Unreachable : HivemindState.NotSignedIn, Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The SDK hands over the authorization URL; this opens it in the default browser and catches the redirect on
    /// the fixed loopback port, then answers the browser with a short page.
    /// </summary>
    private static async Task<AuthorizationResult?> BrowserSignInAsync(AuthorizationCallbackContext context, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri.GetLeftPart(UriPartial.Path));
        listener.Start();
        Process.Start(new ProcessStartInfo(context.AuthorizationUri.ToString()) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        var pending = listener.GetContextAsync();
        await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
        if (!pending.IsCompleted) throw new TimeoutException("The Hivemind sign-in was not finished in the browser within 5 minutes.");

        var http = await pending;
        var query = System.Web.HttpUtility.ParseQueryString(http.Request.Url?.Query ?? string.Empty);
        string? error = query["error"];
        string page = error == null
            ? "Holodex is signed in to Hivemind. You can close this tab."
            : $"Hivemind sign-in did not finish: {WebUtility.HtmlEncode(query["error_description"] ?? error)}";
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Holodex</title><body style=\"background:#050607;color:#e4d9c0;font:16px sans-serif;padding:40px\">{page}</body>");
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.OutputStream.WriteAsync(body, ct);
        http.Response.Close();

        if (error != null) throw new InvalidOperationException($"Hivemind sign-in: {query["error_description"] ?? error}");
        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
    }

    private static HivemindMission? ReadMission(JsonElement root, string agentFallback)
    {
        var receipt = FindObjectWith(root, "executionId");
        if (receipt is not JsonElement r) return null;
        string model = r.TryGetProperty("model", out var m)
            ? m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : FirstNonEmpty(Str(m, "id"), Str(m, "model"), Str(m, "name"))
            : string.Empty;
        string result = r.TryGetProperty("result", out var res)
            ? res.ValueKind == JsonValueKind.String ? res.GetString() ?? "" : FirstNonEmpty(Str(res, "text"), Str(res, "output"), Str(res, "summary"), res.ValueKind == JsonValueKind.Null ? "" : res.GetRawText())
            : string.Empty;
        string error = r.TryGetProperty("error", out var err)
            ? err.ValueKind == JsonValueKind.String ? err.GetString() ?? "" : FirstNonEmpty(Str(err, "message"), err.ValueKind == JsonValueKind.Null ? "" : err.GetRawText())
            : string.Empty;
        return new HivemindMission(Str(r, "executionId"), FirstNonEmpty(Str(r, "targetAgentId"), agentFallback), Str(r, "status"), model, result, error);
    }

    private static JsonElement? FindArray(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array) return v;
        foreach (var p in e.EnumerateObject())
            if (FindArray(p.Value, name) is JsonElement found) return found;
        return null;
    }

    private static JsonElement? FindObjectWith(JsonElement e, string property)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(property, out _)) return e;
        foreach (var p in e.EnumerateObject())
            if (FindObjectWith(p.Value, property) is JsonElement found) return found;
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Describe(Exception ex) => (ex is AggregateException agg ? agg.InnerException ?? ex : ex).Message;

    private void SetState(HivemindState state, string error)
    {
        LastError = error;
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    private async Task DropClientAsync()
    {
        var client = _client;
        _client = null;
        if (client != null)
        {
            try { await client.DisposeAsync(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropClientAsync();
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// The SDK's token cache, kept in Windows Credential Manager. The container carries the access and refresh
    /// tokens plus the registered client id and issuer, so a restart refreshes without registering again.
    /// </summary>
    private sealed class WinCredTokenCache(string target) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            string? json = ArkStationClient.TryReadWindowsCredential(target);
            if (string.IsNullOrWhiteSpace(json)) return ValueTask.FromResult<TokenContainer?>(null);
            try { return ValueTask.FromResult(JsonSerializer.Deserialize<TokenContainer>(json)); }
            catch (JsonException) { return ValueTask.FromResult<TokenContainer?>(null); }
        }

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            ArkStationClient.TryWriteWindowsCredential(target, JsonSerializer.Serialize(tokens));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The probe's cache: starts empty and lives only as long as the probe (the SDK's own is internal).</summary>
    private sealed class ThrowawayTokenCache : ITokenCache
    {
        private TokenContainer? _tokens;
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_tokens);
        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            _tokens = tokens;
            return ValueTask.CompletedTask;
        }
    }
}
