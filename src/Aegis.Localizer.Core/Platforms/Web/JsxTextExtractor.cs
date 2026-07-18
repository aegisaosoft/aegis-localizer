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
/// React sources: JSX text nodes, JSX attributes that hold copy, and UI calls in the surrounding
/// TypeScript. Regex based like the .NET markup extractor - a real parser buys nothing here, since
/// half the files in a live app fail to parse the moment a syntax proposal lands in the toolchain.
/// </summary>
public static partial class JsxTextExtractor
{
    /// <param name="markup">
    /// False for plain .ts / .js, where a `>` is a comparison rather than a tag and scanning for
    /// text nodes would invent candidates out of arithmetic.
    /// </param>
    public static IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, bool markup)
    {
        var scrubbed = WebSyntax.Blank(content, BlockComment());
        scrubbed = WebSyntax.Blank(scrubbed, LineComment());
        scrubbed = WebSyntax.Blank(scrubbed, ImportStatement());
        scrubbed = WebSyntax.Blank(scrubbed, ReExportStatement());
        scrubbed = WebSyntax.Blank(scrubbed, ModuleCall());

        // <Trans> children are already localized markup; leaving them visible would make a second
        // run re-extract everything the first run wrapped.
        scrubbed = WebSyntax.Blank(scrubbed, TransElement());

        if (markup)
        {
            foreach (var c in Attributes(filePath, relativePath, content, scrubbed)) yield return c;
            foreach (var c in TextNodes(filePath, relativePath, content, scrubbed)) yield return c;
        }

        foreach (var c in WebSyntax.UiCallLiterals(filePath, relativePath, content, scrubbed))
            yield return c;
    }

    private static IEnumerable<StringCandidate> Attributes(
        string filePath, string relativePath, string content, string scrubbed)
    {
        foreach (Match m in Attribute().Matches(scrubbed))
        {
            var name = m.Groups["name"].Value;
            if (!WebSyntax.IsCopyAttribute(name)) continue;
            if (!WebSyntax.InsideTag(scrubbed, m.Index)) continue;

            var g = m.Groups["value"];
            if (!WebSyntax.Intact(content, scrubbed, g.Index, g.Length)) continue;

            var raw = content.Substring(g.Index, g.Length);
            var inner = raw[1..^1];
            if (NoiseFilter.IsNoise(inner)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = WebSyntax.LineOf(content, g.Index),
                // Quotes are inside the span: JSX replaces the whole quoted value with {t("Key")},
                // braces and all, so leaving the quotes behind would produce "{t(...)}" as text.
                SpanStart = g.Index,
                SpanLength = g.Length,
                Text = WebSyntax.Decode(inner),
                RawSpanText = raw,
                Kind = CandidateKind.MarkupAttribute,
                Context = WebSyntax.Snippet(content, m.Index, m.Length),
                Member = WebSyntax.Construct("JsxAttribute", WebSyntax.ElementName(scrubbed, m.Index), name)
            };
        }
    }

    private static IEnumerable<StringCandidate> TextNodes(
        string filePath, string relativePath, string content, string scrubbed)
    {
        foreach (Match m in TextNode().Matches(scrubbed))
        {
            var g = m.Groups["text"];
            var raw = content.Substring(g.Index, g.Length);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (NoiseFilter.IsNoise(trimmed)) continue;

            // No enclosing element means the `>` and `<` came from an expression, not from markup.
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
                Member = WebSyntax.Construct("JsxText", element)
            };
        }
    }

    // Braces are excluded from the text class, which is also what makes the pass idempotent: once a
    // node reads {t("Key")} it no longer looks like a text node.
    [GeneratedRegex(@">(?<text>[^<>{}]*?)<")]
    private static partial Regex TextNode();

    [GeneratedRegex("""(?<name>[A-Za-z_][\w:.-]*)\s*=\s*(?<value>"[^"\r\n]*"|'[^'\r\n]*')""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    // The lookbehind keeps "https://..." inside a string from swallowing the rest of its line.
    [GeneratedRegex(@"(?<![:\w])//[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^[ \t]*import\s[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex ImportStatement();

    [GeneratedRegex("""^[ \t]*export\s[^\r\n]*?\sfrom\s*(?<q>["'])[^"'\r\n]*\k<q>[^\r\n]*""", RegexOptions.Multiline)]
    private static partial Regex ReExportStatement();

    [GeneratedRegex("""(?:require|import)\s*\(\s*(?<q>["'])[^"'\r\n]*\k<q>\s*\)""")]
    private static partial Regex ModuleCall();

    [GeneratedRegex(@"<Trans\b[^>]*>.*?</Trans\s*>", RegexOptions.Singleline)]
    private static partial Regex TransElement();
}
