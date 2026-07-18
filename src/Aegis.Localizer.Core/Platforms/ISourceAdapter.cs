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
using Aegis.Localizer.Resources;

namespace Aegis.Localizer.Platforms;

/// <summary>
/// How one literal becomes a resource lookup. <paramref name="RequiredImport"/> is a whole line
/// (an import, using or include) that the file needs for the replacement to compile; the rewriter
/// adds it once per file and only when it is missing.
/// </summary>
public sealed record RewritePlan(string Replacement, string? RequiredImport = null);

/// <summary>
/// Everything stack-specific about localizing a codebase: which files hold copy, how to pull the
/// strings out, how a lookup is written, and what runtime glue the result needs.
///
/// Adding a stack means implementing this and registering it in <see cref="AdapterRegistry"/>.
/// Scanning, batching, caching, translation, reporting and the backup-safe rewrite are shared and
/// need no changes.
/// </summary>
public interface ISourceAdapter
{
    /// <summary>Value accepted by --platform.</summary>
    string Name { get; }

    /// <summary>Human-readable stack name, shown to the user and given to the model as context.</summary>
    string DisplayName { get; }

    /// <summary>File extensions to scan, lower-case and dot-prefixed.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Resource layout this ecosystem expects, unless the user overrides it.</summary>
    ResourceFormat DefaultFormat { get; }

    /// <summary>Confidence 0..100 that the tree belongs to this stack; drives --platform auto.</summary>
    int DetectionScore(string projectRoot);

    /// <summary>Conventional resource folder for this stack, used when the request does not name one.</summary>
    string DefaultResourceDirectory(string projectRoot);

    /// <summary>
    /// Adapts a key to this ecosystem's naming convention - lower_snake_case for Android,
    /// lowerCamelCase for Flutter ARB.
    ///
    /// This is the ONLY place a key may be renamed. Doing it in the store or in PlanRewrite instead
    /// lets the bundle and the rewritten code disagree about a name, which compiles right up until
    /// the user runs it. Must be idempotent, or a second run renames what the first one wrote.
    /// </summary>
    string NormalizeKey(string key) => key;

    /// <summary>Pulls hardcoded string candidates out of one file.</summary>
    IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request);

    /// <summary>
    /// How to replace this candidate with a lookup, or null when the construct cannot be rewritten
    /// safely and should only be reported.
    /// </summary>
    RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request);

    /// <summary>
    /// Writes whatever glue the stack needs to read the bundles: a typed accessor class, an i18n
    /// bootstrap module, nothing at all. Called after the resources are written.
    /// </summary>
    void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log);
}
