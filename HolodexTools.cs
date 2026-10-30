using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Omniwand;

/// <summary>
/// The tools Nemotron gets on this PC, next to web_search (<see cref="WebSearch"/>), each switched on by a Vault
/// adapter:
/// <list type="bullet">
/// <item>Files (<c>local-files</c>): list_files, read_file and write_file inside the folder the user chose (default
/// Documents\Holodex). Every path is resolved and must stay under that root; anything else is refused.</item>
/// <item>PC command line (<c>local-pc-cli</c>): run_command runs one PowerShell command, and only after the user
/// approves that exact command in Holodex. This is how Nemotron configures PC Mod and the other mods on a public
/// install. Nothing runs unapproved, and output is capped.</item>
/// </list>
/// </summary>
internal static class HolodexTools
{
    public const string FilesId = "local-files";
    public const string CliId = "local-pc-cli";
    public static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Holodex");
    private const int MaxRead = 20_000;
    private const int MaxOutput = 6_000;

    /// <summary>The UI context approvals are shown on (set by Program at startup).</summary>
    public static SynchronizationContext? Ui { get; set; }

    public static string? FilesRoot =>
        HolodexVault.Load().FirstOrDefault(e => e.ProviderId == FilesId && e.Status == "works") is { } e && Directory.Exists(e.Url) ? e.Url : null;

    public static bool CliConnected => HolodexVault.Load().Any(e => e.ProviderId == CliId);

    public static bool Any => FilesRoot != null || CliConnected || GoogleAdapter.SignedIn;

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
            }
        }
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    /// <summary>Adds the connected local tools to a chat payload's tools and says what they reach.</summary>
    public static void Offer(JsonObject payload)
    {
        if (payload["tools"] is not JsonArray tools) payload["tools"] = tools = new JsonArray();
        var notes = new StringBuilder();
        if (FilesRoot is { } root)
        {
            tools.Add(Tool("list_files", "List files and folders in the user's Holodex files folder. Paths are relative to it.",
                new JsonObject { ["path"] = Str("Folder relative to the files root; empty for the root.") }));
            tools.Add(Tool("read_file", "Read a text file from the user's Holodex files folder.",
                new JsonObject { ["path"] = Str("File path relative to the files root.") }, "path"));
            tools.Add(Tool("write_file", "Write a text file in the user's Holodex files folder, replacing it if it exists.",
                new JsonObject { ["path"] = Str("File path relative to the files root."), ["content"] = Str("The whole file text.") }, "path", "content"));
            notes.Append($" You can read and write files only inside the user's files folder ({root}).");
        }
        if (CliConnected)
        {
            tools.Add(Tool("run_command",
                "Run one PowerShell command on the user's Windows PC, for example to inspect or configure PC Mod and other mods. The user sees the exact command and approves or declines it first; explain why in 'why'.",
                new JsonObject { ["command"] = Str("A single PowerShell command."), ["why"] = Str("One sentence the user reads before approving.") }, "command", "why"));
            notes.Append(" You can run PowerShell commands on the user's PC with run_command; each one needs the user's approval, so ask only for what the task needs.");
        }
        if (GoogleAdapter.SignedIn)
        {
            GoogleAdapter.Offer(tools);
            notes.Append(" You can search and read the user's Google Drive with drive_search and drive_read.");
        }
        payload["tool_choice"] = "auto";
        if (notes.Length > 0 && payload["messages"] is JsonArray messages && messages.Count > 0 && messages[0] is JsonObject system)
            system["content"] = system["content"]?.GetValue<string>() + notes;
    }

    /// <summary>Runs one local tool call and returns the text handed back to the model.</summary>
    public static async Task<string> RunAsync(string name, JsonNode? args, CancellationToken ct = default)
    {
        string Arg(string key) => args?[key]?.GetValue<string>() ?? "";
        try
        {
            return name switch
            {
                "list_files" => ListFiles(Arg("path")),
                "read_file" => ReadFile(Arg("path")),
                "write_file" => await WriteFileAsync(Arg("path"), Arg("content"), ct),
                "run_command" => await RunCommandAsync(Arg("command"), Arg("why"), ct),
                "drive_search" => await GoogleAdapter.SearchAsync(Arg("query"), ct),
                "drive_read" => await GoogleAdapter.ReadAsync(Arg("id"), ct),
                _ => $"Unknown tool {name}."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            return $"{name} failed: {ex.Message}";
        }
    }

    /// <summary>A path under the files root, or an error. Absolute paths and .. that leave the root are refused.</summary>
    public static string Resolve(string relative)
    {
        string root = Path.GetFullPath(FilesRoot ?? throw new InvalidOperationException("No files folder is connected in the Vault."));
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\', '/')));
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{relative} is outside the files folder.");
        return full;
    }

    private static string ListFiles(string relative)
    {
        string dir = Resolve(relative);
        if (!Directory.Exists(dir)) return $"No folder {relative}.";
        var lines = Directory.GetDirectories(dir).Select(d => Path.GetFileName(d) + "/")
            .Concat(Directory.GetFiles(dir).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)"))
            .Take(200).ToList();
        return lines.Count == 0 ? "The folder is empty." : string.Join("\n", lines);
    }

    private static string ReadFile(string relative)
    {
        string path = Resolve(relative);
        if (!File.Exists(path)) return $"No file {relative}.";
        string text = File.ReadAllText(path);
        return text.Length > MaxRead ? text[..MaxRead] + $"\n[truncated at {MaxRead} characters]" : text;
    }

    private static async Task<string> WriteFileAsync(string relative, string content, CancellationToken ct)
    {
        if (relative.Length == 0) return "No path was given.";
        string path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
        return $"Wrote {relative} ({content.Length} characters).";
    }

    /// <summary>Asks the user on the UI thread whether to run the exact command; true only on Run.</summary>
    public static Func<string, string, Task<bool>> Approve { get; set; } = (command, why) =>
    {
        var done = new TaskCompletionSource<bool>();
        void Ask(object? _)
        {
            using var dialog = new CommandApprovalForm(command, why);
            done.TrySetResult(dialog.ShowDialog() == DialogResult.OK);
        }
        if (Ui != null) Ui.Post(Ask, null); else done.TrySetResult(false);
        return done.Task;
    };

    private static async Task<string> RunCommandAsync(string command, string why, CancellationToken ct)
    {
        if (command.Trim().Length == 0) return "No command was given.";
        if (!await Approve(command, why)) return "The user declined this command. Do not run it again; ask what they want instead.";
        var psi = new ProcessStartInfo("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = FilesRoot ?? DefaultRoot
        };
        Directory.CreateDirectory(psi.WorkingDirectory);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch (Exception) { } return "The command ran past 60 seconds and was stopped."; }
        string output = (await stdout + (await stderr is { Length: > 0 } err ? "\n[stderr]\n" + err : "")).Trim();
        if (output.Length > MaxOutput) output = output[..MaxOutput] + "\n[output truncated]";
        return $"Exit code {proc.ExitCode}.\n{(output.Length > 0 ? output : "(no output)")}";
    }
}

