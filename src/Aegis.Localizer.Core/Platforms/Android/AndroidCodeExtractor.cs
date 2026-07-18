/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

using System.Text;
using System.Text.RegularExpressions;
using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;

namespace Aegis.Localizer.Platforms.Android;

/// <summary>
/// String literals in Kotlin and Java that reach the screen.
///
/// The strategy is deliberately the opposite of the C# extractor's: instead of collecting every
/// literal and then subtracting the ones that are not copy, this only ever matches literals that sit
/// in a known UI position - an argument of setText/Toast/Snackbar/AlertDialog, or a Compose
/// composable and its text-carrying named arguments. There is no Kotlin syntax tree available here,
/// and on Android the great majority of literals are tags, keys, intent actions and log messages;
/// an allow-list keeps those out by construction rather than by an ever-growing list of exclusions.
/// </summary>
public static partial class AndroidCodeExtractor
{
    /// <summary>
    /// Compose constructs. Kept apart from the classic ones because the two need different
    /// replacements - stringResource() versus getString() - and the adapter decides between them
    /// using the construct name recorded on the candidate.
    /// </summary>
    public static readonly HashSet<string> ComposeConstructs = new(StringComparer.Ordinal)
    {
        "Text", "text", "label", "placeholder", "contentDescription", "supportingText",
        "headlineContent", "overlineContent", "supportingContent", "title", "message", "hint",
        "description", "confirmButtonText", "dismissButtonText"
    };

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        // Comments are blanked, not removed, so match offsets stay valid against the raw file.
        var scrubbed = Blank(content, LineComment());
        scrubbed = Blank(scrubbed, BlockComment());

        var seen = new HashSet<int>();

        foreach (var match in FirstArgumentCall().Matches(scrubbed)
                     .Concat(SecondArgumentCall().Matches(scrubbed))
                     .Concat(NamedArgument().Matches(scrubbed)))
        {
            var literal = match.Groups["lit"];

            // Two patterns can cover the same literal; the first one to claim it wins.
            if (!seen.Add(literal.Index)) continue;

            var raw = content.Substring(literal.Index, literal.Length);
            var call = match.Groups["call"].Value;

            if (!IsLocalizable(raw, scrubbed, match.Index)) continue;

            var text = DecodeLiteral(raw);
            if (NoiseFilter.IsNoise(text)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = LineOf(content, literal.Index),
                SpanStart = literal.Index,
                SpanLength = literal.Length,
                Text = text,
                RawSpanText = raw,

                // Reusing the plain-code-literal kind: Kotlin and Java literals behave exactly like
                // it downstream, and the enum belongs to the shared model, not to this adapter.
                Kind = CandidateKind.CodeLiteral,
                Context = Snippet(content, match.Index, match.Length),

                // The UI construct, not the enclosing function: this is what PlanRewrite needs in
                // order to choose between a Compose and a classic replacement.
                Member = call
            };
        }
    }

    /// <summary>Rejects literals that a lookup cannot safely replace.</summary>
    private static bool IsLocalizable(string raw, string scrubbed, int matchIndex)
    {
        // Raw and multi-line strings: the span would not be a simple quoted literal.
        if (raw.Length < 2 || raw[0] != '"') return false;

        // A Kotlin template has runtime holes; swapping it for a static lookup would drop them.
        if (UnescapedTemplate().IsMatch(raw)) return false;

        // Diagnostics never reach a user. The allow-list already keeps Log.d(TAG, "...") out, but a
        // UI-shaped named argument can still appear inside a logging or analytics builder.
        var lineStart = scrubbed.LastIndexOf('\n', Math.Min(matchIndex, scrubbed.Length - 1)) + 1;
        var lineEnd = scrubbed.IndexOf('\n', matchIndex);
        if (lineEnd < 0) lineEnd = scrubbed.Length;
        var line = scrubbed[lineStart..lineEnd];

        if (Diagnostic().IsMatch(line)) return false;

        // Annotation arguments must be compile-time constants.
        return !line.TrimStart().StartsWith('@');
    }

    /// <summary>Replaces every match with spaces so later regexes keep the original offsets.</summary>
    private static string Blank(string content, Regex regex)
    {
        var sb = new StringBuilder(content);
        foreach (Match m in regex.Matches(content))
            for (var i = m.Index; i < m.Index + m.Length; i++)
                if (sb[i] is not ('\r' or '\n')) sb[i] = ' ';
        return sb.ToString();
    }

    /// <summary>Quoted literal to its runtime value; the same escapes in Kotlin and Java.</summary>
    private static string DecodeLiteral(string raw)
    {
        var body = raw.Length >= 2 ? raw[1..^1] : raw;
        var sb = new StringBuilder(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length)
            {
                sb.Append(body[i]);
                continue;
            }

            var next = body[++i];

            if (next == 'u' && i + 4 < body.Length &&
                int.TryParse(body.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb.Append((char)code);
                i += 4;
                continue;
            }

            sb.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                _ => next
            });
        }

        return sb.ToString();
    }

    private static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static string Snippet(string content, int index, int length)
    {
        var from = Math.Max(0, index - 40);
        var to = Math.Min(content.Length, index + length + 60);
        var s = content[from..to].Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }

    // A literal is "..." with backslash escapes and no embedded newline; Kotlin's """ blocks and
    // Java text blocks therefore never match, which is intended - their spans are not swappable.
    private const string Literal = @"""(?:\\.|[^""\\\n])*""";

    [GeneratedRegex(
        @"\b(?<call>setText|setTitle|setMessage|setHint|setContentDescription|setSubtitle|setSummary|" +
        @"setPositiveButton|setNegativeButton|setNeutralButton|setLabel|setError|setPrompt|setQueryHint|" +
        @"Text)\s*\(\s*(?<lit>" + Literal + ")")]
    private static partial Regex FirstArgumentCall();

    // Toast and Snackbar take the context or view first and the copy second. The first argument is
    // allowed to be a simple call such as requireContext(), but nothing more nested than that.
    [GeneratedRegex(
        @"\b(?<call>Toast\.makeText|Snackbar\.make)\s*\(\s*(?:[^,()""]|\([^()""]*\))*,\s*(?<lit>" + Literal + ")")]
    private static partial Regex SecondArgumentCall();

    // Kotlin named arguments (Compose) and property assignments (view.text = "...").
    [GeneratedRegex(
        // The lookbehind excludes identifiers that merely end in one of these words (someText = ...)
        // while still allowing the very common view property assignment (binding.title.text = ...).
        @"(?<!\w)(?<call>text|label|placeholder|contentDescription|supportingText|headlineContent|" +
        @"overlineContent|title|message|hint|description|confirmButtonText|dismissButtonText)\s*=\s*(?<lit>" +
        Literal + ")")]
    private static partial Regex NamedArgument();

    [GeneratedRegex(@"\b(Log\.[dewiv]|Timber\.[a-z]|println|print|System\.out\.print(ln)?|Crashlytics)\b")]
    private static partial Regex Diagnostic();

    // "$name" and "${expr}", but not the escaped "\$".
    [GeneratedRegex(@"(?<!\\)\$")]
    private static partial Regex UnescapedTemplate();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
