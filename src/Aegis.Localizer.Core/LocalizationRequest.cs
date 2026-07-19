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

using Aegis.Localizer.Resources;

namespace Aegis.Localizer;

/// <summary>
/// Everything one localization run needs. Deliberately a plain data object with no console or file
/// system assumptions, so the CLI, the desktop app and the web service can all build one.
/// </summary>
public sealed class LocalizationRequest
{
    /// <summary>Root folder to scan.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Target cultures, e.g. ["ru", "es", "pt-BR"]. At least one.</summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>
    /// Culture the source strings are written in. Used to tell the model what it is translating
    /// from, so the tool is not hardcoded to English source copy.
    /// </summary>
    public string SourceLanguage { get; init; } = "en";

    /// <summary>Adapter name, or "auto" to detect from the tree.</summary>
    public string Platform { get; init; } = "auto";

    /// <summary>Where resources are written. Null means the adapter's convention for this stack.</summary>
    public string? OutputDir { get; init; }

    /// <summary>Bundle base name, meaningful for formats that use one (resx, i18next namespaces).</summary>
    public string ResourceName { get; init; } = "AppResources";

    /// <summary>Namespace or module identity for generated runtime glue. Inferred when null.</summary>
    public string? Namespace { get; init; }

    /// <summary>Resource file format. Null means the adapter's default for this stack.</summary>
    public ResourceFormat? Format { get; init; }

    /// <summary>Rewrite the sources. False keeps the run a dry run.</summary>
    public bool Apply { get; init; }

    /// <summary>Treat log and exception text as localizable too.</summary>
    public bool IncludeDiagnostics { get; init; }

    /// <summary>Extract and report only; never calls the API.</summary>
    public bool ScanOnly { get; init; }

    /// <summary>Reuse verdicts and translations cached from earlier runs.</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// Add the localization support the project is missing - i18n dependencies, generated-
    /// localization config, a culture bootstrap - before rewriting anything.
    /// </summary>
    public bool Setup { get; init; }

    /// <summary>
    /// Redo every translation, including ones already in the bundle. Normally a run only fills what
    /// is missing or stale; this is for after changing the tone, the glossary or the model.
    /// </summary>
    public bool Retranslate { get; init; }

    public int BatchSize { get; init; } = 25;

    public int Concurrency { get; init; } = 4;

    /// <summary>0 means no limit.</summary>
    public int MaxFiles { get; init; }

    /// <summary>Extra path fragments to skip.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    public string Model { get; init; } = "claude-sonnet-5";

    /// <summary>Free-form product context ("a car rental app, informal tone") passed to the model.</summary>
    public string? ProjectContext { get; init; }

    /// <summary>Terms that must never be translated, on top of what the model infers.</summary>
    public IReadOnlyList<string> DoNotTranslate { get; init; } = [];

    /// <summary>Folder holding the cache and the reports.</summary>
    public string WorkDir => Path.Combine(ProjectPath, ".aegis-localizer");
}
