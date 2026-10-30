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

/// <summary>Why a Hivemind model answer did not come back, so the card can say what to do.</summary>
public enum HivemindModelError { None, NotSignedIn, ScopeMissing, RateLimited, Upstream, Unreachable }

/// <summary>A <c>model_invoke</c> answer: the text and the model Hivemind reports, or the error and its cause.</summary>
public sealed record HivemindModelAnswer(bool Ok, string Text, string Model, string Error, HivemindModelError Kind, string ReceiptId = "")
{
    public static HivemindModelAnswer Fail(HivemindModelError kind, string model, string error) => new(false, "", model, error, kind);
}

/// <summary>One web result: the page title, its URL and the text the search returned for it.</summary>
public sealed record WebSearchResult(string Title, string Url, string Content);

/// <summary>A Session with its transcript, oldest message first.</summary>
public sealed record HivemindSession(HivemindSessionSummary Summary, IReadOnlyList<HivemindSessionMessage> Messages);

/// <summary>
/// Omniwand's connection to the user's Hivemind: the remote MCP server at <see cref="Endpoint"/> (OMNIWAND_HIVEMIND_URL,
/// else the hosted Hivemind), through the
/// official MCP C# SDK (ModelContextProtocol.Core) and its OAuth 2.1 client. Sign-in is the server's public-client
/// flow: dynamic registration, PKCE S256, and a browser sign-in that redirects to a fixed loopback port
/// (Hivemind matches redirect URIs exactly, so the port never changes). Tokens and the registered client id live in
/// the Windows Credential Manager slot <c>Omniwand:HivemindToken</c>; the SDK refreshes them, so the browser opens
/// only on the sign-in the operator starts (<see cref="SignInAsync"/>), never from a background call.
/// </summary>
public sealed class HivemindClient : IAsyncDisposable
{
    /// <summary>The hosted Hivemind, used when OMNIWAND_HIVEMIND_URL names no other.</summary>
    public const string HostedHivemindUrl = "https://hivemind.arkomnitek.com/mcp";

    /// <summary>
    /// The Hivemind this install signs in to: OMNIWAND_HIVEMIND_URL (process, then user environment), else the hosted
    /// one. A URL without a path gets Hivemind's /mcp. An unusable value falls back to the hosted Hivemind.
    /// </summary>
    public static readonly Uri Endpoint = ResolveEndpoint(
        Environment.GetEnvironmentVariable("OMNIWAND_HIVEMIND_URL") ??
        Environment.GetEnvironmentVariable("OMNIWAND_HIVEMIND_URL", EnvironmentVariableTarget.User));

    public static readonly Uri RedirectUri = new("http://127.0.0.1:47613/hivemind/callback/");

    /// <summary>
    /// The Credential Manager slot for the sign-in: <c>Omniwand:HivemindToken</c> for the hosted Hivemind, and one
    /// per host for any other, so two Hiveminds never share tokens or a registered client id.
    /// </summary>
    public static readonly string CredentialTarget = Endpoint.AbsoluteUri == HostedHivemindUrl
        ? "Omniwand:HivemindToken"
        : $"Omniwand:HivemindToken:{Endpoint.Authority}";

    /// <summary>The Hivemind web app's Sessions view (it addresses views, not single rooms), on the same host.</summary>
    public static readonly Uri WebSessionsView = new(Endpoint, "/?view=sessions");

    /// <summary>The host the profile names, such as hivemind.arkomnitek.com or 127.0.0.1:18788.</summary>
    public static string EndpointHost => Endpoint.Authority;

