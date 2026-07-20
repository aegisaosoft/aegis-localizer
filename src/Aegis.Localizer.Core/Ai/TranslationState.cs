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
/// What this project has learned about translating itself.
///
/// Two jobs, and the second is the point. First, bookkeeping: which source text each translation was
/// made from, so a reworded string can be told from a current one - the bundle alone cannot say.
///
/// Second, and more valuable: the tool also records what IT wrote. Anything in the bundle that no
/// longer matches was changed by a person, and that is the most reliable signal of quality a project
/// ever produces. Those corrections are never overwritten, not even by --retranslate, and they are
/// shown to the model as examples of how this project wants to be written. A project therefore gets
/// better at being translated the more its people correct it.
/// </summary>
public sealed class TranslationState
{
    private sealed class Entry
    {
        /// <summary>Hash of the source text this translation was made from.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Hash of the translation this tool wrote, to recognise a later human edit.</summary>
        public string Translation { get; set; } = string.Empty;
    }

    private sealed class Payload
    {
        /// <summary>language -> key -> what the tool last wrote there.</summary>
        public Dictionary<string, Dictionary<string, Entry>> Written { get; set; } = new();

        /// <summary>language -> source text -> the wording a person settled on.</summary>
        public Dictionary<string, Dictionary<string, string>> Approved { get; set; } = new();
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
                // Unreadable bookkeeping is not worth failing a run over; the next run rebuilds it.
            }
        }

        return new TranslationState(path, new Payload());
    }

    /// <summary>
    /// True when the copy behind this key was reworded after it was translated.
    ///
    /// A key with no record counts as current, not stale: bundles translated before this file
    /// existed would otherwise all be redone at once, at the user's expense, for nothing.
    /// </summary>
    public bool IsStale(string language, string key, string sourceText) =>
        Find(language, key) is { } entry && entry.Source != Hash(sourceText);

    /// <summary>
    /// True when the value in the bundle is not what this tool put there - so a person changed it.
    ///
    /// Unknown keys are NOT treated as edited: a bundle written before this record existed would
    /// otherwise freeze wholesale, and the tool would quietly stop being able to improve it.
    /// </summary>
    public bool IsHumanEdited(string language, string key, string currentValue) =>
        Find(language, key) is { } entry &&
        entry.Translation.Length > 0 &&
        entry.Translation != Hash(currentValue);

    /// <summary>Records a translation the tool just wrote.</summary>
    public void RecordWritten(string language, string key, string sourceText, string translation)
    {
        if (!_payload.Written.TryGetValue(language, out var keys))
        {
            keys = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _payload.Written[language] = keys;
        }

        keys[key] = new Entry { Source = Hash(sourceText), Translation = Hash(translation) };
    }

    /// <summary>
    /// Remembers a human's wording for a source string, so the next translation of anything similar
    /// can follow it. Keyed by source text rather than by key: the same copy under a new key should
    /// inherit the decision a person already made about it.
    /// </summary>
    public void RecordApproved(string language, string sourceText, string translation)
    {
        if (!_payload.Approved.TryGetValue(language, out var byText))
        {
            byText = new Dictionary<string, string>(StringComparer.Ordinal);
            _payload.Approved[language] = byText;
        }

        byText[sourceText] = translation;
    }

    /// <summary>A wording a person has already settled on for exactly this source text, if any.</summary>
    public string? ApprovedFor(string language, string sourceText) =>
        _payload.Approved.TryGetValue(language, out var byText) && byText.TryGetValue(sourceText, out var value)
            ? value
            : null;

    /// <summary>
    /// The project's own corrections, for the model to imitate. Shortest first: a handful of short,
    /// varied pairs teaches tone better than a few long ones that eat the budget.
    /// </summary>
    public IReadOnlyList<(string Source, string Translation)> ApprovedExamples(string language, int limit)
    {
        if (!_payload.Approved.TryGetValue(language, out var byText)) return [];

        return byText
            .OrderBy(kv => kv.Key.Length)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public int ApprovedCount(string language) =>
        _payload.Approved.TryGetValue(language, out var byText) ? byText.Count : 0;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Entry? Find(string language, string key) =>
        _payload.Written.TryGetValue(language, out var keys) && keys.TryGetValue(key, out var entry)
            ? entry
            : null;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
