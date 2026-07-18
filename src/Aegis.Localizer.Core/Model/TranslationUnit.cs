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
/// One string to translate, decoupled from where it came from.
///
/// Translation works from the source bundle, not from the code, because after a rewrite the literal
/// is no longer in the code at all. A unit therefore may or may not have a candidate behind it: the
/// ones found by this scan do, the ones already in the bundle from earlier runs do not.
/// </summary>
public sealed record TranslationUnit(string Key, string SourceText, string Context);

/// <summary>Why a string is being sent to the model on this run.</summary>
public enum TranslationReason
{
    /// <summary>New string, never translated into this language.</summary>
    Missing,

    /// <summary>The source text changed since this language was last translated.</summary>
    SourceChanged,

    /// <summary>The caller asked for everything to be redone.</summary>
    Forced
}

/// <summary>What one language's bundle gained on this run; drives the report and the CLI output.</summary>
public sealed class LanguageOutcome
{
    public required string Language { get; init; }

    /// <summary>Strings sent to the model, by why they were sent.</summary>
    public Dictionary<TranslationReason, int> Sent { get; } = new();

    /// <summary>Already translated and left alone.</summary>
    public int AlreadyTranslated { get; set; }

    /// <summary>Total keys the bundle should hold after this run.</summary>
    public int Total { get; set; }

    public int SentTotal => Sent.Values.Sum();
}
