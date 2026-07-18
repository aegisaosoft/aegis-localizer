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

using System.Text.RegularExpressions;
using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;

namespace Aegis.Localizer.Platforms.Web;

/// <summary>
/// Plain HTML pages and Svelte components. Both are scanned for reporting only - a .html file has
/// no module system to import a lookup into, and Svelte's copy is worth listing long before the
/// tool is confident enough to edit it.
/// </summary>
public static partial class HtmlTextExtractor
{
    /// <param name="dialect">
    /// Reported as the construct name. Svelte markup is close enough to HTML to share the scan, but
    /// a reader of the report needs to know which of the two they are looking at.
    /// </param>
    public static IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, string dialect)
    {
        var scrubbed = WebSyntax.Blank(content, HtmlComment());
        scrubbed = WebSyntax.Blank(scrubbed, ScriptOrStyle());

        foreach (var c in Attributes(filePath, relativePath, content, scrubbed, dialect)) yield return c;
        foreach (var c in TextNodes(filePath, relativePath, content, scrubbed, dialect)) yield return c;
    }

    private static IEnumerable<StringCandidate> Attributes(
        string filePath, string relativePath, string content, string scrubbed, string dialect)
    {
        foreach (Match m in Attribute().Matches(scrubbed))
        {
            var name = m.Groups["name"].Value;
            if (!WebSyntax.IsCopyAttribute(name)) continue;
            if (!WebSyntax.InsideTag(scrubbed, m.Index)) continue;

            var g = m.Groups["value"];
            if (!WebSyntax.Intact(content, scrubbed, g.Index, g.Length)) continue;

            var inner = content.Substring(g.Index + 1, g.Length - 2);

            // A template expression left in an attribute is already dynamic, whatever produced it.
            if (inner.StartsWith('{')) continue;
            if (NoiseFilter.IsNoise(inner)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = WebSyntax.LineOf(content, g.Index),
                SpanStart = g.Index,
                SpanLength = g.Length,
                Text = WebSyntax.Decode(inner),
                RawSpanText = content.Substring(g.Index, g.Length),
                Kind = CandidateKind.MarkupAttribute,
                Context = WebSyntax.Snippet(content, m.Index, m.Length),
                Member = WebSyntax.Construct($"{dialect}Attribute", WebSyntax.ElementName(scrubbed, m.Index), name)
            };
        }
    }

    private static IEnumerable<StringCandidate> TextNodes(
        string filePath, string relativePath, string content, string scrubbed, string dialect)
    {
        foreach (Match m in TextNode().Matches(scrubbed))
        {
            var g = m.Groups["text"];
            var raw = content.Substring(g.Index, g.Length);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (NoiseFilter.IsNoise(trimmed)) continue;

            var element = WebSyntax.ElementName(scrubbed, m.Index);
            if (element is null) continue;

            var lead = raw.Length - raw.TrimStart().Length;
            if (!WebSyntax.Intact(content, scrubbed, g.Index + lead, trimmed.Length)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = WebSyntax.LineOf(content, g.Index + lead),
                SpanStart = g.Index + lead,
                SpanLength = trimmed.Length,
                Text = WebSyntax.Decode(trimmed),
                RawSpanText = trimmed,
                Kind = CandidateKind.MarkupText,
                Context = WebSyntax.Snippet(content, m.Index, m.Length),
                Member = WebSyntax.Construct($"{dialect}Text", element)
            };
        }
    }

    // Attribute names are restricted to plain identifiers, which drops Angular's [prop], (event)
    // and *directive forms along with Svelte's {shorthand} without needing to know either dialect.
    [GeneratedRegex("""(?<name>[A-Za-z_][\w:.-]*)\s*=\s*(?<value>"[^"\r\n]*"|'[^'\r\n]*')""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@">(?<text>[^<>{}]*?)<")]
    private static partial Regex TextNode();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();
}
