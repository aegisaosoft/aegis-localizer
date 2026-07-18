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
using System.Xml;
using System.Xml.Linq;

namespace Aegis.Localizer.Resources;

/// <summary>
/// Android string resources: res/values/strings.xml for the source language, res/values-ru/strings.xml
/// and friends for the rest.
///
/// Two things make this format less trivial than it looks. The folder name is a resource qualifier
/// with its own grammar (not a culture name), and the value is not plain XML text - aapt2 applies a
/// second, backslash-based escaping layer on top of XML. Both are handled here so that a value
/// survives a write/read round trip unchanged.
/// </summary>
public sealed partial class AndroidXmlStore : IResourceStore
{
    /// <summary>
    /// Android looks up strings by file position, not file name: any file in values/ is merged into
    /// the same table. strings.xml is the universal convention, so the bundle base name is ignored
    /// rather than producing a file no Android developer expects to find.
    /// </summary>
    private const string FileName = "strings.xml";

    public ResourceFormat Format => ResourceFormat.AndroidXml;

    public string ResolvePath(ResourceLocation location) =>
        Path.Combine(location.Directory, ValuesFolder(location.Culture), FileName);

    /// <summary>
    /// Culture name to resource qualifier. `ru` becomes values-ru and `pt-BR` becomes values-pt-rBR:
    /// the region needs the `r` prefix, which is the single most common mistake in hand-written
    /// Android localization. Anything the legacy two-letter grammar cannot express - a script
    /// subtag such as zh-Hans, or a three-letter language - has to use the BCP-47 `b+` form
    /// instead, which aapt2 accepts and the platform honours from API 24 on.
    /// </summary>
    public static string ValuesFolder(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "values";

        var parts = culture.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "values";

        var language = parts[0].ToLowerInvariant();

        if (parts.Length == 1 && language.Length == 2)
            return $"values-{language}";

        if (parts.Length == 2 && language.Length == 2 && IsRegion(parts[1]))
            return $"values-{language}-r{parts[1].ToUpperInvariant()}";

        var subtags = parts.Skip(1).Select(CanonicalSubtag);
        return "values-b+" + string.Join('+', new[] { language }.Concat(subtags));
    }

    /// <summary>Region subtags are two letters (ISO 3166-1) or three digits (UN M.49).</summary>
    private static bool IsRegion(string subtag) =>
        (subtag.Length == 2 && subtag.All(char.IsAsciiLetter)) ||
        (subtag.Length == 3 && subtag.All(char.IsAsciiDigit));

    /// <summary>BCP-47 casing: Titlecase scripts, uppercase regions, lowercase everything else.</summary>
    private static string CanonicalSubtag(string subtag)
    {
        if (subtag.Length == 4 && subtag.All(char.IsAsciiLetter))
            return char.ToUpperInvariant(subtag[0]) + subtag[1..].ToLowerInvariant();

        return IsRegion(subtag) ? subtag.ToUpperInvariant() : subtag.ToLowerInvariant();
    }

