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

namespace Aegis.Localizer.Platforms.DotNet;

/// <summary>
/// Text extraction for the markup half of a .NET app: XAML (WPF / MAUI) and Razor
/// (.cshtml / .razor). Regex based on purpose - these files are edited by hand and a tolerant
/// scan beats a strict parser that gives up on the first malformed fragment.
/// </summary>
public static partial class MarkupExtractor
{
    public static IEnumerable<StringCandidate> ExtractXaml(string filePath, string relativePath, string content)
    {
        var scrubbed = Blank(content, XmlComment());
        foreach (var c in Attributes(filePath, relativePath, content, scrubbed, CandidateKind.XamlAttribute))
            yield return c;
        foreach (var c in TextNodes(filePath, relativePath, content, scrubbed, CandidateKind.XamlText, xaml: true))
            yield return c;
    }

    public static IEnumerable<StringCandidate> ExtractRazor(string filePath, string relativePath, string content)
    {
        // Code islands and script/style bodies are not markup copy; blank them but keep offsets.
        var scrubbed = Blank(content, XmlComment());
        scrubbed = Blank(scrubbed, ScriptOrStyle());
        scrubbed = Blank(scrubbed, RazorCodeBlock());

        foreach (var c in Attributes(filePath, relativePath, content, scrubbed, CandidateKind.RazorAttribute))
            yield return c;
        foreach (var c in TextNodes(filePath, relativePath, content, scrubbed, CandidateKind.RazorText, xaml: false))
            yield return c;
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

    private static IEnumerable<StringCandidate> Attributes(
        string filePath, string relativePath, string content, string scrubbed, CandidateKind kind)
    {
        foreach (Match m in AttributePair().Matches(scrubbed))
        {
            var rawName = m.Groups["name"].Value;
            var localName = rawName.Contains(':') ? rawName[(rawName.IndexOf(':') + 1)..] : rawName;
            if (!NoiseFilter.UiAttributes.Contains(localName)) continue;

            var valueGroup = m.Groups["value"];
            var value = content.Substring(valueGroup.Index, valueGroup.Length);

            // Bindings, Razor expressions and resource lookups are already dynamic.
            if (value.StartsWith('{') || value.StartsWith('@')) continue;
            if (NoiseFilter.IsNoise(value)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = LineOf(content, valueGroup.Index),
                SpanStart = valueGroup.Index,
                SpanLength = valueGroup.Length,
                Text = Decode(value),
                RawSpanText = value,
                Kind = kind,
                Context = Snippet(content, m.Index, m.Length),
                Member = ElementName(scrubbed, m.Index)
            };
        }
    }

    private static IEnumerable<StringCandidate> TextNodes(
        string filePath, string relativePath, string content, string scrubbed, CandidateKind kind, bool xaml)
    {
        foreach (Match m in TextNode().Matches(scrubbed))
        {
            var g = m.Groups["text"];
            var raw = content.Substring(g.Index, g.Length);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            // Razor expressions, XAML markup extensions and directives are not static copy.
            if (trimmed.StartsWith('@') || trimmed.StartsWith('{')) continue;
            if (trimmed.Contains('@') && !xaml) continue;
            if (NoiseFilter.IsNoise(trimmed)) continue;

            // Narrow the span to the trimmed text so surrounding whitespace survives a rewrite.
            var lead = raw.Length - raw.TrimStart().Length;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = LineOf(content, g.Index + lead),
                SpanStart = g.Index + lead,
                SpanLength = trimmed.Length,
                Text = Decode(trimmed),
                RawSpanText = trimmed,
                Kind = kind,
                Context = Snippet(content, m.Index, m.Length),
                Member = ElementName(scrubbed, m.Index)
            };
        }
    }

    private static string Decode(string s) => s
        .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'");

    private static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    /// <summary>Name of the element the match sits in, found by walking back to the nearest '&lt;'.</summary>
    private static string? ElementName(string content, int index)
    {
        var lt = content.LastIndexOf('<', Math.Min(index, content.Length - 1));
        if (lt < 0) return null;

        var i = lt + 1;
        if (i < content.Length && content[i] == '/') i++;
        var start = i;
        while (i < content.Length && (char.IsLetterOrDigit(content[i]) || content[i] is ':' or '.' or '-' or '_')) i++;
        return i > start ? content[start..i] : null;
    }

    private static string Snippet(string content, int index, int length)
    {
        var from = Math.Max(0, index - 60);
        var to = Math.Min(content.Length, index + length + 60);
        var s = content[from..to].Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Trim();
    }

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w.:-]*)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex AttributePair();

    [GeneratedRegex(@">(?<text>[^<>{}]*?)<")]
    private static partial Regex TextNode();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"@(code|functions)\s*\{.*?\n\}", RegexOptions.Singleline)]
    private static partial Regex RazorCodeBlock();
}