    internal static Uri ResolveEndpoint(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            // Only https, or http on this PC (a local Hivemind), carries a sign-in.
            return new Uri(HostedHivemindUrl);
        }
        return uri.AbsolutePath is "" or "/" ? new Uri(uri, "/mcp") : uri;
    }

    /// <summary>"Open in Hivemind": hands over to the web app's Sessions, where every Holodex Session lives too.</summary>
    public static void OpenWebSessions()
    {
        try { Process.Start(new ProcessStartInfo(WebSessionsView.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { PhosphorToast.Show("Hivemind did not open", ex.Message, "HOLODEX // HIVEMIND", 3600, ToastTone.Warning); }
    }

    /// <summary>
    /// Read the roster, mission status and saved Sessions (runtime.read); dispatch missions and post into Sessions
    /// (runtime.execute); ask NVIDIA Nemotron through the user's own Hivemind (model.invoke), which is how a PC with
    /// no Nebius key and no Ark gets answers. A capped account may consent to model.invoke alone; the others then
    /// answer scope-denied and Holodex says so.
    /// </summary>
    private static readonly string[] RequestedScopes = { "runtime.read", "runtime.execute", "model.invoke" };

    /// <summary>
    /// Asked for only when the server lists it in scopes_supported, so a Hivemind that predates it still signs in:
    /// web.search for Hivemind's hosted <c>web_search</c> tool.
    /// </summary>
    private static readonly string[] OptionalScopes = { "web.search" };

    /// <summary>The scopes a sign-in asks for, given the scopes the server offers (null when it lists none).</summary>
    internal static string[] SelectScopes(IEnumerable<string>? offered)
    {
        var list = offered?.ToList();
        return RequestedScopes.Where(scope => list?.Contains(scope) ?? true)
            .Concat(OptionalScopes.Where(scope => list?.Contains(scope) == true))
            .ToArray();
    }
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;

    public HivemindState State { get; private set; } = HasSavedSignIn ? HivemindState.Connecting : HivemindState.NotSignedIn;
    public string LastError { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public static bool HasSavedSignIn => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(CredentialTarget));

    /// <summary>
    /// The scopes the saved sign-in was granted (the token response's <c>scope</c>), or null when it did not say.
    /// Consent can narrow a request: a new account's token may carry only "model.invoke runtime.read".
    /// </summary>
    public static IReadOnlySet<string>? GrantedScopes
    {
        get
        {
            try
            {
                string? json = ArkStationClient.TryReadWindowsCredential(CredentialTarget);
                string? scope = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TokenContainer>(json)?.Scope;
                return string.IsNullOrWhiteSpace(scope) ? null : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>
    /// Whether the saved sign-in may use <paramref name="scope"/>: true when the grant names it or names nothing (an
    /// older Hivemind), false when signed out or when consent left it out. runtime.execute gates Gala asks, missions and
    /// work orders; model.invoke gates answers.
    /// </summary>
    public static bool Granted(string scope) => HasSavedSignIn && Granted(GrantedScopes, scope);

    internal static bool Granted(IReadOnlySet<string>? granted, string scope) => granted == null || granted.Contains(scope);

    /// <summary>What Holodex says when the sign-in cannot ask agents (consent left out runtime.execute).</summary>
    public const string NoAgentsOnThisSignIn =
        "This Hivemind sign-in answers with Nemotron but cannot ask agents or send work orders: it was not granted runtime.execute.";

    /// <summary>
    /// Opens a sign-in's authorization URL. Null opens the default browser; a headless proof (tests) sets it to drive
    /// the sign-up and consent pages itself, so no person's browser is used.
    /// </summary>
    internal static Func<Uri, Task>? OpenAuthorizationUrl { get; set; }

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
            await RevokeSavedSignInAsync();
            ArkStationClient.TryDeleteWindowsCredential(CredentialTarget);
            SetState(HivemindState.NotSignedIn, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the saved sign-in on the server before the local copy is deleted: an RFC 7009 revoke of the refresh token
    /// (which ends its whole token family) at this Hivemind's /revoke. Best effort, bounded to 5 seconds: a Hivemind
    /// that is unreachable or predates /revoke still gets its local sign-in removed.
    /// </summary>
    private async Task RevokeSavedSignInAsync()
    {
        TokenContainer? saved = null;
        try
        {
            string? json = ArkStationClient.TryReadWindowsCredential(CredentialTarget);
            if (!string.IsNullOrWhiteSpace(json)) saved = JsonSerializer.Deserialize<TokenContainer>(json);
        }
        catch (JsonException) { }
        string token = saved?.RefreshToken ?? saved?.AccessToken ?? string.Empty;
        string clientId = saved?.ClientId ?? string.Empty;
        if (token.Length == 0 || clientId.Length == 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token,
                ["token_type_hint"] = saved?.RefreshToken is { Length: > 0 } ? "refresh_token" : "access_token",
                ["client_id"] = clientId
            });
            // Always this install's own Hivemind origin, never an issuer read back from storage.
            using var response = await _http.PostAsync(new Uri(Endpoint, "/revoke"), form, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
    }

    /// <summary>Connects with the saved sign-in only; false (and no browser) when there is none or it no longer works.</summary>
    public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => ConnectAsync(interactive: false, ct);

    public async Task<IReadOnlyList<HivemindAgent>?> ListAgentsAsync(CancellationToken ct = default)
    {
        // The public roster: verified agents with public names, Gala first, no private vessel agents. A Hivemind
        // that predates the audience argument ignores it and returns its full roster.
        var data = await CallAsync("ark_agents", new Dictionary<string, object?> { ["audience"] = "public" }, ct);
        if (data is not JsonElement root) return null;
        var agents = FindArray(root, "agents");
        if (agents is not JsonElement list) return Array.Empty<HivemindAgent>();
        return list.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object && Str(a, "id").Length > 0)
            .Select(a => new HivemindAgent(
                Str(a, "id"),
                FirstNonEmpty(Str(a, "displayName"), Str(a, "name"), Str(a, "id")),
                Str(a, "role"),
                FirstNonEmpty(Str(a, "runtimeProvider"), Str(a, "provider")),
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
                // The replaced sign-in is revoked on the server first so its refresh token does not outlive it.
                await RevokeSavedSignInAsync();
                ArkStationClient.TryDeleteWindowsCredential(CredentialTarget);
                SetState(HivemindState.SigningIn, string.Empty);
            }

            _client = await CreateClientAsync(interactive
                ? BrowserSignInAsync
                : (_, _) => throw new InvalidOperationException($"{NeedsNewSignIn} Use Sign in to Hivemind in Holodex."), ct);
            SetState(HivemindState.Connected, string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _client = null;
            if (!EndSignInIfRevoked(ex))
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
                ScopeSelector = offered => SelectScopes(offered),
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
        var raw = await CallRawAsync(tool, args, ct);
        if (raw is not { } r) return null;
        var outcome = ReadEnvelope(r.Text, r.IsError, tool);
        LastError = outcome.Error;
        return outcome.Ok ? outcome.Data : null;
    }

    /// <summary>
    /// One tool call's text and error flag, or null when there is no connection (state and LastError say why). A
    /// tool that answers with an error is still a call: only a transport failure drops the connection.
    /// </summary>
    private async Task<(string Text, bool IsError)?> CallRawAsync(string tool, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct)) return null;
        try
        {
            var result = await _client!.CallToolAsync(tool, args, cancellationToken: ct);
            return (string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)), result.IsError == true);
        }
        catch (Exception ex)
        {
            // A dropped session or an expired sign-in: the next call reconnects (silently, with the saved tokens).
            await DropClientAsync();
            if (!EndSignInIfRevoked(ex))
                SetState(HasSavedSignIn ? HivemindState.Unreachable : HivemindState.NotSignedIn, Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// A Hivemind tool result read as its envelope. Hivemind answers with one text block holding
    /// <c>{ok, data, error, message}</c>; a tool that threw answers with the error's plain text and isError, and a
    /// missing scope with <c>{ok:false, error:"scope-denied", requiredScope}</c>.
    /// </summary>
    internal static ToolEnvelope ReadEnvelope(string text, bool isError, string tool)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text.Length > 0 ? text : "{}"); }
        catch (JsonException) { }
        using (doc)
        {
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ToolEnvelope(false, null, null, text.Trim().Length > 0 ? text.Trim() : $"{tool} failed", "");
            var root = doc.RootElement;
            bool ok = !isError && root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            string code = Str(root, "error");
            if (!ok)
                return new ToolEnvelope(false, null, root.Clone(), FirstNonEmpty(Str(root, "message"), code, text.Trim().Length > 0 ? text.Trim() : $"{tool} failed"), code,
                    Str(root, "requiredScope"));
            return new ToolEnvelope(true, root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone(), root.Clone(), "", "");
        }
    }

    /// <summary>A read tool result: ok with its data, or the error text, the error code and a missing scope.</summary>
    internal sealed record ToolEnvelope(bool Ok, JsonElement? Data, JsonElement? Root, string Error, string Code, string RequiredScope = "");

    // ---- Answers through the user's own Hivemind (model_invoke, scope model.invoke) -----------------------------------

    /// <summary>
    /// One completion through Hivemind's <c>model_invoke</c> (scope model.invoke). A capped account (a judge's) is
    /// answered from the hosted gateway by NVIDIA Nemotron on Nebius Token Factory, never through the owner's PC.
    /// Text only: Hivemind takes up to 16 messages of at most 8000 characters each, so longer content is trimmed.
    /// The answer carries the model Hivemind reports, or a plain error naming its cause.
    /// </summary>
    public async Task<HivemindModelAnswer> InvokeModelAsync(IReadOnlyList<(string Role, string Content)> messages, string? model, int maxTokens, CancellationToken ct = default)
    {
        if (!HasSavedSignIn && State != HivemindState.Connected)
            return HivemindModelAnswer.Fail(HivemindModelError.NotSignedIn, model ?? "", NotSignedInForAnswers);
        if (!Granted(GrantedScopes, "model.invoke"))
            return HivemindModelAnswer.Fail(HivemindModelError.ScopeMissing, model ?? "", ScopeMissingForAnswers);
        var args = new Dictionary<string, object?>
        {
            ["messages"] = ModelMessages(messages),
            ["maxTokens"] = Math.Clamp(maxTokens, 1, 4096),
            ["temperature"] = 0.2,
            ["stream"] = false
        };
        if (!string.IsNullOrWhiteSpace(model)) args["model"] = model;
        var raw = await CallRawAsync("model_invoke", args, ct);
        if (raw is not { } r)
        {
            return State == HivemindState.NotSignedIn
                ? HivemindModelAnswer.Fail(HivemindModelError.NotSignedIn, model ?? "", LastError.Length > 0 ? LastError : NotSignedInForAnswers)
                : HivemindModelAnswer.Fail(HivemindModelError.Unreachable, model ?? "", $"Hivemind did not answer: {FirstNonEmpty(LastError, "the connection failed")}.");
        }
        var answer = ReadModelInvocation(r.Text, r.IsError, model ?? "");
        LastError = answer.Ok ? string.Empty : answer.Error;
        return answer;
    }

    public const string ScopeMissingForAnswers =
        "Your Hivemind sign-in does not allow model answers (model.invoke). Sign out in Holodex, sign in again and allow model access.";

    public const string NotSignedInForAnswers =
        "Sign in with Hivemind to get answers: open Holodex (Ctrl+Alt+H), then the profile at the foot of the rail, then Sign in. No key is needed.";

    /// <summary>Hivemind's message limits: at most 16, each 1 to 8000 characters; the system message and the newest turns are kept.</summary>
    internal static List<Dictionary<string, string>> ModelMessages(IReadOnlyList<(string Role, string Content)> messages)
    {
        const int MaxMessages = 16, MaxChars = 8000;
        var kept = messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList();
        if (kept.Count > MaxMessages)
        {
            var system = kept.Where(m => m.Role == "system").Take(1).ToList();
            kept = system.Concat(kept.Where(m => m.Role != "system").TakeLast(MaxMessages - system.Count)).ToList();
        }
        return kept.Select(m =>
        {
            string content = m.Content.Trim();
            if (content.Length > MaxChars) content = content[..(MaxChars - 3)] + "...";
            string role = m.Role is "system" or "assistant" ? m.Role : "user";
            return new Dictionary<string, string> { ["role"] = role, ["content"] = content };
        }).ToList();
    }

    /// <summary>
    /// A model_invoke result as an answer. Success is <c>{ok:true, data:&lt;chat completion&gt;, receiptId?}</c>; the
    /// answering model is the completion's <c>model</c>. Failures are classed by cause so the card can say what to do.
    /// </summary>
    internal static HivemindModelAnswer ReadModelInvocation(string text, bool isError, string requestedModel)
    {
        var env = ReadEnvelope(text, isError, "model_invoke");
        if (!env.Ok)
        {
            string error = env.Error;
            if (env.Code == "scope-denied" || env.RequiredScope == "model.invoke" ||
                error.Contains("missing model.invoke", StringComparison.OrdinalIgnoreCase))
                return HivemindModelAnswer.Fail(HivemindModelError.ScopeMissing, requestedModel, ScopeMissingForAnswers);
            if (error.Contains("per minute", StringComparison.OrdinalIgnoreCase) || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("429", StringComparison.Ordinal))
                return HivemindModelAnswer.Fail(HivemindModelError.RateLimited, requestedModel, $"Hivemind is rate limiting answers. {error}");
            return HivemindModelAnswer.Fail(HivemindModelError.Upstream, requestedModel, $"Hivemind could not get an answer: {error}");
        }
        var data = env.Data!.Value;
        string model = FirstNonEmpty(Str(data, "model"), requestedModel);
        string answer = "";
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message))
            answer = FirstNonEmpty(Str(message, "content"), Str(message, "reasoning_content")).Trim();
        string receipt = env.Root is { } root ? Str(root, "receiptId") : "";
        return answer.Length == 0
            ? HivemindModelAnswer.Fail(HivemindModelError.Upstream, model, $"Hivemind returned no answer text from {model}.")
            : new HivemindModelAnswer(true, answer, model, "", HivemindModelError.None, receipt);
    }

    // ---- Tools this Hivemind offers, and its hosted web search -------------------------------------------------------

    private HashSet<string>? _tools;

    /// <summary>
    /// Whether the signed-in Hivemind lists <paramref name="tool"/> (tools/list, read once per connection). False
    /// when signed out or unreachable; never throws.
    /// </summary>
    public async Task<bool> OffersToolAsync(string tool, CancellationToken ct = default)
    {
        if (!HasSavedSignIn) return false;
        if (_tools is { } known) return known.Contains(tool);
        if (!await EnsureConnectedAsync(ct)) return false;
        try
        {
            var tools = await _client!.ListToolsAsync(cancellationToken: ct);
            _tools = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            return _tools.Contains(tool);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Hivemind's hosted <c>web_search</c> {query, maxResults}: the search runs on the user's Hivemind, so the PC
    /// needs no search key. Null with LastError set when the tool fails or is missing.
    /// </summary>
    public async Task<IReadOnlyList<WebSearchResult>?> WebSearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        var raw = await CallRawAsync("web_search", new Dictionary<string, object?> { ["query"] = query, ["maxResults"] = Math.Clamp(maxResults, 1, 10) }, ct);
        if (raw is not { } r) return null;
        var env = ReadEnvelope(r.Text, r.IsError, "web_search");
        LastError = env.Error;
        return env.Ok ? ReadSearchResults(env.Data!.Value) : null;
    }

    /// <summary>Search results wherever the data keeps them (results, or items), each with a URL.</summary>
    internal static IReadOnlyList<WebSearchResult> ReadSearchResults(JsonElement data)
    {
        var list = FindArray(data, "results") ?? FindArray(data, "items");
        if (list is not JsonElement items) return Array.Empty<WebSearchResult>();
        return items.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object && FirstNonEmpty(Str(i, "url"), Str(i, "link")).Length > 0)
            .Select(i => new WebSearchResult(Str(i, "title"), FirstNonEmpty(Str(i, "url"), Str(i, "link")), FirstNonEmpty(Str(i, "content"), Str(i, "snippet"), Str(i, "description"))))
            .ToList();
    }

    /// <summary>The words a signed-out state carries after Hivemind refused the saved sign-in.</summary>
    public const string SignInEndedMessage = "Your Hivemind sign-in ended. Sign in again.";

    /// <summary>
    /// When Hivemind refused the saved sign-in itself (invalid_grant, invalid_token, or a refresh that needs the
    /// browser), the saved tokens can never work again: they are deleted and the state is Signed out, not
    /// Unreachable with the SDK's raw message. True when that happened.
    /// </summary>
    private bool EndSignInIfRevoked(Exception ex)
    {
        string text = ex.ToString();
        bool revoked = text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains(NeedsNewSignIn, StringComparison.Ordinal);
        if (!revoked || !HasSavedSignIn) return false;
        ArkStationClient.TryDeleteWindowsCredential(CredentialTarget);
        SetState(HivemindState.NotSignedIn, SignInEndedMessage);
        return true;
    }

    private const string NeedsNewSignIn = "Hivemind needs a new sign-in.";

    /// <summary>
    /// The SDK hands over the authorization URL; this opens it in the default browser and catches the redirect on
    /// the fixed loopback port, then answers the browser with a short page.
    /// </summary>
    private static Task<AuthorizationResult?> BrowserSignInAsync(AuthorizationCallbackContext context, CancellationToken ct) =>
        LoopbackSignInAsync(context, RedirectUri, "Hivemind", ct);

    /// <summary>
    /// Opens an authorization URL in the default browser and catches its redirect on a loopback <paramref name="redirect"/>,
    /// then answers the browser with a short page naming <paramref name="service"/>. Shared by the Hivemind sign-in and
    /// the Vault's MCP server sign-ins.
    /// </summary>
    internal static Task<AuthorizationResult?> LoopbackSignInAsync(AuthorizationCallbackContext context, Uri redirect, string service, CancellationToken ct) =>
        LoopbackSignInAsync(context.AuthorizationUri, redirect, service, ct);

    /// <summary>The same loopback sign-in for a flow that builds its own authorization URL (the Vault's Google sign-in).</summary>
    internal static async Task<AuthorizationResult?> LoopbackSignInAsync(Uri authorizationUri, Uri redirect, string service, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Path));
        listener.Start();
        if (OpenAuthorizationUrl is { } open) _ = open(authorizationUri);
        else Process.Start(new ProcessStartInfo(authorizationUri.ToString()) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        var pending = listener.GetContextAsync();
        await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
        if (!pending.IsCompleted) throw new TimeoutException($"The {service} sign-in was not finished in the browser within 5 minutes.");

        var http = await pending;
        var query = System.Web.HttpUtility.ParseQueryString(http.Request.Url?.Query ?? string.Empty);
        string? error = query["error"];
        string page = error == null
            ? $"Holodex is signed in to {WebUtility.HtmlEncode(service)}. You can close this tab."
            : $"{WebUtility.HtmlEncode(service)} sign-in did not finish: {WebUtility.HtmlEncode(query["error_description"] ?? error)}";
        byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Holodex</title><body style=\"background:#050607;color:#e4d9c0;font:16px sans-serif;padding:40px\">{page}</body>");
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.OutputStream.WriteAsync(body, ct);
        http.Response.Close();

        if (error != null) throw new InvalidOperationException($"{service} sign-in: {query["error_description"] ?? error}");
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
        _tools = null;
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
    internal sealed class WinCredTokenCache(string target) : ITokenCache
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
