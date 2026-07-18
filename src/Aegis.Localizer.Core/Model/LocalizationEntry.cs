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

namespace Aegis.Localizer.Model;

/// <summary>
/// The model's language-independent verdict on one string: is it copy at all, and what should the
/// resource be called. Kept separate from translation so a second target language costs only the
/// translation pass.
/// </summary>
public sealed class StringVerdict
{
    public int Id { get; set; }

    /// <summary>True when the string is shown to an end user and should be localized.</summary>
    public bool UserFacing { get; set; }

    /// <summary>Short justification, kept in the report so decisions can be reviewed.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Suggested resource key, PascalCase.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>One translated string, as returned by the translation pass.</summary>
public sealed class TranslatedString
{
    public int Id { get; set; }

    public string Translation { get; set; } = string.Empty;

    /// <summary>Set by the model when it had to deviate, e.g. reordered placeholders.</summary>
    public string? Note { get; set; }
}

/// <summary>A candidate joined with its final key and every translation produced for it.</summary>
public sealed class LocalizationEntry
{
    public required StringCandidate Candidate { get; init; }

    /// <summary>Final, collision-free resource key.</summary>
    public required string Key { get; set; }

    public string Reason { get; init; } = string.Empty;

    /// <summary>Culture name to translated text.</summary>
    public Dictionary<string, string> Translations { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A candidate the model ruled out, kept so the report can explain why.</summary>
public sealed record RejectedEntry(StringCandidate Candidate, string Reason);
