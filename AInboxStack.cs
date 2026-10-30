using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omniwand;

// The AInbox intake domain (AInboxUpload CP-27, design targets "Capture tools" and "Stack, predict, review"):
// captures carry their origin, stack into context groups, Omnitek ranks what each group should become (Nemotron
// refines the ranking), the Handoff Scroll's fields adapt to the chosen intent, Aegis redacts secrets before
// anything leaves, and handoffs queue for the AInbox service (:8840) until it accepts them.

/// <summary>Where a capture came from, scanned while it is taken (the design target's ORIGIN SCAN).</summary>
internal sealed record CaptureOrigin(string App, string Window, string? Url = null, string? Element = null, string? Repo = null, string? KnownToArk = null)
{
    /// <summary>Captures with the same key stack into one group.</summary>
    public string ContextKey => (Repo ?? $"{App}|{CoreTitle(Window)}").ToLowerInvariant();

    public string ContextLabel => Repo != null ? $"{Repo.ToUpperInvariant()}" : $"FROM {App.ToUpperInvariant()} // {CoreTitle(Window).ToUpperInvariant()}";

    /// <summary>"PR 1068 · ark - Google Chrome" reads "PR 1068 · ark": the app suffix is the App field already.</summary>
    public static string CoreTitle(string title)
    {
        string t = title.Trim();
        int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return t.Length > 40 ? t[..40] : t.Length == 0 ? "untitled" : t;
    }

    public IEnumerable<(string Key, string Value)> Rows()
    {
        yield return ("APP", App);
        yield return ("WINDOW", Window);
        if (Url != null) yield return ("URL", Url);
        if (Element != null) yield return ("ELEMENT", Element);
        if (Repo != null) yield return ("REPO", Repo);
        if (KnownToArk != null) yield return ("KNOWN TO ARK", KnownToArk);
    }
}

internal static class OriginScanner
{
    private const string ArkStationRoot = @"E:\ArkStation";

