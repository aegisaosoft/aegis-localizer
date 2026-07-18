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

namespace Aegis.Localizer.Platforms.Apple;

/// <summary>
/// Storyboard and XIB copy. Report-only by design: Interface Builder files are localized through
/// their own pipeline (a Base.lproj plus per-language .strings keyed by object id, produced by
/// ibtool), so the useful output here is telling the user which screens still hold hardcoded copy,
/// not editing the XML. See <see cref="AppleAdapter.PlanRewrite"/>, which returns null for these.
///
/// Attributes only - the free text nodes in these files are mostly serialized state, and a scan
/// that reports them would bury the real findings.
/// </summary>
public static partial class InterfaceBuilderExtractor
{
    /// <summary>Interface Builder attributes that hold copy.</summary>
    private static readonly HashSet<string> UiAttributes = new(StringComparer.Ordinal)
    {
        "text", "title", "placeholder", "prompt", "headerTitle", "footerTitle", "normalTitle"
    };

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var scrubbed = Blank(content, XmlComment());

        foreach (Match match in AttributePair().Matches(scrubbed))
        {
            var name = match.Groups["name"].Value;
            if (!UiAttributes.Contains(name)) continue;

            var group = match.Groups["value"];
            var value = content.Substring(group.Index, group.Length);
            if (NoiseFilter.IsNoise(value)) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = CodeContext.LineOf(content, group.Index),
                SpanStart = group.Index,
                SpanLength = group.Length,
                Text = Decode(value),
                RawSpanText = value,
                // Reusing the markup-attribute kind: the enum names it after XAML, but it means
                // "value of a markup attribute", which is exactly what this is.
                Kind = CandidateKind.MarkupAttribute,
                Context = CodeContext.Snippet(content, match.Index, match.Length),
                Member = ElementName(scrubbed, match.Index)
            };
        }
    }

    /// <summary>Replaces every match with spaces so the later regex keeps the original offsets.</summary>
    private static string Blank(string content, Regex regex)
    {
        var sb = new StringBuilder(content);

        foreach (Match match in regex.Matches(content))
            for (var i = match.Index; i < match.Index + match.Length; i++)
                CodeContext.Blank(sb, i);

        return sb.ToString();
    }

    private static string Decode(string s) => s
        .Replace("&amp;", "&", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&#39;", "'", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal);

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

    [GeneratedRegex(@"(?<name>[A-Za-z_][\w.:-]*)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex AttributePair();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
