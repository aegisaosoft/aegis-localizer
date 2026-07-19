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
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Apple;

/// <summary>
/// The Apple stack: Swift and SwiftUI, Objective-C, and the Interface Builder documents that go with
/// them, which together cover iOS, iPadOS, macOS, watchOS and tvOS apps.
/// </summary>
public sealed class AppleAdapter : ISourceAdapter
{
    public string Name => "apple";

    public string DisplayName => "Apple (Swift, SwiftUI, Objective-C)";

    public IReadOnlyCollection<string> Extensions { get; } = [".swift", ".m", ".h", ".storyboard", ".xib"];

    public ResourceFormat DefaultFormat => ResourceFormat.AppleStrings;

    public int DetectionScore(string projectRoot)
    {
        // An Xcode project or workspace is a folder, not a file, hence the directory probe.
        if (DirectoryWalk.Directories(projectRoot, "*.xcodeproj").Any()) return 100;
        if (DirectoryWalk.Directories(projectRoot, "*.xcworkspace").Any()) return 100;
        if (File.Exists(Path.Combine(projectRoot, "Package.swift"))) return 95;
        if (DirectoryWalk.Files(projectRoot, "Package.swift").Any()) return 90;
        return DirectoryWalk.Files(projectRoot, "*.swift").Any() ? 60 : 0;
    }

    /// <summary>
    /// Xcode keeps .lproj folders under a Resources group, which is also where SwiftPM expects
    /// them, so that folder is used whether or not it exists yet.
    ///
    /// The project root itself is deliberately not a fallback: the scanner skips everything under
    /// the resource directory so a run never re-reads its own output, and pointing that at the root
    /// would silently exclude the whole tree.
    /// </summary>
    public string DefaultResourceDirectory(string projectRoot) => Path.Combine(projectRoot, "Resources");

    public IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request)
    {
        var candidates = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".swift" => SwiftExtractor.Extract(filePath, relativePath, content),
            ".m" or ".h" => ObjectiveCExtractor.Extract(filePath, relativePath, content),
            ".storyboard" or ".xib" => InterfaceBuilderExtractor.Extract(filePath, relativePath, content),
            _ => []
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Kind == CandidateKind.Diagnostic && !request.IncludeDiagnostics) continue;
            yield return candidate;
        }
    }

    public RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request)
    {
        // Interface Builder documents are localized by ibtool against a Base.lproj, keyed by object
        // id. Editing the XML in place would produce a file Xcode still shows but no longer wires
        // up correctly, so these stay report-only.
        if (candidate.Kind is CandidateKind.MarkupAttribute or CandidateKind.MarkupText) return null;

        // Interpolated copy needs a format string and reordered arguments; not a mechanical swap.
        if (candidate.IsInterpolated) return null;

        return Path.GetExtension(candidate.FilePath).ToLowerInvariant() switch
        {
            // String(localized:) resolves against the default Localizable table, which is exactly
            // what AppleStringsStore writes. The import is a no-op when the file already has it.
            ".swift" => new RewritePlan($"String(localized: \"{key}\")", "import Foundation"),

            ".m" or ".h" => new RewritePlan($"NSLocalizedString(@\"{key}\", nil)", "#import <Foundation/Foundation.h>"),

            _ => null
        };
    }

    public LocalizationSetup InspectSetup(LocalizationRequest request, string resourceDir) =>
        AppleSetup.Inspect(request, resourceDir);

    // No ApplySetup override: everything AppleSetup reports lives in project.pbxproj or Package.swift,
    // and the interface default of "did nothing" is the honest answer for both.

    public void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log) =>
        // Foundation reads .strings straight out of the app bundle, so there is no accessor to
        // generate; the .lproj folders only have to be added to the target's Copy Bundle Resources.
        log.Info($"  no runtime glue needed: {keys.Count} keys resolve through the .lproj bundles in {resourceDir}");
}
