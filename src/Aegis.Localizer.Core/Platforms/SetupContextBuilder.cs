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

using Aegis.Localizer.Io;
using Aegis.Localizer.Model;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms;

/// <summary>
/// Collects the files that govern how a project is built, for the model to read.
///
/// Generic on purpose. Which build files matter is not really a per-stack question - a repository
/// has manifests, build scripts and an entry point wherever it happens to keep them - and a shared
/// collector means a new adapter inherits this with no code at all. An adapter that knows about
/// something unusual can still add to the list.
/// </summary>
public static class SetupContextBuilder
{
    /// <summary>Enough to cover a monorepo without burying the model in a lockfile.</summary>
    private const int MaxFiles = 24;

    /// <summary>Files that decide how a project builds, packages and starts, on any stack.</summary>
    private static readonly string[] Manifests =
    [
        "pubspec.yaml", "l10n.yaml", "package.json", "Package.swift", "AndroidManifest.xml",
        "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
        "gradle.properties", "Info.plist", "project.pbxproj", "angular.json", "vite.config.ts",
        "vite.config.js", "next.config.js", "next.config.mjs", "nuxt.config.ts", "tsconfig.json",
        "Directory.Build.props"
    ];

    private static readonly string[] ManifestPatterns = ["*.csproj"];

    /// <summary>Where an app is likely to start, which is where a bootstrap import has to go.</summary>
    private static readonly string[] EntryPoints =
    [
        "src/main.tsx", "src/main.ts", "src/main.jsx", "src/main.js",
        "src/index.tsx", "src/index.ts", "src/index.jsx", "src/index.js",
        "src/App.tsx", "src/App.vue", "lib/main.dart", "Program.cs", "Startup.cs",
        "App.xaml.cs", "MauiProgram.cs"
    ];

    public static SetupContext Build(
        ISourceAdapter adapter, LocalizationRequest request, string resourceDir, IEnumerable<string>? extra = null)
    {
        var root = request.ProjectPath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ProjectFile>();

        foreach (var relative in EntryPoints.Concat(extra ?? []))
            Add(files, seen, root, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var name in Manifests)
            foreach (var path in Find(root, name))
                Add(files, seen, root, path);

        foreach (var pattern in ManifestPatterns)
            foreach (var path in Find(root, pattern))
                Add(files, seen, root, path);

        // The bundles we just wrote, named but not read: the model needs to know they exist and
        // where, not what is inside them.
        var bundles = Directory.Exists(resourceDir)
            ? DirectoryWalk.Files(resourceDir).Take(12).Select(f => Path.GetRelativePath(root, f))
            : [];

        var layout = Directory.Exists(resourceDir)
            ? $"Translation bundles are written to {Path.GetRelativePath(root, resourceDir)} " +
              $"({adapter.DefaultFormat}). Files: {string.Join(", ", bundles)}"
            : $"Translation bundles will be written to {Path.GetRelativePath(root, resourceDir)} ({adapter.DefaultFormat}).";

        return new SetupContext(adapter.DisplayName, adapter.RewriteContract, layout, files);
    }

    private static IEnumerable<string> Find(string root, string nameOrPattern)
    {
        // Shallow paths first: in a monorepo the root manifest matters more than a fixture's.
        return DirectoryWalk
            .Files(root, nameOrPattern)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .Take(4);
    }

    private static void Add(List<ProjectFile> files, HashSet<string> seen, string root, string path)
    {
        if (files.Count >= MaxFiles) return;

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal)) return;
        if (!seen.Add(relative)) return;

        var content = SourceFile.TryRead(path);
        if (content is null) return;

        files.Add(new ProjectFile(relative, content.Text));
    }
}