    /// <summary>The window under a screen point: its app, title and the element class at that point.</summary>
    public static CaptureOrigin AtPoint(Point screen)
    {
        try
        {
            IntPtr hit = WindowFromPoint(new POINT { X = screen.X, Y = screen.Y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) return new CaptureOrigin("Desktop", "Desktop");
            var title = new StringBuilder(512);
            GetWindowText(root, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hit, cls, cls.Capacity);
            GetWindowThreadProcessId(root, out uint pid);
            string app = "Unknown";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                app = proc.ProcessName;
                try { app = proc.MainModule?.FileVersionInfo.ProductName is { Length: > 0 } product ? product : app; } catch { }
            }
            catch { }
            string window = title.ToString();
            string? url = Regex.Match(window, @"https?://\S+").Value is { Length: > 0 } u ? u : null;
            string? repo = Regex.Match(window, @"\b([A-Za-z0-9-]+/[A-Za-z0-9._-]+)\b.*(Pull Request|pull/|GitHub)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            return new CaptureOrigin(app, window, url, cls.ToString(), repo, OwnerRepoTag(repo));
        }
        catch
        {
            return new CaptureOrigin("Desktop", "Desktop");
        }
    }

    /// <summary>A file's folder, the git repository and branch it sits in, and whether it is inside ArkStation.</summary>
    public static CaptureOrigin ForFile(string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
        string? repo = null;
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? "");
            while (dir != null && repo == null)
            {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    string head = Directory.Exists(git) ? Path.Combine(git, "HEAD") : "";
                    string branch = File.Exists(head) && File.ReadAllText(head).Trim() is var h && h.StartsWith("ref: refs/heads/") ? h["ref: refs/heads/".Length..] : "";
                    repo = branch.Length > 0 ? $"{dir.Name} // {branch}" : dir.Name;
                }
                dir = dir.Parent;
            }
        }
        catch { }
        string? ark = path.StartsWith(ArkStationRoot, StringComparison.OrdinalIgnoreCase)
            ? "ArkStation // " + string.Join('\\', Path.GetRelativePath(ArkStationRoot, Path.GetDirectoryName(path) ?? ArkStationRoot).Split('\\').Take(2))
            : null;
        return new CaptureOrigin("File", folder.Length > 0 ? folder : "Desktop", Element: Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), Repo: repo, KnownToArk: ark);
    }

    /// <summary>A link: its host, and the repository when it is a GitHub URL.</summary>
    public static CaptureOrigin ForLink(string url, CaptureOrigin source)
    {
        string host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        string? repo = null;
        if (u != null && u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length >= 2) repo = $"{parts[0]}/{parts[1]}";
        }
        return source with
        {
            Url = url,
            Window = repo == null ? host : source.Window,
            Repo = repo ?? source.Repo,
            KnownToArk = OwnerRepoTag(repo) ?? source.KnownToArk
        };
    }

    /// <summary>The owner's own repositories are tagged on the owner's Ark only; a public install tags none.</summary>
    private static string? OwnerRepoTag(string? repo) =>
        ArkStationClient.ArkOnThisPc && repo != null && repo.StartsWith("GalaAI/", StringComparison.OrdinalIgnoreCase) ? "GalaAI repository" : null;

    /// <summary>A PNG of the whole top-level window under a screen point (the design target's WINDOW capture).</summary>
    public static (string? DataUrl, Size Size) WindowShotAt(Point screen)
    {
        try
        {
            IntPtr hit = WindowFromPoint(new POINT { X = screen.X, Y = screen.Y });
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2);
            if (root == IntPtr.Zero) return (null, Size.Empty);
            // DWM's extended frame bounds exclude the invisible resize border GetWindowRect includes on Windows 10+.
            if (DwmGetWindowAttribute(root, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(root, out r);
            int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ("data:image/png;base64," + Convert.ToBase64String(ms.ToArray()), new Size(w, h));
        }
        catch { return (null, Size.Empty); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

/// <summary>One capture in the stack.</summary>
internal sealed class StackItem
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public CaptureKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Meta { get; set; } = "";
    public string? Path { get; init; }
    public string? DataUrl { get; init; }
    public string? Text { get; init; }
    public string? Url { get; init; }
    public CaptureOrigin Origin { get; init; } = new("Desktop", "Desktop");
    public string? Sha256 { get; set; }
    public List<AegisFinding> Secrets { get; } = new();
    public bool Scanned { get; set; }

    public string MediaType => Kind switch
    {
        CaptureKind.Link => "text/uri-list",
        CaptureKind.Snippet => "text/plain",
        _ when DataUrl != null => "image/png",
        _ => MediaTypes.For(Path ?? Name)
    };

    public string Channel => Kind switch
    {
        CaptureKind.Link => "link",
        CaptureKind.Snippet => "text",
        CaptureKind.Screen or CaptureKind.Window or CaptureKind.Pick or CaptureKind.Lasso => "screenshot",
        _ => "file"
    };

    /// <summary>Text this item carries for ranking and scanning: the snippet, the link, or a text file's head.</summary>
    public string? TextForAnalysis()
    {
        if (Text != null) return Text;
        if (Url != null) return Url;
        if (Path != null && MediaTypes.IsText(Path))
        {
            try
            {
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length <= 2 * 1024 * 1024) return File.ReadAllText(Path);
            }
            catch { }
        }
        return null;
    }
}

internal static class MediaTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".pdf"] = "application/pdf", [".csv"] = "text/csv", [".json"] = "application/json", [".jsonl"] = "application/jsonl",
        [".md"] = "text/markdown", [".txt"] = "text/plain", [".log"] = "text/plain", [".sql"] = "text/plain",
        [".cs"] = "text/plain", [".ts"] = "text/plain", [".tsx"] = "text/plain", [".js"] = "text/plain", [".py"] = "text/plain",
        [".ps1"] = "text/plain", [".sh"] = "text/plain", [".yml"] = "text/yaml", [".yaml"] = "text/yaml", [".env"] = "text/plain",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".wav"] = "audio/wav", [".mp3"] = "audio/mpeg",
        [".zip"] = "application/zip", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    public static string For(string path) => Map.TryGetValue(System.IO.Path.GetExtension(path), out var t) ? t : "application/octet-stream";

    public static bool IsText(string path) => For(path) is var t && (t.StartsWith("text/") || t is "application/json" or "application/jsonl");
}

/// <summary>A context group: captures that share an origin. Lettered A, B, C in the order they arrived.</summary>
internal sealed class StackGroup
{
    public string Letter { get; set; } = "A";
    public string Key { get; init; } = "";
    public string Label { get; set; } = "";
    public List<StackItem> Items { get; } = new();
    public List<IntentOption> Ranking { get; set; } = new();
    public string ChosenIntent { get; set; } = "fix";
    public bool ChosenByHand { get; set; }
    public string Why { get; set; } = "";
    public string WhySource { get; set; } = "OMNITEK";
    public string Synthesis { get; set; } = "";
    public Dictionary<string, ScrollField> Fields { get; } = new();
    public string FieldsIntent { get; set; } = "";
    public int RefineGeneration { get; set; }
    public bool Refining { get; set; }

    public IntentOption Chosen => Ranking.FirstOrDefault(o => o.Intent.Id == ChosenIntent) ?? Ranking.FirstOrDefault() ?? IntentCatalog.Option("fix", 1f, "nexus");
}

/// <summary>A Scroll field's value and where it came from (PREDICTED, FROM STACK A, NEMOTRON, YOU EDITED).</summary>
internal sealed record ScrollField(string Label, string Value, string Source);

/// <summary>An intent and the Scroll fields it needs (the design target's FIELDS ADAPT TO THE INTENT).</summary>
internal sealed record IntentShape(string Id, string Label, string Kicker, string[] Fields);

