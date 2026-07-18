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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aegis.Localizer.Ai;

/// <summary>
/// Which source text each translation was made from, per language and key.
///
/// Bookkeeping, not a cache: without it there is no way to tell a translation that is current from
/// one whose English was reworded afterwards. Both look like "the key is present". Once a project
/// has been rewritten, the bundle is where people edit the source copy, so this is the only signal
/// that a translation has gone stale. It is therefore written even when caching is off.
/// </summary>
public sealed class TranslationState
{
    private sealed class Payload
    {
        /// <summary>language -> key -> hash of the source text the translation was made from.</summary>
        public Dictionary<string, Dictionary<string, string>> Sources { get; set; } = new();
    }

    private readonly string _path;
    private readonly Payload _payload;

    private TranslationState(string path, Payload payload)
    {
        _path = path;
        _payload = payload;
    }

    public static TranslationState Load(string workDir)
    {
        var path = Path.Combine(workDir, "state.json");

        if (File.Exists(path))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
                if (payload is not null) return new TranslationState(path, payload);
            }
            catch (JsonException)
            {
                // Unreadable bookkeeping is not worth failing a run over; it just means the next
                // run cannot tell stale translations from current ones until it rebuilds.
            }
        }

        return new TranslationState(path, new Payload());
    }

    /// <summary>
    /// True when this key was translated from different copy than it now holds.
    ///
    /// A key with no record is treated as current, not as stale: bundles translated before this
    /// file existed would otherwise all be redone at once, at the user's expense, for nothing.
    /// </summary>
    public bool IsStale(string language, string key, string sourceText) =>
        _payload.Sources.TryGetValue(language, out var keys) &&
        keys.TryGetValue(key, out var recorded) &&
        recorded != Hash(sourceText);

    public void Record(string language, string key, string sourceText)
    {
        if (!_payload.Sources.TryGetValue(language, out var keys))
        {
            keys = new Dictionary<string, string>(StringComparer.Ordinal);
            _payload.Sources[language] = keys;
        }

        keys[key] = Hash(sourceText);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
