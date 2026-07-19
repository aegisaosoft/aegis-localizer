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

using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Apple;

/// <summary>
/// Checking the localization support an Apple app needs.
///
/// Inspection only, and that is a decision rather than an omission. What is missing here lives in
/// project.pbxproj - an Xcode-owned, ordering-sensitive file full of generated object identifiers
/// that has to stay consistent with the .xcodeproj around it. Editing it blind is how a repository
/// stops opening in Xcode, so every step below is written out for a human instead.
///
/// Nothing is blocking either. String(localized:) and NSLocalizedString are Foundation calls that
/// compile whatever is in the bundle; when the table is not there they return the key. So the
/// rewrite never breaks the build - but be clear about the failure mode, because it is nastier than
/// the usual "stays English": the UI shows raw keys like SaveButton until the .lproj folders are in
/// the target.
/// </summary>
internal static class AppleSetup
{
    public static LocalizationSetup Inspect(LocalizationRequest request, string resourceDir)
    {
        var missing = new List<SetupStep>();

        var cultures = new List<string> { request.SourceLanguage };

        foreach (var language in request.Languages)
            if (!cultures.Contains(language, StringComparer.OrdinalIgnoreCase))
                cultures.Add(language);

        var pbxproj = FindPbxproj(request.ProjectPath);
        var packageSwift = FindPackageSwift(request.ProjectPath);

        if (pbxproj is not null)
        {
            var project = Read(pbxproj);

            // Unreadable is not the same as unwired; say which one it is rather than inventing a gap.
            if (project is null)
                missing.Add(new SetupStep(
                    "Could not read the Xcode project",
                    $"{Rel(request.ProjectPath, pbxproj)} could not be opened, so whether the .lproj " +
                    "folders are in the target is unknown. Open the project in Xcode and check that " +
                    $"{Folders(cultures)} appear under the target's Copy Bundle Resources phase.",
                    SetupSeverity.Recommended,
                    Automatic: false,
                    File: Rel(request.ProjectPath, pbxproj)));
            else
            {
                var unreferenced = cultures.Where(c => !ReferencesLocale(project, c)).ToList();

                if (unreferenced.Count > 0)
                    missing.Add(new SetupStep(
                        "Add the .lproj folders to the Xcode target",
                        $"{Folders(unreferenced)} under {Rel(request.ProjectPath, resourceDir)} " +
                        (unreferenced.Count == 1 ? "is" : "are") + " not referenced by " +
                        $"{Rel(request.ProjectPath, pbxproj)}.\n" +
                        "In Xcode: File > Add Files to \"<project>\", pick the .lproj folders, tick your " +
                        "app target, and confirm they land in Build Phases > Copy Bundle Resources. " +
                        "Then check Project > Info > Localizations lists " +
                        string.Join(", ", unreferenced) + ".\n" +
                        "Until this is done the app still builds and runs, but String(localized:) finds " +
                        "no table and returns the key, so the UI shows key names rather than copy. " +
                        "This is left to you on purpose: project.pbxproj is Xcode's own file and " +
                        "editing it from outside is how a project stops opening.",
                        // Blocking despite compiling: rewriting would replace working English copy
                        // with raw key names on screen, which is a worse app than the one we started
                        // with. That is the bar, not whether the compiler objects.
                        SetupSeverity.Blocking,
                        Automatic: false,
                        File: Rel(request.ProjectPath, pbxproj)));
            }
        }

        if (packageSwift is not null)
        {
            var manifest = Read(packageSwift);

            if (manifest is not null && !manifest.Contains("defaultLocalization", StringComparison.Ordinal))
                missing.Add(new SetupStep(
                    "Set defaultLocalization in Package.swift",
                    $"let package = Package(\n    name: \"...\",\n    defaultLocalization: \"{cultures[0]}\",\n" +
                    "    ...\n)\n" +
                    "SwiftPM refuses to process localized resources without it, and the .lproj folders " +
                    "also need declaring on the target:\n" +
                    "    .target(name: \"...\", resources: [.process(\"" +
                    Rel(request.ProjectPath, resourceDir).Replace('\\', '/') + "\")])\n" +
                    "Left manual because both edits go inside a Swift manifest whose structure this " +
                    "tool does not parse.",
                    SetupSeverity.Recommended,
                    Automatic: false,
                    File: Rel(request.ProjectPath, packageSwift)));
        }

        // No project file of either kind: the strings were written, but there is nothing on disk
        // that says how they reach a bundle. Saying so is more honest than reporting Complete, which
        // means "checked, nothing needed".
        if (pbxproj is null && packageSwift is null)
            missing.Add(new SetupStep(
                "No Xcode project or Package.swift found",
                $"The .lproj folders were written to {Rel(request.ProjectPath, resourceDir)}, but without " +
                "a project file there is no way to verify they are bundled. Add them to whichever target " +
                "builds your app - Copy Bundle Resources in Xcode, or a resources: entry in Package.swift " +
                "- or Foundation will not find the tables at run time.",
                SetupSeverity.Recommended,
                Automatic: false));

        // Only worth raising when there is no Xcode project to derive the list from. An app target
        // gets CFBundleLocalizations written into its built Info.plist by Xcode itself, so asking a
        // normal app to add it by hand would put a step in every single report that nobody needs.
        var infoPlist = pbxproj is null ? FindInfoPlist(request.ProjectPath) : null;

        if (infoPlist is not null && Read(infoPlist) is { } plist &&
            !plist.Contains("CFBundleLocalizations", StringComparison.Ordinal))
            missing.Add(new SetupStep(
                "List the localizations in Info.plist",
                "There is no Xcode project here to derive the list from - a framework, a plug-in or a " +
                "hand-assembled bundle - so the bundle has to name its localizations itself:\n" +
                "  <key>CFBundleLocalizations</key>\n  <array>\n" +
                string.Join("\n", cultures.Select(c => $"    <string>{c}</string>")) +
                "\n  </array>\n" +
                $"CFBundleDevelopmentRegion should be {cultures[0]} in that case too.",
                SetupSeverity.Recommended,
                Automatic: false,
                File: Rel(request.ProjectPath, infoPlist)));

        return new LocalizationSetup(missing);
    }