internal sealed record IntentOption(IntentShape Intent, float Score, string PodId)
{
    public ArkPodInboxInfo Pod => ArkStationClient.PodInboxes.FirstOrDefault(p => p.PodId == PodId) ?? ArkStationClient.PodInboxes[0];
}

internal static class IntentCatalog
{
    public static readonly IntentShape[] All =
    {
        ArkStationClient.ArkOnThisPc
            ? new("fix", "Fix it in a Pod", "FIX IN A POD", new[] { "Context", "Goal", "Repro", "Evidence", "Acceptance" })
            : new("fix", "Fix it", "FIX IT", new[] { "Context", "Goal", "Repro", "Evidence", "Acceptance" }),
        new("flow", "Build a Flow", "BUILD A FLOW", new[] { "Trigger", "Kind", "Steps", "Output", "Haptic" }),
        new("research", "Research digest", "RESEARCH", new[] { "Question", "Sources", "Depth", "Deliverable" }),
        new("absorb", "Absorb into dataset", "ABSORB DATASET", new[] { "Schema", "License", "Destination", "Refresh" }),
        new("content", "Scroll for content", "SCROLL CONTENT", new[] { "Audience", "Angle", "Format", "Channel" }),
        // Quest proof: the Watcher reviews it and mints XP for the skill (Stats page).
        new("proof", "Prove a quest", "QUEST PROOF", new[] { "Quest", "Skill", "What I did", "Evidence" }),
    };

    public static IntentShape Shape(string id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];

    public static IntentOption Option(string id, float score, string podId) => new(Shape(id), score, podId);

    /// <summary>The pod each intent lands in. A fix goes where Omnitek routes the stack's item type.</summary>
    public static string PodFor(string intent, string fixPod) => intent switch
    {
        "flow" => "forge",
        "research" => "cortex",
        "absorb" => "cortex",
        "content" => "magixlab",
        "proof" => "nexus",
        _ => fixPod
    };
}

/// <summary>
/// Omnitek's ranking of what a group should become, from the stack's kinds, files and words, plus the intent
/// the operator typed. Instant and local; <see cref="StackRefiner"/> asks Nemotron to re-rank it afterwards.
/// </summary>
internal static class IntentRanker
{
    private static readonly (string Intent, string Pattern, string Reason)[] Words =
    {
        ("fix", @"\b(fix|bug|error|fail\w*|broken|crash|why|hold\w*|exception|stack ?trace|regress\w*)\b", "words about a failure"),
        ("flow", @"\b(every|daily|weekly|schedule\w*|automate|ritual|combo|spell|macro|when .+ then|cron|each morning)\b", "a repeating or automatable ask"),
        ("research", @"\b(research|summari[sz]e|read|learn|compare|explain|what is|digest|sources?)\b", "a question to answer"),
        ("absorb", @"\b(dataset|corpus|import|ingest|table|rows|schema|select .+ from|export)\b", "data to keep"),
        ("content", @"\b(post|publish|social|blog|thumbnail|caption|newsletter|launch|announce)\b", "something to publish"),
    };

