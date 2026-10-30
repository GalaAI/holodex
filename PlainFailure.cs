using System;
using System.Text.RegularExpressions;

namespace Omniwand;

/// <summary>
/// What a failure card says. A worker's stderr line, an exception message or a gateway error can carry environment
/// variable names, paths, URLs or stack frames; those go to the log (%TEMP%\omniwand-errors.log) and the card says in
/// plain words who could not answer. A failure that is already a plain sentence is shown as it is.
/// </summary>
internal static class PlainFailure
{
    private static readonly Regex Internal = new(
        @"\b[A-Z][A-Z0-9]*_[A-Z0-9_]{2,}\b" +                          // ARK_MODEL_GATEWAY_INBOUND_KEY
        @"|environment variable|mission worker|Exception\b|\bat [A-Z][\w.]+\(" +
        @"|https?://|[A-Za-z]:\|\{\s*""?\w+""?\s*:|\bECONN\w+|\bstatus code\b|\bHTTP \d{3}\b|\b(?:401|403|404|500|502|503)\b",
        RegexOptions.Compiled);

    /// <summary>The card text for <paramref name="who"/>'s failure; the raw text is logged, never shown, when it looks internal.</summary>
    public static string For(string? raw, string who)
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return $"{who} could not answer this time.";
        if (!LooksInternal(text)) return text;
        CursorCrashGuard.Log($"{who} failure (shown as plain words): {text}");
        return $"{who} could not answer this time. Try again in a moment.";
    }

    internal static bool LooksInternal(string text) => Internal.IsMatch(text);
}
