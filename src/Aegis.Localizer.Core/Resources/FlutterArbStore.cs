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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aegis.Localizer.Resources;

/// <summary>
/// Flutter ARB bundles: &lt;dir&gt;/app_&lt;locale&gt;.arb, the layout `flutter gen_l10n` expects.
///
/// ARB is JSON where a key holds the copy and the matching `@key` object holds its metadata, so a
/// merge has to preserve those metadata blocks: they carry the placeholder declarations that decide
/// what signature the generated Dart accessor gets.
/// </summary>
public sealed class FlutterArbStore : IResourceStore
{
    /// <summary>Locale used for the neutral bundle; ARB has no neutral file, every bundle is a locale.</summary>
    private const string NeutralCulture = "en";

    public ResourceFormat Format => ResourceFormat.FlutterArb;

    /// <summary>
    /// The `app_` prefix is the arb-file-template default; <see cref="ResourceLocation.BaseName"/> is
    /// not used because gen_l10n matches files by that template, not by our bundle name.
    /// </summary>
    public string ResolvePath(ResourceLocation location) =>
        Path.Combine(location.Directory, $"app_{NormalizeLocale(location.EffectiveCulture)}.arb");

    public IReadOnlyDictionary<string, string> Read(ResourceLocation location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolvePath(location);
        if (!File.Exists(path)) return result;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return result;

            foreach (var (name, node) in root)
            {
                // '@' prefixes metadata ("@key") and globals ("@@locale"); neither is copy.
                if (name.StartsWith('@')) continue;
                if (node is JsonValue value && value.TryGetValue<string>(out var text)) result[name] = text;
            }
        }
        catch (JsonException)
        {
            // Malformed bundle: rebuilt rather than aborting the run.
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

        var locale = NormalizeLocale(location.EffectiveCulture);
        var (globals, metadata, strings) = Load(path);

        var added = 0;
        var updated = 0;
        var fresh = new List<string>();

        foreach (var (key, value) in values)
        {
            if (!strings.TryGetValue(key, out var current))
            {
                added++;
                fresh.Add(key);
            }
            else if (current != value) updated++;
            else continue;

            strings[key] = value;
        }

        // The source reference goes into the metadata block, which is where gen_l10n and every ARB
        // editor look for a translator note. Written for new keys only: rewriting it on every run
        // would churn the file, because inserting the import line shifts every line below it.
        if (comments is not null)
        {
            foreach (var key in fresh)
            {
                if (!comments.TryGetValue(key, out var comment) || string.IsNullOrWhiteSpace(comment)) continue;

                if (!metadata.TryGetValue(key, out var block))
                {
                    block = new JsonObject();
                    metadata[key] = block;
                }

                // Never clobber a description a translator wrote by hand.
                block["description"] ??= comment;
            }
        }

        var root = new JsonObject { ["@@locale"] = locale };

        foreach (var (name, node) in globals)
            if (name != "@@locale")
                root[name] = node;

        // Sorted output keeps diffs readable when several people run the tool.
        foreach (var key in strings.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            root[key] = strings[key];
            if (metadata.TryGetValue(key, out var block) && block.Count > 0) root["@" + key] = block;
        }

        // Metadata whose key is gone stays put: it may belong to a bundle entry a person removed by
        // hand and intends to restore, and dropping it silently would lose their placeholder work.
        foreach (var (key, block) in metadata)
            if (!strings.ContainsKey(key) && block.Count > 0)
                root["@" + key] = block;

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep real characters rather than \uXXXX so the bundles stay human-readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(path, json + "\n", new UTF8Encoding(false));

        return new ResourceWriteResult(path, added, updated);
    }

    /// <summary>Splits an existing bundle into its three parts: globals, per-key metadata and copy.</summary>
    private static (
        List<KeyValuePair<string, JsonNode?>> Globals,
        Dictionary<string, JsonObject> Metadata,
        Dictionary<string, string> Strings) Load(string path)
    {
        var globals = new List<KeyValuePair<string, JsonNode?>>();
        var metadata = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path)) return (globals, metadata, strings);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return (globals, metadata, strings);
        }

        if (root is null) return (globals, metadata, strings);

        foreach (var (name, node) in root)
        {
            // Nodes are cloned because a JsonNode cannot be attached to a second parent.
            if (name.StartsWith("@@", StringComparison.Ordinal))
            {
                globals.Add(new KeyValuePair<string, JsonNode?>(name, node?.DeepClone()));
                continue;
            }

            if (name.StartsWith('@'))
            {
                if (node is JsonObject block) metadata[name[1..]] = (JsonObject)block.DeepClone();
                continue;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out var text)) strings[name] = text;
        }

        return (globals, metadata, strings);
    }

    /// <summary>
    /// Culture name to the ARB/intl spelling: underscore separated, language lower-case, script in
    /// title case and region upper-case - "pt-br" and "pt_BR" both become app_pt_BR.arb.
    /// </summary>
    private static string NormalizeLocale(string culture)
    {
        var parts = culture.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return NeutralCulture;

        var sb = new StringBuilder(parts[0].ToLowerInvariant());

        foreach (var part in parts.Skip(1))
            sb.Append('_').Append(part.Length == 4
                ? char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()   // script subtag: Hans
                : part.ToUpperInvariant());                                       // region subtag: BR

        return sb.ToString();
    }
}
