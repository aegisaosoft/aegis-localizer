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

using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms;
using Aegis.Localizer.Resources;

namespace Aegis.Localizer;

/// <summary>What one run produced. Everything a CLI, a GUI or an HTTP response needs to report.</summary>
public sealed class LocalizationResult
{
    public required string Platform { get; init; }

    public required ResourceFormat Format { get; init; }

    public required string ResourceDirectory { get; init; }

    public int FilesScanned { get; set; }

    public int FilesSkipped { get; set; }

    /// <summary>Every candidate the scanner produced, before the model weighed in.</summary>
    public IReadOnlyList<StringCandidate> Candidates { get; set; } = [];

    /// <summary>Strings that will be localized, with their keys and translations.</summary>
    public IReadOnlyList<LocalizationEntry> Localized { get; set; } = [];

    /// <summary>Candidates the model ruled out, with its reasoning.</summary>
    public IReadOnlyList<RejectedEntry> Rejected { get; set; } = [];

    /// <summary>Written bundle path per culture.</summary>
    public Dictionary<string, ResourceWriteResult> Written { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each target language gained, and what was left alone because it was already done.</summary>
    public List<LanguageOutcome> Languages { get; } = [];

    public RewriteSummary? Rewrite { get; set; }

    /// <summary>What the project is still missing before localization does anything.</summary>
    public LocalizationSetup Setup { get; set; } = LocalizationSetup.Complete;

    /// <summary>Setup steps this run carried out.</summary>
    public List<SetupStep> SetupApplied { get; } = [];

    /// <summary>
    /// True when --apply was asked for but refused, because the rewritten project would not build.
    /// </summary>
    public bool RewriteBlocked { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public TimeSpan Elapsed { get; set; }

    public string? ReportPath { get; set; }

    /// <summary>True when the run only listed candidates and never called the model.</summary>
    public bool ScanOnly { get; set; }
}

/// <summary>Outcome of the source rewriting stage.</summary>
public sealed record RewriteSummary(
    int FilesChanged, int Replacements, int NotRewritable, IReadOnlyList<string> Warnings, string BackupDirectory);
