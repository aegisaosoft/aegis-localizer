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

namespace Aegis.Localizer.Filtering;

/// <summary>
/// Turns model-proposed keys into legal, unique C# identifiers. The model picks readable names;
/// this class guarantees they compile and never collide.
/// </summary>
public sealed class KeyNamer
{
    private readonly Dictionary<string, string> _byText = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    /// <param name="existing">
    /// Resources already on disk, mapped source text -&gt; key. Seeding them means an unchanged
    /// string keeps its key across runs, and a new string can never take a key that is already in
    /// use by code the previous run rewrote.
    /// </param>
    /// <param name="normalize">
    /// The stack's naming convention, applied before uniqueness is decided. Enforcing uniqueness on
    /// the pre-normalized form would let two distinct keys collide after conversion.
    /// </param>
    public KeyNamer(IReadOnlyDictionary<string, string>? existing = null, Func<string, string>? normalize = null)
    {
        _normalize = normalize ?? (key => key);

        if (existing is null) return;

        foreach (var (text, key) in existing)
        {
            _byText[text] = key;
            _taken.Add(key);
        }
    }

    private readonly Func<string, string> _normalize;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "Get", "Format", "Culture", "Manager", "Equals", "GetHashCode", "GetType", "ToString", "L"
    };

    /// <summary>
    /// Resolves the final key for a string. Identical source text always maps to the same key, so
    /// repeated copy collapses into a single resource entry.
    /// </summary>
    public string Resolve(string sourceText, string? proposed)
    {
        if (_byText.TryGetValue(sourceText, out var existing)) return existing;

        var candidate = Sanitize(proposed);
        if (candidate.Length == 0) candidate = Sanitize(FromText(sourceText));
        if (candidate.Length == 0) candidate = "Text";
        if (Reserved.Contains(candidate)) candidate += "Text";

        candidate = _normalize(candidate);
        if (candidate.Length == 0) candidate = _normalize("Text");

        var final = candidate;
        for (var n = 2; !_taken.Add(final); n++)
            final = _normalize(candidate + n);

        _byText[sourceText] = final;
        return final;
    }

    /// <summary>Builds a PascalCase key from the copy itself, used when the model gives none.</summary>
    private static string FromText(string text)
    {
        var words = text
            .Split([' ', '\t', '\n', '\r', '-', '_', '.', ',', ':', ';', '!', '?', '/', '\\', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Any(char.IsLetterOrDigit))
            .Take(6)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        return string.Concat(words);
    }

    private static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        var upperNext = false;

        foreach (var c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                // Separators become PascalCase boundaries instead of underscores.
                upperNext = sb.Length > 0;
            }
        }

        var result = sb.ToString();
        if (result.Length == 0) return string.Empty;
        if (char.IsDigit(result[0])) result = "N" + result;
        return char.ToUpperInvariant(result[0]) + result[1..];
    }
}