    /// <summary>
    /// Turns a PascalCase key into an Android resource name. Resource names become fields on the
    /// generated R class, so they must be legal Java identifiers, and the ecosystem writes them
    /// lower_snake_case. Already-snake names pass through unchanged, which keeps a second run
    /// idempotent.
    /// </summary>
    public static string ResourceName(string key)
    {
        var sb = new StringBuilder(key.Length + 4);

        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];

            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                continue;
            }

            // A capital starts a new word when it follows a lower-case run, or when it is the last
            // capital of an acronym ("HTTPServer" -> http_server).
            if (char.IsUpper(c) && sb.Length > 0 && sb[^1] != '_')
            {
                var previous = key[i - 1];
                var startsWord = char.IsLower(previous) || char.IsDigit(previous) ||
                                 (i + 1 < key.Length && char.IsLower(key[i + 1]));
                if (startsWord) sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        var name = sb.ToString().Trim('_');
        if (name.Length == 0) return "text";
        if (char.IsDigit(name[0])) name = "s_" + name;

        // A resource name that collides with a Java keyword breaks the generated R class.
        return JavaKeywords.Contains(name) ? name + "_text" : name;
    }

    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float",
        "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long", "native",
        "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp",
        "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void",
        "volatile", "while", "true", "false", "null"
    };

    public IReadOnlyDictionary<string, string> Read(ResourceLocation location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolvePath(location);
        if (!File.Exists(path)) return result;

        try
        {
            var root = XDocument.Load(path).Root;
            if (root is null) return result;

            foreach (var element in root.Elements("string"))
            {
                var name = element.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                result[name] = Unescape(element.Value);
            }
        }
        catch (XmlException)
        {
            // A broken bundle is rebuilt rather than aborting the run.
        }

        return result;
    }

    public ResourceWriteResult Write(
        ResourceLocation location,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? comments = null)
    {
        var path = ResolvePath(location);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var doc = File.Exists(path) ? SafeLoad(path) : XDocument.Parse(Skeleton);
        var root = doc.Root ?? XDocument.Parse(Skeleton).Root!;

        var existing = root.Elements("string")
            .Where(e => e.Attribute("name") is not null)
            .GroupBy(e => e.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var added = 0;
        var updated = 0;

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // The key arrives already in the ecosystem's convention: AndroidAdapter.NormalizeKey
            // owns that. Renaming again here would let Read return names Write was never given, and
            // the runner reads the bundle back to decide which keys already exist.
            var name = key;
            var escaped = Escape(value);

            if (existing.TryGetValue(name, out var element))
            {
                if (element.Value != escaped)
                {
                    element.ReplaceNodes(new XText(escaped));
                    updated++;
                }

                ApplyFormattedAttribute(element, value);
                continue;
            }

            var created = new XElement("string", new XAttribute("name", name), escaped);
            ApplyFormattedAttribute(created, value);

            // Source location as a comment, on first write only, so re-runs do not stack comments.
            if (comments is not null && comments.TryGetValue(key, out var comment) &&
                !string.IsNullOrWhiteSpace(comment))
                root.Add(new XComment($" {comment} "));

            root.Add(created);
            added++;
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = new UTF8Encoding(false),
            NewLineChars = "\n"
        };

        using (var writer = XmlWriter.Create(path, settings))
            doc.Save(writer);

        return new ResourceWriteResult(path, added, updated);
    }

    /// <summary>
    /// aapt2 rejects a string that mixes several un-indexed positional arguments unless the string
    /// is explicitly declared unformatted, so the flag follows the value on every write.
    /// </summary>
    private static void ApplyFormattedAttribute(XElement element, string value)
    {
        var attribute = element.Attribute("formatted");

        if (HasMultipleUnindexedArguments(value))
            element.SetAttributeValue("formatted", "false");
        else if (attribute is not null && attribute.Value == "false")
            attribute.Remove();
    }

    private static bool HasMultipleUnindexedArguments(string value)
    {
        // "%%" is an escaped percent sign, never an argument; drop it before counting.
        var scrubbed = value.Replace("%%", string.Empty);

        var count = FormatSpecifier().Matches(scrubbed).Count(m => !m.Groups["index"].Success);
        return count > 1;
    }

    /// <summary>
    /// Applies Android's escaping layer. XML metacharacters are deliberately not touched here:
    /// XmlWriter turns &amp;, &lt; and &gt; into entities when the element value is written, and
    /// escaping them twice would show the entity text to the user.
    /// </summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            switch (c)
            {
                // The backslash goes first, otherwise it would escape the escapes added below.
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': break;                       // CR is not representable and aapt2 drops it
                default: sb.Append(c); break;
            }
        }

        // '@' and '?' only mean "resource reference" and "theme attribute" in the leading position.
        if (sb.Length > 0 && sb[0] is '@' or '?') sb.Insert(0, '\\');

        return sb.ToString();
    }

    private static string Unescape(string raw)
    {
        var s = raw;

        // Wrapping a value in double quotes is Android's way of preserving leading or trailing
        // whitespace; the quotes are markup, not content. Our own writer escapes quotes instead, so
        // an unescaped pair here can only come from a hand-written file.
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];

        var sb = new StringBuilder(s.Length);

        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            var next = s[++i];

            if (next == 'u' && i + 4 < s.Length &&
                int.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                sb.Append((char)code);
                i += 4;
                continue;
            }

            sb.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                _ => next            // \\ \' \" \@ \? all decode to the character itself
            });
        }

        return sb.ToString();
    }

    private static XDocument SafeLoad(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (XmlException)
        {
            return XDocument.Parse(Skeleton);
        }
    }

    private const string Skeleton =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <resources />
        """;

    [GeneratedRegex(@"%(?<index>\d+\$)?[-#+ 0,(]*\d*(\.\d+)?[a-zA-Z]")]
    private static partial Regex FormatSpecifier();
}
