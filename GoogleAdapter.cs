using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Omniwand;

/// <summary>
/// Google Drive in the Vault, with the user's own Google OAuth client: Holodex ships no Google client or secret.
/// The user creates a Desktop app client in their Google Cloud project, pastes its downloaded client JSON into the
/// Vault's intake, and signs in. The flow is Google's for desktop apps (docs checked 2026-10-30): the browser
/// authorizes at accounts.google.com with PKCE S256 and redirects to a loopback port on this PC
/// (<see cref="HivemindClient.LoopbackSignInAsync(Uri, Uri, string, CancellationToken)"/>); the code is exchanged at
/// oauth2.googleapis.com. Desktop clients are public, so the client secret is not a secret, but it still stays in
/// Credential Manager. Scope is Drive read-only. Nemotron then gets drive_search and drive_read.
/// </summary>
internal static class GoogleAdapter
{
    public const string Id = "google";
    public const string ClientTarget = "Omniwand:Vault:google-client";
    public const string TokenTarget = "Omniwand:Vault:google";
    public const string Scope = "https://www.googleapis.com/auth/drive.readonly";
    public const string ConsoleUrl = "https://console.cloud.google.com/apis/credentials";
    private static readonly Uri Redirect = new("http://127.0.0.1:47613/vault/google/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool HasClient => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(ClientTarget));
    public static bool SignedIn => !string.IsNullOrWhiteSpace(ArkStationClient.TryReadWindowsCredential(TokenTarget));

    /// <summary>True when the pasted text is a Google OAuth client file (its installed or web section has a client_id).</summary>
    public static bool LooksLikeClientJson(string raw)
    {
        try { return ClientSection(JsonNode.Parse(raw.Trim())) != null; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static JsonObject? ClientSection(JsonNode? root) =>
        (root?["installed"] ?? root?["web"]) is JsonObject s && s["client_id"] != null ? s : null;

    /// <summary>Keeps the client id and secret in Credential Manager and records Google Drive as ready to sign in.</summary>
    public static VaultEntry SaveClient(string raw)
    {
        var section = ClientSection(JsonNode.Parse(raw.Trim())) ?? throw new InvalidOperationException("That is not a Google OAuth client file.");
        var client = new JsonObject
        {
            ["client_id"] = section["client_id"]!.GetValue<string>(),
            ["client_secret"] = section["client_secret"]?.GetValue<string>() ?? ""
        };
        if (!ArkStationClient.TryWriteWindowsCredential(ClientTarget, client.ToJsonString()))
            throw new InvalidOperationException("Windows Credential Manager refused the Google client.");
        var entry = Entry();
        entry.Status = "unchecked";
        entry.Detail = "Client saved. Choose Sign in on the Google Drive card.";
        HolodexVault.Save(entry);
        return entry;
    }

    private static VaultEntry Entry() => HolodexVault.Load().FirstOrDefault(e => e.Id == Id) ?? new VaultEntry
    {
        Id = Id, ProviderId = Id, Label = "Google Drive", Kind = VaultKind.SignIn, Target = TokenTarget,
        Url = "https://www.googleapis.com/drive/v3", AddedAt = DateTimeOffset.Now
    };

    private static (string Id, string Secret) Client()
    {
        var c = JsonNode.Parse(ArkStationClient.TryReadWindowsCredential(ClientTarget) ?? throw new InvalidOperationException("Paste your Google OAuth client file into the Vault first."));
        return (c!["client_id"]!.GetValue<string>(), c["client_secret"]?.GetValue<string>() ?? "");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The browser sign-in: PKCE S256, loopback redirect, offline access so the refresh token is kept.</summary>
    public static async Task<VaultEntry> SignInAsync(CancellationToken ct = default)
    {
        var (clientId, secret) = Client();
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var auth = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect.ToString(),
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
        var result = await HivemindClient.LoopbackSignInAsync(auth, Redirect, "Google", ct)
            ?? throw new InvalidOperationException("Google sign-in returned nothing.");
        if (result.State != state) throw new InvalidOperationException("Google sign-in came back with a different state; nothing was saved.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = result.Code ?? "",
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = Redirect.ToString()
        };
        if (secret.Length > 0) form["client_secret"] = secret;
        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google token exchange answered HTTP {(int)res.StatusCode}.");
        var tokens = JsonNode.Parse(body)!;
        StoreTokens(tokens["access_token"]?.GetValue<string>() ?? "", tokens["refresh_token"]?.GetValue<string>(), tokens["expires_in"]?.GetValue<int>() ?? 3600);

        var entry = Entry();
        return await VerifyAsync(entry, ct);
    }

    private static void StoreTokens(string access, string? refresh, int expiresIn)
    {
        string? keptRefresh = refresh;
        if (string.IsNullOrEmpty(keptRefresh) && ArkStationClient.TryReadWindowsCredential(TokenTarget) is { } old)
            keptRefresh = JsonNode.Parse(old)?["refresh_token"]?.GetValue<string>();
        ArkStationClient.TryWriteWindowsCredential(TokenTarget, new JsonObject
        {
            ["access_token"] = access,
            ["refresh_token"] = keptRefresh ?? "",
            ["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60).ToUnixTimeSeconds()
        }.ToJsonString());
    }

    /// <summary>A current access token, refreshed with the saved refresh token when it has expired.</summary>
    private static async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var saved = JsonNode.Parse(ArkStationClient.TryReadWindowsCredential(TokenTarget) ?? throw new InvalidOperationException("Google Drive is not signed in."))!;
        if (saved["expires_at"]?.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return saved["access_token"]!.GetValue<string>();
        var (clientId, secret) = Client();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = saved["refresh_token"]?.GetValue<string>() ?? ""
        };
        if (secret.Length > 0) form["client_secret"] = secret;
        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google refresh answered HTTP {(int)res.StatusCode}; sign in again.");
        var tokens = JsonNode.Parse(body)!;
        string access = tokens["access_token"]!.GetValue<string>();
        StoreTokens(access, tokens["refresh_token"]?.GetValue<string>(), tokens["expires_in"]?.GetValue<int>() ?? 3600);
        return access;
    }

    private static async Task<JsonNode?> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        using var res = await Http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google Drive answered HTTP {(int)res.StatusCode}.");
        return JsonNode.Parse(body);
    }

    /// <summary>Proves the sign-in by reading the Drive account's name; records works or failed.</summary>
    public static async Task<VaultEntry> VerifyAsync(VaultEntry entry, CancellationToken ct = default)
    {
        entry.CheckedAt = DateTimeOffset.Now;
        try
        {
            var about = await GetAsync("https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)", ct);
            entry.Status = "works";
            entry.Fingerprint = "signed in";
            entry.Detail = about?["user"]?["emailAddress"]?.GetValue<string>() ?? "signed in";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            entry.Status = SignedIn ? "failed" : "unchecked";
            entry.Detail = ex.Message;
        }
        HolodexVault.Save(entry);
        return entry;
    }

    /// <summary>Signs out: deletes the tokens and the client from Credential Manager.</summary>
    public static void Forget()
    {
        ArkStationClient.TryDeleteWindowsCredential(TokenTarget);
        ArkStationClient.TryDeleteWindowsCredential(ClientTarget);
    }

    // ---- Nemotron tools ----

    public static void Offer(JsonArray tools)
    {
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "drive_search",
                ["description"] = "Search the user's Google Drive by words in file names and contents. Returns ids, names, types and links.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("query")
                }
            }
        });
        tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "drive_read",
                ["description"] = "Read a Google Drive file's text by its id (Google Docs, Sheets and Slides export as text).",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("id")
                }
            }
        });
    }

    public static async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        string q = $"(name contains '{query.Replace("'", "\\'")}' or fullText contains '{query.Replace("'", "\\'")}') and trashed = false";
        var res = await GetAsync("https://www.googleapis.com/drive/v3/files?pageSize=10&fields=files(id,name,mimeType,modifiedTime,webViewLink)&q=" + Uri.EscapeDataString(q), ct);
        var files = res?["files"] as JsonArray;
        if (files == null || files.Count == 0) return $"No Drive files match \"{query}\".";
        return string.Join("\n", files.OfType<JsonObject>().Select(f =>
            $"{f["name"]} | id {f["id"]} | {f["mimeType"]} | modified {f["modifiedTime"]} | {f["webViewLink"]}"));
    }

    public static async Task<string> ReadAsync(string id, CancellationToken ct)
    {
        var meta = await GetAsync($"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?fields=name,mimeType", ct);
        string mime = meta?["mimeType"]?.GetValue<string>() ?? "";
        string url = mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)
            ? $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString(mime.EndsWith("spreadsheet") ? "text/csv" : "text/plain")}"
            : $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media";
        if (!mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !mime.StartsWith("text/", StringComparison.Ordinal) && mime != "application/json")
            return $"{meta?["name"]} is {mime}; only text, Docs, Sheets and Slides can be read.";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        using var res = await Http.SendAsync(req, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return $"Google Drive answered HTTP {(int)res.StatusCode} for {meta?["name"]}.";
        return text.Length > 20_000 ? text[..20_000] + "\n[truncated]" : text;
    }
}
