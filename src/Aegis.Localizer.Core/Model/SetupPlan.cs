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

using Aegis.Localizer.Platforms;

namespace Aegis.Localizer.Model;

/// <summary>One file the model is allowed to look at, and to propose changes to.</summary>
public sealed record ProjectFile(string RelativePath, string Content, bool Exists = true);

/// <summary>
/// Everything the model needs to work out what a project is missing.
///
/// Adapters supply facts - which files govern this build, what the rewritten code will call, where
/// the bundles went - and stop there. Deciding what to change is the model's job, because the shape
/// of a real build file is not something a hand-written rule can keep up with: a Gradle script can
/// restrict shipped locales, a monorepo can hide the manifest three levels down, a Vite project can
/// put its entry point anywhere.
/// </summary>
public sealed record SetupContext(
    string Stack,
    string RewriteContract,
    string ResourceLayout,
    IReadOnlyList<ProjectFile> Files);

public enum SetupEditKind
{
    /// <summary>Write a new file. Refused if it already exists.</summary>
    CreateFile,

    /// <summary>Replace an exact snippet. Refused unless it occurs exactly once.</summary>
    ReplaceText,

    /// <summary>Insert after an exact snippet. Refused unless it occurs exactly once.</summary>
    InsertAfter,

    /// <summary>Append to the end of an existing file.</summary>
    AppendToFile
}

/// <summary>
/// A single change the model proposes.
///
/// Anchored rather than whole-file: an exact snippet that must occur exactly once is verifiable
/// before anything is written, and it keeps the model from quietly reformatting a manifest somebody
/// maintains by hand. It is the same discipline the string rewriter uses on source spans.
/// </summary>
public sealed record SetupEdit
{
    public string File { get; set; } = string.Empty;
    public SetupEditKind Kind { get; set; }
    public string? Anchor { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A thing the project needs, with the edits that would provide it.</summary>
public sealed class PlannedStep
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public SetupSeverity Severity { get; set; }

    /// <summary>Empty when the change belongs inside the user's own code and must be done by hand.</summary>
    public List<SetupEdit> Edits { get; set; } = [];

    public bool IsAutomatic => Edits.Count > 0;
}

public sealed class SetupPlan
{
    public List<PlannedStep> Steps { get; set; } = [];
}