    public static void Rank(StackGroup g, string intentText, ArkStationClient ark)
    {
        var score = IntentCatalog.All.ToDictionary(s => s.Id, _ => 1f);
        var reasons = new List<string>();
        void Add(string intent, float w, string why) { score[intent] += w; if (!reasons.Contains(why)) reasons.Add(why); }

        foreach (var it in g.Items)
        {
            string ext = Path.GetExtension(it.Path ?? it.Name).ToLowerInvariant();
            switch (it.Kind)
            {
                case CaptureKind.Pick: Add("fix", 2.5f, "a picked element"); break;
                case CaptureKind.Lasso or CaptureKind.Screen or CaptureKind.Window: Add("fix", 1.2f, "a screen capture"); Add("content", 0.8f, "a screen capture"); break;
                case CaptureKind.Link:
                    if (it.Origin.Repo != null) Add("fix", 2f, "a repository link"); else Add("research", 2.5f, "a web link");
                    break;
                case CaptureKind.Snippet:
                    string t = it.Text ?? "";
                    if (Regex.IsMatch(t, @"[{};]|=>|\bdef \b|\bfunction\b|\bclass\b")) Add("fix", 2.5f, "a code snippet");
                    else if (Regex.IsMatch(t, @"\bselect\b.+\bfrom\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)) Add("absorb", 3f, "a SQL query");
                    else Add("research", 1f, "a text snippet");
                    break;
            }
            if (ext is ".csv" or ".jsonl" or ".parquet" or ".xlsx" or ".db") Add("absorb", 4f, "a tabular file");
            else if (ext is ".pdf" or ".docx" or ".md") Add("research", 2.5f, "a document");
            else if (ext is ".png" or ".jpg" or ".jpeg" or ".mp4" or ".webm" or ".wav" or ".mp3" or ".gif") Add("content", 2.5f, "media");
            else if (ext is ".cs" or ".ts" or ".tsx" or ".py" or ".js" or ".log") Add("fix", 3f, "source or logs");
            else if (ext is ".ps1" or ".sh" or ".bamf") Add("flow", 2f, "a script");
        }

        string words = intentText + " " + string.Join(" ", g.Items.Select(i => i.Text ?? ""));
        foreach (var (intent, pattern, why) in Words)
            if (Regex.IsMatch(words, pattern, RegexOptions.IgnoreCase)) Add(intent, intentText.Length > 0 && Regex.IsMatch(intentText, pattern, RegexOptions.IgnoreCase) ? 4f : 1.5f, why);

        var detected = ark.AutoDetectOmnitekItem(g.Items.FirstOrDefault(i => i.Path != null)?.Path, string.Join("\n", g.Items.Select(i => i.Text ?? i.Url ?? "")), intentText);
        if (detected.ItemType == "flow") Add("flow", 3f, $"a {detected.ItemSubtype} Spell");
        // Code, picks and repository links get fixed in 4GE; Navigate, SolaTree and infra work in Eden; otherwise
        // the pod Omnitek routes the item type to.
        bool code = reasons.Any(r => r is "a picked element" or "a code snippet" or "a repository link" or "source or logs");
        string fixPod = Regex.IsMatch(words, @"\b(navigate|solatree|eden|deploy\w*|infra\w*|worktree|graft)\b", RegexOptions.IgnoreCase) ? "eden"
            : code ? "forge" : detected.RecommendedPodId;

        float total = score.Values.Sum();
        g.Ranking = score.Select(kv => IntentCatalog.Option(kv.Key, kv.Value / total, IntentCatalog.PodFor(kv.Key, fixPod)))
            .OrderByDescending(o => o.Score).ToList();
        if (!g.ChosenByHand) g.ChosenIntent = g.Ranking[0].Intent.Id;
        g.Why = reasons.Count > 0 ? string.Join(" + ", reasons.Take(3)) : "nothing specific yet; steer it";
        g.WhySource = "OMNITEK";
    }
}

/// <summary>Predicted Scroll fields for the chosen intent, from the stack itself. Operator edits are never overwritten.</summary>
internal static class ScrollPredictor
{
    public static void Fill(StackGroup g, string intentText)
    {
        var shape = IntentCatalog.Shape(g.ChosenIntent);
        if (g.FieldsIntent != shape.Id)
        {
            g.Fields.Clear();
            g.FieldsIntent = shape.Id;
        }
        string names = g.Items.Count == 0 ? "the request" : string.Join(", ", g.Items.Select(i => i.Name).Take(4));
        string first = g.Items.FirstOrDefault()?.Name ?? "the request";
        string ask = intentText.Trim().Split('\n')[0];
        string fromStack = $"FROM STACK {g.Letter}";
        var o = g.Items.FirstOrDefault()?.Origin;
        string where = o == null ? "" : o.Repo ?? CaptureOrigin.CoreTitle(o.Window);
        string time = Regex.Match(ask, @"\b(every \w+( at \d+(:\d+)?\s*(am|pm)?)?|daily|weekly|each morning|nightly)\b", RegexOptions.IgnoreCase).Value;
        var pod = g.Chosen.Pod;

        var predicted = shape.Id switch
        {
            "fix" => new Dictionary<string, (string, string)>
            {
                ["Context"] = ($"{(where.Length > 0 ? where + ": " : "")}{(ask.Length > 0 ? ask : names)}", "PREDICTED"),
                ["Goal"] = (ask.Length > 0 ? ask : $"Resolve what {first} shows.", ask.Length > 0 ? "FROM YOUR ASK" : "PREDICTED"),
                ["Repro"] = ($"Open {first}{(o?.Url != null ? " at " + o.Url : "")} and follow the marked steps.", fromStack),
                ["Evidence"] = ($"{g.Letter}: {names}", fromStack),
                ["Acceptance"] = ($"{pod.Label} returns a verified fix with a test or validation receipt.", "PREDICTED"),
            },
            "flow" => new()
            {
                ["Trigger"] = (time.Length > 0 ? time : "On demand from the Spell Orb", time.Length > 0 ? "FROM YOUR ASK" : "PREDICTED"),
                ["Kind"] = (Regex.IsMatch(ask, @"every|daily|weekly|nightly|schedule", RegexOptions.IgnoreCase) ? "Ritual" : "Combo", "PREDICTED"),
                ["Steps"] = ($"1. Read {names}. 2. {(ask.Length > 0 ? ask : "Do the task")}. 3. Report the result.", fromStack),
                ["Output"] = ("A Spell bound to a free Spell Orb slot, with a receipt per run.", "PREDICTED"),
                ["Haptic"] = ("MX Master 4 Actions Ring, next free slot.", "PREDICTED"),
            },
            "research" => new()
            {
                ["Question"] = (ask.Length > 0 ? ask : $"What {(g.Items.Count > 1 ? "do" : "does")} {names} say, and what should we do about it?", ask.Length > 0 ? "FROM YOUR ASK" : "PREDICTED"),
                ["Sources"] = (names, fromStack),
                ["Depth"] = ("One-page digest with citations.", "PREDICTED"),
                ["Deliverable"] = ($"A research digest from {pod.Label}.", "PREDICTED"),
            },
            "absorb" => new()
            {
                ["Schema"] = (CsvHeader(g) ?? "Detect on ingest.", CsvHeader(g) != null ? fromStack : "PREDICTED"),
                ["License"] = ("Owner data, internal use.", "PREDICTED"),
                ["Destination"] = ($"{pod.Label} library: {Slug(where.Length > 0 ? where : first)}", "PREDICTED"),
                ["Refresh"] = (time.Length > 0 ? time : "One-time import.", time.Length > 0 ? "FROM YOUR ASK" : "PREDICTED"),
            },
            _ => new()
            {
                ["Audience"] = ("People who follow your work.", "PREDICTED"),
                ["Angle"] = (ask.Length > 0 ? ask : $"What {first} shows, and why it matters.", ask.Length > 0 ? "FROM YOUR ASK" : "PREDICTED"),
                ["Format"] = (g.Items.Any(i => i.MediaType.StartsWith("video")) ? "Short video post" : "Image post with a caption", fromStack),
                ["Channel"] = ("Social post, drafted for your review.", "PREDICTED"),
            },
        };

        foreach (var label in shape.Fields)
        {
            if (g.Fields.TryGetValue(label, out var existing) && existing.Source is "YOU EDITED" or "NEMOTRON") continue;
            var (value, source) = predicted[label];
            g.Fields[label] = new ScrollField(label, value, source);
        }
    }

    private static string? CsvHeader(StackGroup g)
    {
        var csv = g.Items.FirstOrDefault(i => i.Path != null && i.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        try { return csv?.Path != null && File.Exists(csv.Path) ? File.ReadLines(csv.Path).FirstOrDefault() : null; }
        catch { return null; }
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } x ? x : "intake";
}

/// <summary>
/// Nemotron's pass over a group (Token Factory, Nemotron 3 Nano): re-rank the intents, say why in one line,
/// synthesize what the captures have in common, and fill the chosen intent's fields. Secrets are redacted before
/// the text leaves; images are described by name and origin only.
/// </summary>
internal static class StackRefiner
{
    public const string Model = ArkStationClient.DefaultNemotronFastModel;

    public static async Task<bool> RefineAsync(StackGroup g, string intentText, ArkStationClient ark, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Omnitek, the intake classifier inside ArkStation's AInbox. A user stacked these captures as one group.");
        foreach (var it in g.Items)
        {
            string text = AegisScanner.Redact(it.TextForAnalysis() ?? "");
            sb.AppendLine($"- {it.Kind} \"{it.Name}\" from {it.Origin.App} / {CaptureOrigin.CoreTitle(it.Origin.Window)}{(it.Origin.Url != null ? " " + it.Origin.Url : "")}{(text.Length > 0 ? ": " + (text.Length > 400 ? text[..400] + "..." : text) : "")}");
        }
        sb.AppendLine($"User's ask: {(intentText.Trim().Length > 0 ? AegisScanner.Redact(intentText.Trim()) : "(none)")}");
        sb.AppendLine("Intents: fix (fix it in a Pod), flow (build an automation), research (research digest), absorb (absorb into a dataset), content (publishable content).");
        var shape = IntentCatalog.Shape(g.Ranking.FirstOrDefault()?.Intent.Id ?? "fix");
        sb.AppendLine("Reply with JSON only, no prose: {\"ranking\":{\"fix\":0-100,\"flow\":0-100,\"research\":0-100,\"absorb\":0-100,\"content\":0-100},"
            + "\"top\":\"<intent>\",\"why\":\"<one short line>\",\"synthesis\":\"<two sentences on what the captures share and what they point at>\","
            + "\"fields\":{<each field of the top intent>: \"<one line>\"}}. Fields per intent: "
            + string.Join("; ", IntentCatalog.All.Select(s => $"{s.Id}: {string.Join(", ", s.Fields)}")) + ".");

        var answer = await ark.AskAsync(sb.ToString(), null, Model).WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (!answer.Ok) return false;
        string output = answer.Output;
        int a = output.IndexOf('{'), b = output.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(output[a..(b + 1)]);
            var root = doc.RootElement;
            if (root.TryGetProperty("ranking", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                var scores = IntentCatalog.All.ToDictionary(s => s.Id, s => r.TryGetProperty(s.Id, out var v) && v.TryGetDouble(out double d) ? (float)Math.Max(0, d) : 0f);
                float total = scores.Values.Sum();
                if (total > 0)
                {
                    string fixPod = g.Ranking.FirstOrDefault(o => o.Intent.Id == "fix")?.PodId ?? "nexus";
                    g.Ranking = scores.Select(kv => IntentCatalog.Option(kv.Key, kv.Value / total, IntentCatalog.PodFor(kv.Key, fixPod)))
                        .OrderByDescending(o => o.Score).ToList();
                    if (!g.ChosenByHand) g.ChosenIntent = g.Ranking[0].Intent.Id;
                }
            }
            if (root.TryGetProperty("why", out var why) && why.GetString() is { Length: > 0 } w) { g.Why = w; g.WhySource = "NEMOTRON"; }
            if (root.TryGetProperty("synthesis", out var syn) && syn.GetString() is { Length: > 0 } s) g.Synthesis = s;
            if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object && IntentCatalog.Shape(g.ChosenIntent) is var chosen)
            {
                if (g.FieldsIntent != chosen.Id) { g.Fields.Clear(); g.FieldsIntent = chosen.Id; }
                foreach (var label in chosen.Fields)
                {
                    if (g.Fields.TryGetValue(label, out var cur) && cur.Source == "YOU EDITED") continue;
                    var prop = f.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase));
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } v)
                        g.Fields[label] = new ScrollField(label, v, "NEMOTRON");
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>A secret Aegis found: what kind, which item, and a masked form that is safe to show.</summary>
internal sealed record AegisFinding(string Kind, string Masked, string Secret);

/// <summary>
/// Aegis secret gate: finds tokens and keys in text captures and text files. Found secrets are redacted in what
/// leaves Omniwand; the operator can stow each one in Windows Credential Manager. Images are not scanned.
/// </summary>
internal static class AegisScanner
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    {
        ("GitHub token", new Regex(@"\b(gh[pousr]_[A-Za-z0-9]{16,255}|github_pat_[A-Za-z0-9_]{22,255})\b", RegexOptions.Compiled)),
        ("AWS access key", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Anthropic key", new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled)),
        ("OpenAI key", new Regex(@"\bsk-(proj-)?[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled)),
        ("Stripe key", new Regex(@"\b[rs]k_live_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled)),
        ("Slack token", new Regex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
        ("Private key", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)),
        ("Assigned secret", new Regex(@"(?i)\b(api[_-]?key|secret|password|token)\b\s*[:=]\s*[""']?([A-Za-z0-9_\-./+]{16,})", RegexOptions.Compiled)),
    };

    public static List<AegisFinding> Scan(string text)
    {
        var found = new List<AegisFinding>();
        foreach (var (kind, pattern) in Patterns)
            foreach (Match m in pattern.Matches(text))
            {
                string secret = m.Groups.Count > 2 && kind == "Assigned secret" ? m.Groups[2].Value : m.Value;
                if (found.Any(f => f.Secret == secret || f.Secret.Contains(secret) || secret.Contains(f.Secret))) continue;
                found.Add(new AegisFinding(kind, Mask(secret), secret));
            }
        return found;
    }

    public static string Redact(string text)
    {
        foreach (var f in Scan(text)) text = text.Replace(f.Secret, $"[AEGIS:{f.Kind.ToUpperInvariant().Replace(' ', '-')}]");
        return text;
    }

    public static string Mask(string s) => s.Length <= 8 ? new string('*', s.Length) : $"{s[..4]}...{s[^2..]}";
}

/// <summary>A handoff as it is sent: the scroll, its fields, its captures, and the seal over them.</summary>
internal sealed record StackHandoff(
    string ScrollId,
    string Group,
    string GroupLabel,
    string PodId,
    string IntentId,
    string IntentLabel,
    string Ask,
    List<ScrollField> Fields,
    List<QueuedItem> Items,
    string Sha256,
    string CreatedAt);

/// <summary>A capture waiting to be delivered: where its (redacted) bytes are, and whether AInbox has it yet.</summary>
internal sealed record QueuedItem(string Id, string Name, string Kind, string Channel, string MediaType, string? FilePath, string? Text, string? Url, string? Sha256, Dictionary<string, string> Origin)
{
    public string? AInboxItemId { get; set; }
    public string? Error { get; set; }
}

internal static class HandoffSeal
{
    /// <summary>
    /// SHA-256 (lowercase hex) of what the receiving agent acts on. Canonical form, UTF-8, fields joined by LF:
    /// "ark.handoff-scroll.v2", pod id, intent id, the ask, each field as "Label=value" in shape order, then each
    /// capture's SHA-256 (or its name when it has none) in stack order. Ids and timestamps are left out, so a
    /// draft and the sent scroll seal the same when nothing changed.
    /// </summary>
    public static string Seal(string podId, string intentId, string ask, IEnumerable<ScrollField> fields, IEnumerable<string> captureShas)
    {
        var parts = new List<string> { "ark.handoff-scroll.v2", podId, intentId, ask.Trim() };
        parts.AddRange(fields.Select(f => $"{f.Label}={f.Value}"));
        parts.AddRange(captureShas);
        return ArkStationClient.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
    }

    public static string Markdown(StackHandoff h, ArkPodInboxInfo pod) =>
        $"# Handoff scroll: {h.IntentLabel} -> {pod.Label}\n\n" +
        $"- Scroll: {h.ScrollId}\n- Seal: sha256:{h.Sha256}\n- {(ArkStationClient.ArkOnThisPc ? "Pod" : "Destination")}: {pod.Label} ({pod.InboxChannel}), orchestrator {pod.OrchestratorLabel}\n" +
        $"- Stack {h.Group}: {h.GroupLabel}\n- Written: {h.CreatedAt}\n\n" +
        (h.Ask.Length > 0 ? $"## Ask\n\n{h.Ask}\n\n" : "") +
        string.Join("", h.Fields.Select(f => $"## {f.Label}\n\n{f.Value}\n\n_{f.Source.ToLowerInvariant()}_\n\n")) +
        "## Captures\n\n" + string.Join("\n", h.Items.Select(i => $"- {i.Kind} `{i.Name}`{(i.Sha256 != null ? $" sha256:{i.Sha256}" : "")}")) + "\n";
}

/// <summary>
/// The outbound queue (the design target's SYNCED and QUEUED states). A handoff is written to disk first, then
/// delivered to the AInbox service (:8840) as one capture per item plus the scroll itself; anything the service
/// cannot take yet stays queued and retries every 30 seconds. Idempotency keys make a retry safe.
/// </summary>
internal sealed class OutboundQueue
{
    public const string ServiceUrl = "http://127.0.0.1:8840";
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omniwand", "ainbox-queue");
    private static readonly string PendingDir = Path.Combine(Root, "pending");
    private static readonly string SentDir = Path.Combine(Root, "sent");
    private static readonly string BlobDir = Path.Combine(Root, "blobs");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where a public install keeps sealed Scrolls: one Markdown file each, in the user's Documents.</summary>
    public static readonly string SavedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Holodex", "Scrolls");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 30_000 };
    private readonly SemaphoreSlim _drainLock = new(1, 1);

    private static OutboundQueue? _shared;

    /// <summary>The one queue every sender uses (AInbox intake, quest proof), so a single drain owns the folder. Create on the UI thread.</summary>
    /// <summary>True on a public install (no AInbox service), so Send saves the sealed Scroll on this PC. Set at startup, before <see cref="Shared"/> is first read.</summary>
    public static bool SharedLocalOnly { get; set; }

    public static OutboundQueue Shared => _shared ??= new OutboundQueue(SharedLocalOnly);

    /// <summary>Raised after a "proof" handoff is queued, so the Watcher can review it (HolodexProgressionService).</summary>
    public static event Action<StackHandoff>? ProofQueued;

    internal static void RaiseProofQueued(StackHandoff h) => ProofQueued?.Invoke(h);

    private OutboundQueue(bool localOnly)
    {
        LocalOnly = localOnly;
        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(SentDir);
        Directory.CreateDirectory(BlobDir);
        if (LocalOnly) return;
        _retry.Tick += async (_, _) => await DrainAsync();
        _retry.Start();
    }

    public bool LocalOnly { get; }
    public int SavedToday => Directory.Exists(SavedDir) ? Directory.GetFiles(SavedDir, "*.md").Count(f => File.GetLastWriteTime(f).Date == DateTime.Today) : 0;

    /// <summary>Writes the sealed Scroll (its Markdown, captures listed by seal) to <see cref="SavedDir"/> and returns the file.</summary>
    public async Task<string> SaveLocallyAsync(StackHandoff h)
    {
        Directory.CreateDirectory(SavedDir);
        string markdown = h.Items.FirstOrDefault(i => i.Id == "scroll")?.Text ?? "";
        string path = Path.Combine(SavedDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{SafeName(h.IntentLabel)}-{h.Sha256[..12]}.md");
        await File.WriteAllTextAsync(path, markdown);
        Changed?.Invoke(this, EventArgs.Empty);
        return path;
    }

    public int Pending => SafeCount(PendingDir);
    public int SentToday => Directory.Exists(SentDir) ? Directory.GetFiles(SentDir, "*.json").Count(f => File.GetLastWriteTime(f).Date == DateTime.Today) : 0;
    public bool? ServiceOnline { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread whenever the counts or the service state change.</summary>
    public event EventHandler? Changed;

    /// <summary>Stores bytes the queue must keep (a redacted copy, a decoded image) and returns their path.</summary>
    public static string StoreBlob(string name, byte[] bytes)
    {
        Directory.CreateDirectory(BlobDir);
        string path = Path.Combine(BlobDir, $"{Guid.NewGuid():N}-{Regex.Replace(name, @"[^A-Za-z0-9._-]", "_")}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public async Task<StackHandoff> EnqueueAsync(StackHandoff h)
    {
        await File.WriteAllTextAsync(Path.Combine(PendingDir, $"{SafeName(h.ScrollId)}.json"), JsonSerializer.Serialize(h, Json));
        Changed?.Invoke(this, EventArgs.Empty);
        await DrainAsync();
        return h;
    }

    public async Task DrainAsync()
    {
        if (LocalOnly) return;
        if (!await _drainLock.WaitAsync(0)) return;
        try
        {
            ServiceOnline = await ProbeAsync();
            if (ServiceOnline != true) { LastError = "AInbox service (:8840) not reachable"; return; }
            foreach (string file in Directory.GetFiles(PendingDir, "*.json").OrderBy(File.GetCreationTimeUtc))
            {
                StackHandoff? h;
                try { h = JsonSerializer.Deserialize<StackHandoff>(await File.ReadAllTextAsync(file)); }
                catch { continue; }
                if (h == null) continue;
                bool allDone = true;
                foreach (var item in h.Items.Where(i => i.AInboxItemId == null))
                {
                    var (ok, id, error, retry) = await DeliverAsync(h, item);
                    if (ok) { item.AInboxItemId = id; item.Error = null; }
                    else { item.Error = error; allDone = false; LastError = error; if (retry) break; }
                }
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(h, Json));
                if (allDone)
                {
                    File.Move(file, Path.Combine(SentDir, Path.GetFileName(file)), overwrite: true);
                    LastError = null;
                }
            }
        }
        finally
        {
            _drainLock.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var r = await _http.GetAsync($"{ServiceUrl}/health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// One capture to AInbox. Files, images and text stream to /captures/file; links go to /ainbox/ingest. Every
    /// capture carries the handoff (scroll id, pod, intent, fields, seal) and its origin in metadata.
    /// </summary>
    private async Task<(bool Ok, string? Id, string? Error, bool Retry)> DeliverAsync(StackHandoff h, QueuedItem item)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sourceId"] = "omniwand",
            ["kind"] = "desktop-hook",
            ["channel"] = item.Channel,
            ["actor"] = "operator:omniwand",
            ["summary"] = $"{h.IntentLabel} // {item.Name}".Length > 2048 ? item.Name[..2000] : $"{h.IntentLabel} // {item.Name}",
            ["idempotencyKey"] = $"omniwand:{h.Sha256[..32]}:{item.Id}",
            ["provenance"] = new Dictionary<string, string> { ["capturedAt"] = h.CreatedAt, ["capturedBy"] = "omniwand", ["sourceSystem"] = "omniwand", ["sourceRecordId"] = h.ScrollId },
            ["metadata"] = new Dictionary<string, object?>
            {
                ["handoff"] = new Dictionary<string, object?>
                {
                    ["scrollId"] = h.ScrollId, ["stack"] = h.Group, ["podId"] = h.PodId, ["intent"] = h.IntentId,
                    ["ask"] = h.Ask, ["fields"] = h.Fields.ToDictionary(f => f.Label, f => f.Value), ["sha256"] = h.Sha256
                },
                ["origin"] = item.Origin,
            },
        };
        try
        {
            HttpResponseMessage resp;
            if (item.Url != null)
            {
                payload["contentRef"] = item.Url;
                resp = await _http.PostAsync($"{ServiceUrl}/ainbox/ingest", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            }
            else
            {
                byte[] bytes = item.FilePath != null ? await File.ReadAllBytesAsync(item.FilePath) : Encoding.UTF8.GetBytes(item.Text ?? "");
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl}/captures/file") { Content = new ByteArrayContent(bytes) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                req.Headers.Add("x-ainbox-capture", Uri.EscapeDataString(JsonSerializer.Serialize(payload)));
                req.Headers.Add("x-ainbox-file-name", Uri.EscapeDataString(item.Name));
                req.Headers.Add("x-ainbox-media-type", item.MediaType);
                resp = await _http.SendAsync(req);
            }
            using (resp)
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    string? id = null;
                    try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("item", out var it) && it.TryGetProperty("id", out var idEl)) id = idEl.GetString(); } catch { }
                    return (true, id ?? "accepted", null, false);
                }
                // 5xx and 409 are worth retrying; 4xx means the capture itself is wrong and will not get better.
                bool retry = (int)resp.StatusCode >= 500 || (int)resp.StatusCode == 409 || (int)resp.StatusCode == 401;
                return (false, null, $"AInbox {(int)resp.StatusCode}: {body[..Math.Min(160, body.Length)]}", retry);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, null, ex.Message, true);
        }
    }

    private static int SafeCount(string dir) { try { return Directory.GetFiles(dir, "*.json").Length; } catch { return 0; } }
    private static string SafeName(string s) => Regex.Replace(s, @"[^A-Za-z0-9._-]", "_");
}
