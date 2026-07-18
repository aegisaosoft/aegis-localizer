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

namespace Aegis.Localizer.Resources;

/// <summary>
/// Apple bundles: &lt;dir&gt;/&lt;culture&gt;.lproj/Localizable.strings, one `"key" = "value";` per line.
///
/// Unlike .resx there is no neutral file - the source language gets its own .lproj like every other
/// language, so a null culture is written as the default source code rather than to a special file.
/// </summary>
public sealed partial class AppleStringsStore : IResourceStore
{
    /// <summary>
    /// Culture used when the caller asks for the neutral bundle. Apple has no neutral layout, and
    /// <see cref="ResourceLocation"/> carries no source language, so this mirrors the tool's default.
    /// </summary>
    private const string NeutralCulture = "en";

    public ResourceFormat Format => ResourceFormat.AppleStrings;

    /// <summary>
    /// The table name is fixed rather than taken from <see cref="ResourceLocation.BaseName"/>:
    /// the lookups we generate (String(localized:) / NSLocalizedString(key, nil)) read the default
    /// `Localizable` table, and a differently named file would simply never be consulted.
    /// </summary>
    public string ResolvePath(ResourceLocation location) => Path.Combine(
        location.Directory,
        $"{location.EffectiveCulture}.lproj",
        "Localizable.strings");

    public IReadOnlyDictionary<string, string> Read(ResourceLocation location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolvePath(location);
        if (!File.Exists(path)) return result;

        try
        {
            foreach (var entry in Parse(File.ReadAllText(path)).Entries)
                result[entry.Key] = entry.Value;
        }
        catch (IOException)
        {
            // Unreadable bundle: treated as empty rather than aborting the run.
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

        var (entries, trailing) = File.Exists(path)
            ? Parse(File.ReadAllText(path))
            : (new List<Entry>(), new List<string>());

        // First occurrence wins, which is also how Foundation resolves a duplicated key.
        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            byKey.TryAdd(entry.Key, entry);

        var added = 0;
        var updated = 0;

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (byKey.TryGetValue(key, out var existing))
            {
                if (existing.Value == value) continue;
                existing.Value = value;
                updated++;
                continue;
            }

            var entry = new Entry { Key = key, Value = value };

            if (comments is not null &&
                comments.TryGetValue(key, out var comment) &&
                !string.IsNullOrWhiteSpace(comment))
            {
                // Block comment above the pair is the convention genstrings itself emits.
                entry.Lead.Add($"/* {comment.Replace("*/", "* /", StringComparison.Ordinal)} */");
            }

            entries.Add(entry);
            byKey[key] = entry;
            added++;
        }

        var sb = new StringBuilder();

        foreach (var entry in entries)
        {
            foreach (var lead in entry.Lead) sb.Append(lead).Append('\n');
            sb.Append('"').Append(Escape(entry.Key)).Append("\" = \"").Append(Escape(entry.Value)).Append("\";\n");
        }

        foreach (var line in trailing) sb.Append(line).Append('\n');

        // Modern tooling reads .strings as UTF-8; a BOM would show up as a stray key in Xcode.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        return new ResourceWriteResult(path, added, updated);
    }

    /// <summary>One key/value pair plus the raw lines that preceded it, so comments survive a merge.</summary>
    private sealed class Entry
    {
        public required string Key { get; init; }

        public required string Value { get; set; }

        public List<string> Lead { get; } = [];
    }

    /// <summary>
    /// Line-oriented parse. Anything that is not a recognisable pair - comments, blank lines, a
    /// construct we do not model - is carried along verbatim and re-emitted, so a translator's notes
    /// are never silently dropped by a merge.
    /// </summary>
    private static (List<Entry> Entries, List<string> Trailing) Parse(string text)
    {
        var entries = new List<Entry>();
        var pending = new List<string>();
        var inBlockComment = false;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // A file ending in a newline yields a final empty element. Carrying it into `trailing` and
        // re-emitting it with its own newline appends one blank line per run, for ever.
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (inBlockComment)
            {
                pending.Add(line);
                if (line.Contains("*/", StringComparison.Ordinal)) inBlockComment = false;
                continue;
            }

            var match = EntryLine().Match(line);
            if (match.Success)
            {
                var entry = new Entry
                {
                    Key = Unescape(match.Groups["k"].Value),
                    Value = Unescape(match.Groups["v"].Value)
                };

                entry.Lead.AddRange(pending);
                pending.Clear();
                entries.Add(entry);
                continue;
            }

            // A block comment that does not close on its own line swallows the lines that follow.
            if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal) &&
                !line.Contains("*/", StringComparison.Ordinal))
                inBlockComment = true;

            pending.Add(line);
        }

        // Trailing lines have no entry to attach to; they are appended after everything else.
        return (entries, pending);
    }

    private static string Escape(string value) => new StringBuilder(value)
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n")
        .Replace("\t", "\\t")
        .ToString();

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;

        var sb = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            sb.Append(value[i] switch
            {
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                '0' => "\0",
                _ => value[i].ToString()
            });
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*""(?<k>(?:[^""\\]|\\.)*)""\s*=\s*""(?<v>(?:[^""\\]|\\.)*)""\s*;")]
    private static partial Regex EntryLine();
}
