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
/// JSON bundles in the i18next layout: &lt;dir&gt;/&lt;culture&gt;/&lt;namespace&gt;.json. Keys are written flat,
/// which is what i18next uses when `keySeparator` is off and what vue-i18n and react-intl also accept.
/// </summary>
public sealed class I18NextJsonStore : IResourceStore
{
    public ResourceFormat Format => ResourceFormat.I18NextJson;

    /// <summary>
    /// A null culture means the source bundle. The caller passes the request's source language, so
    /// this only sees null when there is genuinely nothing better to use.
    /// </summary>
    public string ResolvePath(ResourceLocation location) =>
        Path.Combine(location.Directory, location.EffectiveCulture, $"{location.BaseName}.json");

    public IReadOnlyDictionary<string, string> Read(ResourceLocation location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolvePath(location);
        if (!File.Exists(path)) return result;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) return result;
            foreach (var (key, value) in obj)
                if (value is JsonValue v && v.TryGetValue<string>(out var s))
                    result[key] = s;
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

        // The whole existing document is carried over, not just the flat string entries we can
        // read. A real i18next bundle is full of things this tool does not model - nested
        // namespaces, plural suffix groups, arrays - and rebuilding the file from our own view of
        // it would delete every one of them while cheerfully reporting success.
        var document = Load(path);
        var added = 0;
        var updated = 0;

        foreach (var (key, value) in values)
        {
            if (document[key] is JsonValue existing && existing.TryGetValue<string>(out var current))
            {
                if (current == value) continue;
                updated++;
            }
            else if (document.ContainsKey(key))
            {
                // The key exists but is not a plain string: leave the user's structure alone.
                continue;
            }
            else
            {
                added++;
            }

            document[key] = value;
        }

        // Sorted output keeps diffs readable when several people run the tool.
        var obj = new JsonObject();
        foreach (var name in document.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            var node = document[name];
            document[name] = null;
            obj[name] = node;
        }

        var json = obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep real characters rather than \uXXXX so the bundles stay human-readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(path, json + "\n", new UTF8Encoding(false));

        return new ResourceWriteResult(path, added, updated);
    }

    /// <summary>The bundle as it stands, or an empty document when it is missing or malformed.</summary>
    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