/// <summary>The approval Holodex shows before Nemotron's command runs: the reason, the exact command, Run or Don't run.</summary>
internal sealed class CommandApprovalForm : Form
{
    public CommandApprovalForm(string command, string why)
    {
        PhosphorFonts.EnsureLoaded();
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = PhosphorTheme.Surface;
        ForeColor = PhosphorTheme.Bone;
        Name = "CommandApproval";
        Text = "Run this command?";
        Size = new Size(620, 330);
        Padding = WidgetChrome.FramePadding();

        var box = new PhosphorField("", multiline: true, 120, mono: true) { Name = "CommandApproval.Command" };
        box.Box.Text = command;
        box.Box.ReadOnly = true;
        var reason = new DataLabel(why.Length > 0 ? why : "Nemotron gave no reason.") { Name = "CommandApproval.Why", Height = 22 };
        var run = new PhosphorButton("Run", PhosphorButtonVariant.Primary) { Name = "CommandApproval.Run", Dock = DockStyle.Right, Width = 150 };
        var decline = new PhosphorButton("Don't run") { Name = "CommandApproval.Decline", Dock = DockStyle.Right, Width = 150 };
        run.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        decline.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var actions = new Panel { Dock = DockStyle.Bottom, Height = PhosphorTheme.TouchMin, BackColor = Color.Transparent, Name = "CommandApproval.Actions" };
        actions.Controls.Add(decline);
        actions.Controls.Add(new Panel { Dock = DockStyle.Right, Width = PhosphorTheme.S2, BackColor = Color.Transparent });
        actions.Controls.Add(run);

        Controls.Add(WidgetChrome.Labeled("COMMAND // RUNS ONLY IF YOU CHOOSE RUN", box, 120));
        Controls.Add(WidgetChrome.Spacer(PhosphorTheme.S2));
        Controls.Add(reason);
        Controls.Add(actions);
        WidgetChrome.Attach(this, "HOLODEX // PC COMMAND LINE // APPROVAL", "Run this command?", PhosphorTheme.Ember, null);
        ResumeLayout(false);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        WidgetChrome.PaintFrame(this, e.Graphics, PhosphorTheme.Line2);
    }
}
