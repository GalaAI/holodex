using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace Omniwand;

/// <summary>
/// <c>Holodex.exe --ask "&lt;prompt&gt;" [--model &lt;id&gt;] [--count &lt;n&gt;]</c>: the HUD's own ask
/// (<see cref="ArkStationClient.AskAsync"/>) without any window, one JSON line per answer on stdout, then a summary
/// line with the success count and latency percentiles. It reads the same keys and ARK_API_BASE_URL as the app,
/// so a run can prove the Ark API route and, pointed at a dead port, the Token Factory direct route.
/// Toasts are off; nothing is shown on the desktop.
/// </summary>
internal static class HeadlessAsk
{
    public static int Run(string[] args)
    {
        string prompt = args.Length > 1 ? args[1] : "Say hello in one sentence.";
        string? model = Option(args, "--model");
        int count = int.TryParse(Option(args, "--count"), out int n) ? Math.Clamp(n, 1, 100) : 1;

        PhosphorToast.Suppressed = true;
        var ark = new ArkStationClient();
        var latencies = new System.Collections.Generic.List<long>();
        int ok = 0;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            start = DateTimeOffset.Now.ToString("O"),
            arkApiBaseUrl = ArkStationClient.ArkApiBaseUrl,
            arkInstalled = ark.ArkInstalled,
            model = model ?? ark.SelectedModel,
            count
        }));
        for (int i = 1; i <= count; i++)
        {
            var clock = Stopwatch.StartNew();
            var answer = ark.AskAsync(prompt, null, model).GetAwaiter().GetResult();
            clock.Stop();
            latencies.Add(clock.ElapsedMilliseconds);
            if (answer.Ok) ok++;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                i,
                ms = clock.ElapsedMilliseconds,
                answer.Ok,
                answer.Route,
                routeLabel = answer.RouteLabel,
                answer.Model,
                answer.ReceiptId,
                answer.FallbackReason,
                output = answer.Output.Length > 160 ? answer.Output[..160] + "..." : answer.Output
            }));
        }
        latencies.Sort();
        long P(double q) => latencies[Math.Min(latencies.Count - 1, (int)Math.Ceiling(q * latencies.Count) - 1)];
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            summary = true,
            ok,
            failed = count - ok,
            errorRate = Math.Round((count - ok) / (double)count, 3),
            p50Ms = P(0.50),
            p95Ms = P(0.95),
            maxMs = latencies.Last()
        }));
        return ok == count ? 0 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
