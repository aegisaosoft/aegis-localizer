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
/// Copy held in Android resource XML: layouts, menus, navigation graphs and preference screens.
/// Regex based like the .NET markup extractor, because these files are hand-edited and a tolerant
/// scan beats a parser that gives up on the first malformed fragment - and because a candidate
/// needs an exact offset into the raw text, which a DOM does not give us.
/// </summary>
public static partial class AndroidLayoutExtractor
{
    /// <summary>
    /// Attribute local names that carry user-visible copy. Checked without the namespace prefix so
    /// android:text and app:title are treated the same way.
    /// </summary>
    private static readonly HashSet<string> UiAttributes = new(StringComparer.Ordinal)
    {
        "text", "hint", "contentDescription", "title", "subtitle", "label", "summary", "summaryOn",
        "summaryOff", "dialogTitle", "dialogMessage", "tooltipText", "textOn", "textOff",
        "helperText", "placeholderText", "prefixText", "suffixText", "errorText", "message",
        "queryHint", "startIconContentDescription", "endIconContentDescription",
        "navigationContentDescription"
    };

    /// <summary>
    /// Namespaces whose values never reach a user: tools: is design-time preview data that Android
    /// Studio strips from the built APK, so translating it would be pure noise.
    /// </summary>
    private static readonly HashSet<string> IgnoredPrefixes = new(StringComparer.Ordinal) { "tools" };

    /// <summary>Folders under res/ whose XML holds strings a user can read.</summary>
    private static readonly string[] CopyBearingFolders = ["layout", "menu", "navigation", "xml"];

    /// <summary>
    /// True when this .xml file is worth scanning. The extension alone is far too broad on Android:
    /// the same scan sees AndroidManifest.xml, gradle config, CI descriptors and - worst of all -
    /// the strings.xml files this tool writes itself, which would be re-extracted on every run.
    /// </summary>
    public static bool IsCopyBearing(string filePath, string content)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Never read back our own output, in any locale variant.
        foreach (var segment in segments)
            if (segment.StartsWith("values", StringComparison.OrdinalIgnoreCase))
                return false;

        if (Path.GetFileName(filePath).Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase))
            return false;

        var folder = segments.Length >= 2 ? segments[^2] : string.Empty;
        if (CopyBearingFolders.Any(f => folder.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            return true;

        // A file outside the conventional folders still counts when it is Android UI markup, which
        // the platform namespace declaration identifies unambiguously.
        if (!content.Contains("schemas.android.com/apk/res/android", StringComparison.Ordinal)) return false;

        var root = RootElement(content);
        return root is not null &&
               !root.Equals("manifest", StringComparison.Ordinal) &&
               !root.Equals("resources", StringComparison.Ordinal);
    }

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        // Comments are blanked rather than removed so every later offset still points into content.
        var scrubbed = Blank(content, XmlComment());

        foreach (Match m in AttributePair().Matches(scrubbed))
        {
            var rawName = m.Groups["name"].Value;
            var colon = rawName.IndexOf(':');
            var prefix = colon < 0 ? string.Empty : rawName[..colon];
            var localName = colon < 0 ? rawName : rawName[(colon + 1)..];

            if (IgnoredPrefixes.Contains(prefix)) continue;
            if (!UiAttributes.Contains(localName)) continue;

            var valueGroup = m.Groups["value"];
            var value = content.Substring(valueGroup.Index, valueGroup.Length);

            // "@string/..." is already localized and "?attr/..." is a theme lookup. Skipping every
            // value that starts with either is what makes a second --apply run a no-op instead of a
            // second layer of indirection.
            if (value.StartsWith('@') || value.StartsWith('?')) continue;
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

                // Reusing the XAML kind: to everything downstream this is what it is, the value of a
                // markup attribute, and the adapter's own PlanRewrite is what interprets it.
                Kind = CandidateKind.MarkupAttribute,
                Context = Snippet(content, m.Index, m.Length),
                Member = ElementName(scrubbed, m.Index)
            };
        }
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

    private static string? RootElement(string content)
    {
        var match = FirstElement().Match(content);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string Decode(string s) => s
        .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
        .Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\n", "\n");

    private static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    /// <summary>Name of the element the attribute sits in, found by walking back to the nearest '&lt;'.</summary>
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

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [GeneratedRegex(@"<\s*(?<name>[A-Za-z_][\w.:-]*)", RegexOptions.None)]
    private static partial Regex FirstElement();
}