    /// <summary>
    /// True when the project file mentions this locale at all, as an .lproj path or in knownRegions.
    /// Substring matching is right here: pbxproj spells the same folder several ways depending on
    /// how the file was added, and a false "already wired" is a much cheaper mistake than telling
    /// someone to redo work they have done.
    /// </summary>
    private static bool ReferencesLocale(string project, string culture) =>
        project.Contains($"{culture}.lproj", StringComparison.OrdinalIgnoreCase) ||
        project.Contains($"/{culture}\"", StringComparison.OrdinalIgnoreCase) ||
        project.Contains($" {culture},", StringComparison.OrdinalIgnoreCase) ||
        project.Contains($"\t{culture},", StringComparison.OrdinalIgnoreCase);

    private static string Folders(IReadOnlyList<string> cultures) =>
        string.Join(", ", cultures.Select(c => $"{c}.lproj"));

    private static string? FindPbxproj(string projectRoot)
    {
        foreach (var bundle in DirectoryWalk.Directories(projectRoot, "*.xcodeproj"))
        {
            var path = Path.Combine(bundle, "project.pbxproj");

            try
            {
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable bundle just does not answer.
            }
        }

        return null;
    }

    private static string? FindPackageSwift(string projectRoot)
    {
        var root = Path.Combine(projectRoot, "Package.swift");
        if (File.Exists(root)) return root;

        return DirectoryWalk.Files(projectRoot, "Package.swift").FirstOrDefault();
    }

    /// <summary>Shallowest Info.plist, skipping the ones Xcode generates into build output.</summary>
    private static string? FindInfoPlist(string projectRoot) =>
        DirectoryWalk.Files(projectRoot, "Info.plist")
            .Where(p => !IsBuildOutput(projectRoot, p))
            .OrderBy(p => p.Count(c => c is '/' or '\\'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool IsBuildOutput(string projectRoot, string path)
    {
        try
        {
            return Path.GetRelativePath(projectRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => s.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                          s.Equals("DerivedData", StringComparison.OrdinalIgnoreCase) ||
                          s.Equals("Pods", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Rel(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
