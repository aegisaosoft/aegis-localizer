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

namespace Aegis.Localizer.Platforms.Web;

/// <summary>
/// Scanning primitives the three web extractors share: offset-preserving blanking, tag context
/// probes and the one construct that looks identical in every web dialect - a string literal handed
/// to a UI call such as alert() or toast.error().
/// </summary>
internal static partial class WebSyntax
{
    /// <summary>Replaces every match with spaces so later regexes keep the original offsets.</summary>
    public static string Blank(string content, Regex regex)
    {
        var sb = new StringBuilder(content);
        foreach (Match m in regex.Matches(content))
            for (var i = m.Index; i < m.Index + m.Length; i++)
                if (sb[i] is not ('\r' or '\n')) sb[i] = ' ';
        return sb.ToString();
    }

    /// <summary>Keeps only one region live, which is how a .vue block is scanned in isolation.</summary>
    public static string BlankOutside(string content, int start, int length)
    {
        var sb = new StringBuilder(content);
        for (var i = 0; i < sb.Length; i++)
        {
            if (i >= start && i < start + length) continue;
            if (sb[i] is not ('\r' or '\n')) sb[i] = ' ';
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the offset sits inside an open element tag. This is what separates a JSX attribute
    /// from a plain assignment: `title="Hi"` in markup and `title = "Hi"` in code are the same
    /// regex match, and only the surrounding tag tells them apart.
    /// </summary>
    public static bool InsideTag(string text, int index)
    {
        // Bounded walk: a tag header longer than this is malformed and not worth chasing.
        var limit = Math.Max(0, index - 4000);
        for (var i = Math.Min(index, text.Length) - 1; i >= limit; i--)
        {
            if (text[i] == '>') return false;
            if (text[i] == '<')
                return i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_');
        }
        return false;
    }

    /// <summary>
    /// True when a span came through blanking untouched. A text node that stretches across a
    /// comment, a script body or a &lt;Trans&gt; element still matches on the scrubbed text but reads
    /// back the original characters, and that mismatch is precisely what marks it as not copy.
    /// </summary>
    public static bool Intact(string content, string scrubbed, int start, int length) =>
        string.CompareOrdinal(content, start, scrubbed, start, length) == 0;

    public static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    /// <summary>Name of the element the match sits in, found by walking back to the nearest '&lt;'.</summary>
    public static string? ElementName(string content, int index)
    {
        var lt = content.LastIndexOf('<', Math.Min(index, content.Length - 1));
        if (lt < 0) return null;

        var i = lt + 1;
        if (i < content.Length && content[i] == '/') i++;
        var start = i;
        while (i < content.Length && (char.IsLetterOrDigit(content[i]) || content[i] is ':' or '.' or '-' or '_')) i++;
        return i > start ? content[start..i] : null;
    }

    /// <summary>
    /// Names the construct a candidate came from. The shared <see cref="CandidateKind"/> enum has no
    /// web members yet, so the precise dialect and element travel here instead - both the classifier
    /// prompt and the report read this field.
    /// </summary>
    public static string Construct(string dialect, string? element, string? attribute = null) =>
        (element, attribute) switch
        {
            (null, null) => dialect,
            (not null, null) => $"{dialect} <{element}>",
            (null, not null) => $"{dialect} {attribute}",
            _ => $"{dialect} {attribute} on <{element}>"
        };

    public static string Snippet(string content, int index, int length)
    {
        var from = Math.Max(0, index - 60);
        var to = Math.Min(content.Length, index + length + 60);
        var s = content[from..to].Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }

    public static string Decode(string s) => s
        .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
        .Replace("&nbsp;", " ");

    /// <summary>
    /// Attributes that carry copy in XAML but carry wiring on the web: `value` is a form value and
    /// `content` belongs to &lt;meta&gt;. Rewriting either changes behaviour, not language.
    /// </summary>
    private static readonly HashSet<string> NonCopyOnWeb = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "content"
    };

    public static bool IsCopyAttribute(string name) =>
        NoiseFilter.UiAttributes.Contains(name) && !NonCopyOnWeb.Contains(name);

    /// <summary>
    /// String literals passed to calls that put text on screen. Deliberately a short allow-list:
    /// guessing which of a project's own helpers are user-facing produces far more noise than it
    /// finds copy, and the model pays for every guess. `console.*` is absent on purpose.
    /// </summary>
    public static IEnumerable<StringCandidate> UiCallLiterals(
        string filePath, string relativePath, string content, string scrubbed)
    {
        foreach (Match m in UiCall().Matches(scrubbed))
        {
            var g = m.Groups["lit"];
            if (!Intact(content, scrubbed, g.Index, g.Length)) continue;

            var raw = content.Substring(g.Index, g.Length);
            var inner = raw[1..^1];
            if (NoiseFilter.IsNoise(inner)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = LineOf(content, g.Index),
                // The span covers the quotes so the whole literal becomes the t(...) call.
                SpanStart = g.Index,
                SpanLength = g.Length,
                Text = inner,
                RawSpanText = raw,
                Kind = CandidateKind.CodeLiteral,
                Context = Snippet(content, m.Index, m.Length),
                Member = Construct("JsUiCall", null, m.Groups["fn"].Value)
            };
        }
    }

    // Literals containing escapes are skipped rather than half-decoded; they are rare in copy and a
    // wrong decode would fail the rewriter's span check anyway.
    [GeneratedRegex("""
        (?<![\w$])(?<fn>(?:window\.)?(?:alert|confirm|prompt)
        |(?:toast|message|notification|notify)(?:\.(?:success|error|info|warn|warning|loading|open))?
        |showToast|showMessage|enqueueSnackbar|Alert\.alert)
        \s*\(\s*(?<lit>"[^"\\\r\n]*"|'[^'\\\r\n]*')
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex UiCall();
}
