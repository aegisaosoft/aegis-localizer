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

namespace Aegis.Localizer.Platforms;

/// <summary>How badly a missing piece of setup hurts.</summary>
public enum SetupSeverity
{
    /// <summary>
    /// The rewrite would leave the app broken for the people who use it. The obvious case is code
    /// that no longer compiles, but it also covers an app that builds and then shows resource keys
    /// where copy used to be - the test is "would the user ship something worse than they had", not
    /// merely "does the compiler complain". The rewrite is refused until this is dealt with.
    /// </summary>
    Blocking,

    /// <summary>
    /// The app keeps working exactly as it did, it just does not gain anything yet - typically
    /// because no code ever selects a culture, so everyone still sees the source language. Worth
    /// shouting about; not worth refusing over.
    /// </summary>
    Recommended
}

/// <summary>One thing the project still needs before localization actually works.</summary>
/// <param name="Title">Short imperative summary, e.g. "Add flutter_localizations to pubspec.yaml".</param>
/// <param name="Detail">What to do, precisely enough to follow by hand.</param>
/// <param name="Automatic">True when <see cref="ISourceAdapter.ApplySetup"/> can do it.</param>
/// <param name="File">The file it concerns, when there is one.</param>
public sealed record SetupStep(
    string Title,
    string Detail,
    SetupSeverity Severity,
    bool Automatic,
    string? File = null);

/// <summary>
/// What an app is still missing before localized strings do anything.
///
/// This exists because most projects have never been localized: they have no resource wiring, no
/// culture selection, no i18n dependency. Extracting and translating strings for such a project and
/// then rewriting the code would leave it worse than it started - not compiling, or compiling and
/// stubbornly English. So the tool inspects first, says exactly what is missing, and can add it.
/// </summary>
public sealed record LocalizationSetup(IReadOnlyList<SetupStep> Missing)
{
    public static LocalizationSetup Complete { get; } = new([]);

    public bool IsReady => Missing.Count == 0;

    /// <summary>True when something missing would stop the rewritten project from building.</summary>
    public bool HasBlocking => Missing.Any(s => s.Severity == SetupSeverity.Blocking);

    /// <summary>True when every blocking gap can be closed without the user editing anything.</summary>
    public bool BlockingIsAutomatic =>
        Missing.Where(s => s.Severity == SetupSeverity.Blocking).All(s => s.Automatic);
}
