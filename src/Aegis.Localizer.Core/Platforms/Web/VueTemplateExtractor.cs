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
/// Vue single file components. The template block and the script block are scanned separately
/// because they are different languages with different rewrite rules, and mixing them would let a
/// mustache expression be treated as markup copy.
/// </summary>
public static partial class VueTemplateExtractor
{
    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var template = TemplateBlock().Match(content);
        if (template.Success)
        {
            var body = template.Groups["body"];
            var scrubbed = WebSyntax.BlankOutside(content, body.Index, body.Length);
            scrubbed = WebSyntax.Blank(scrubbed, HtmlComment());

            // Mustaches already carry an expression - usually $t(...) from an earlier run.
            scrubbed = WebSyntax.Blank(scrubbed, Mustache());

            foreach (var c in Attributes(filePath, relativePath, content, scrubbed)) yield return c;
            foreach (var c in TextNodes(filePath, relativePath, content, scrubbed)) yield return c;
        }

        var script = ScriptBlock().Match(content);
        if (!script.Success) yield break;

        var scriptBody = script.Groups["body"];
        var code = WebSyntax.BlankOutside(content, scriptBody.Index, scriptBody.Length);

        foreach (var c in WebSyntax.UiCallLiterals(filePath, relativePath, content, code))
            yield return c;
    }

    private static IEnumerable<StringCandidate> Attributes(
        string filePath, string relativePath, string content, string scrubbed)
    {
        foreach (Match m in Attribute().Matches(scrubbed))
        {
            var name = m.Groups["name"].Value;

            // ':', '@' and 'v-' all mean the value is an expression, not copy.
            if (name.StartsWith(':') || name.StartsWith('@') ||
                name.StartsWith("v-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!WebSyntax.IsCopyAttribute(name)) continue;

            var pair = m.Groups["pair"];
            if (!WebSyntax.Intact(content, scrubbed, pair.Index, pair.Length)) continue;

            var valueGroup = m.Groups["value"];
            var inner = content.Substring(valueGroup.Index + 1, valueGroup.Length - 2);
            if (NoiseFilter.IsNoise(inner)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = WebSyntax.LineOf(content, valueGroup.Index),
                // The span covers the attribute name too: a static attribute becomes a bound one,
                // so the rewrite has to be able to write the leading ':'.
                SpanStart = pair.Index,
                SpanLength = pair.Length,
                Text = WebSyntax.Decode(inner),
                RawSpanText = content.Substring(pair.Index, pair.Length),
                Kind = CandidateKind.MarkupAttribute,
                Context = WebSyntax.Snippet(content, m.Index, m.Length),
                Member = WebSyntax.Construct("VueAttribute", WebSyntax.ElementName(scrubbed, m.Index), name)
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
                Member = WebSyntax.Construct("VueText", element)
            };
        }
    }

    /// <summary>Attribute name of a Vue pair candidate, read back out of the captured span.</summary>
    public static string? AttributeName(string rawSpanText)
    {
        var eq = rawSpanText.IndexOf('=');
        if (eq <= 0) return null;
        var name = rawSpanText[..eq].Trim();
        return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? name : null;
    }

    // Greedy body so nested <template> tags in slots do not end the block early; an SFC has exactly
    // one outermost template.
    [GeneratedRegex(@"<template[^>]*>(?<body>.*)</template\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TemplateBlock();

    [GeneratedRegex(@"<script[^>]*>(?<body>.*?)</script\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@">(?<text>[^<>{}]*?)<")]
    private static partial Regex TextNode();

    [GeneratedRegex("""(?<pair>(?<name>[A-Za-z_@:][\w:.@-]*)\s*=\s*(?<value>"[^"\r\n]*"|'[^'\r\n]*'))""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex Mustache();
}
