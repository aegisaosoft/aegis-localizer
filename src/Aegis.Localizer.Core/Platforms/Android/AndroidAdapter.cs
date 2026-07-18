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

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Aegis.Localizer.Model;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Android;

/// <summary>
/// The Android stack: Kotlin and Java sources plus resource XML, covering both the classic View
/// world (layouts, findViewById, Toast) and Jetpack Compose.
/// </summary>
public sealed partial class AndroidAdapter : ISourceAdapter
{
    /// <summary>Import Compose needs for stringResource(); added by the rewriter when missing.</summary>
    private const string ComposeImport = "import androidx.compose.ui.res.stringResource";

    /// <summary>
    /// Per-file facts that decide whether a replacement compiles. Cached because the rewriter asks
    /// once per candidate and a screen easily has dozens of them in the same file.
    /// </summary>
    private readonly ConcurrentDictionary<string, FileTraits?> _traits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Module folder to the package its R class lives in; null when it cannot be determined.</summary>
    private readonly ConcurrentDictionary<string, string?> _rPackages = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "android";

    public string DisplayName => "Android (Kotlin, Java, layout XML)";

    public IReadOnlyCollection<string> Extensions { get; } = [".kt", ".java", ".xml"];

    public ResourceFormat DefaultFormat => ResourceFormat.AndroidXml;

    public int DetectionScore(string projectRoot)
    {
        // The manifest is unique to Android and exists in every module of every Android project.
        if (DirectoryWalk.Files(projectRoot, "AndroidManifest.xml").Any())
            return 100;

        // No manifest yet (a library module, or a template): the gradle script still names the plugin.
        foreach (var script in DirectoryWalk.Files(projectRoot, "build.gradle*")
                     .Where(f => f.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
                     .Take(20))
        {
            try
            {
                if (AndroidGradlePlugin().IsMatch(File.ReadAllText(script))) return 90;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable script simply does not vote.
            }
        }

        return 0;
    }

    /// <summary>
    /// Resources live in the module's res folder, so the app module's src/main/res is the target.
    /// The shallowest match wins, which picks the single-module case and the app module of a
    /// multi-module project alike.
    /// </summary>
    public string DefaultResourceDirectory(string projectRoot)
    {
        var candidate = DirectoryWalk.Directories(projectRoot, "res")
            .Where(d => IsMainRes(d) && !IsUnderBuildOutput(d, projectRoot))
            .OrderBy(d => d.Count(c => c is '/' or '\\'))
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return candidate ?? Path.Combine(projectRoot, "res");
    }

    private static bool IsMainRes(string directory)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty);
        return parent.Equals("main", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderBuildOutput(string directory, string projectRoot) =>
        Path.GetRelativePath(projectRoot, directory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                      s.Equals("intermediates", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resource names become fields on the generated R class, so the ecosystem writes them
    /// lower_snake_case. Applied here rather than in the store, so strings.xml and R.string.* can
    /// never disagree about a name.
    /// </summary>
    public string NormalizeKey(string key) => AndroidXmlStore.ResourceName(key);

    public IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".kt" or ".java" => AndroidCodeExtractor.Extract(filePath, relativePath, content),

            // .xml is shared with the manifest, gradle config and our own strings.xml output, so the
            // file has to earn its way in before anything is read out of it.
            ".xml" when AndroidLayoutExtractor.IsCopyBearing(filePath, content) =>
                AndroidLayoutExtractor.Extract(filePath, relativePath, content),

            _ => []
        };
    }

    public RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request)
    {
        var name = key;

        // Markup: the attribute value becomes a resource reference, no imports involved.
        if (candidate.Kind == CandidateKind.MarkupAttribute)
            return new RewritePlan($"@string/{name}");

        var traits = TraitsOf(candidate.FilePath);
        if (traits is null) return null;                       // unreadable file: report only

        // R is generated into the module's own package, so a file in a sub-package only sees it
        // through an import. A RewritePlan carries at most one import line and Compose already
        // needs its own, so R is qualified in the source instead of imported: correct everywhere,
        // and short where the file already resolves R on its own.
        var rPackage = RPackageFor(candidate.FilePath);
        var reference = traits.ResolvesR || (rPackage is not null && rPackage == traits.PackageName)
            ? "R"
            : rPackage is null ? null : $"{rPackage}.R";

        if (reference is null) return null;

        var construct = candidate.Member ?? string.Empty;
        var isComposeCall = AndroidCodeExtractor.ComposeConstructs.Contains(construct);

        // Compose: stringResource() reads the resource through LocalContext, but it is a composable
        // itself, so it is only legal inside a @Composable function.
        if (traits.UsesCompose && isComposeCall && traits.IsInsideComposable(candidate.SpanStart))
            return new RewritePlan($"stringResource({reference}.string.{name})", ComposeImport);

        // Classic views: getString() is a Context member. Inside an Activity, Fragment or Service it
        // resolves without a receiver; anywhere else - an adapter, a view holder, a plain helper
        // class - the same text would need a context we cannot invent, so the string is reported
        // rather than rewritten into code that does not compile.
        if (!isComposeCall || construct == "text")
            return traits.HasContextScope ? new RewritePlan($"getString({reference}.string.{name})") : null;

        return null;
    }

    /// <summary>
    /// Nothing to generate: aapt2 produces the R class from the resource files themselves, and both
    /// getString() and stringResource() are platform APIs.
    /// </summary>
    public void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log) =>
        log.Info($"  no runtime glue needed: Android generates R.string from {resourceDir}");

    private FileTraits? TraitsOf(string path) => _traits.GetOrAdd(path, static p =>
    {
        try
        {
            return new FileTraits(File.ReadAllText(p));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    /// <summary>
    /// Package the R class of the module owning this file belongs to. Found by walking up to the
    /// module root, so a multi-module project resolves each file against its own module rather than
    /// against whichever manifest happens to sort first.
    /// </summary>
    private string? RPackageFor(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));

        for (var depth = 0; directory is not null && depth < 12; depth++, directory = Path.GetDirectoryName(directory))
        {
            var resolved = _rPackages.GetOrAdd(directory, static d => ReadModulePackage(d));
            if (resolved is not null) return resolved;
        }

        return null;
    }

    /// <summary>
    /// The namespace declared by one module folder: the gradle `namespace` since AGP 7, the
    /// manifest `package` attribute before it.
    /// </summary>
    private static string? ReadModulePackage(string directory)
    {
        try
        {
            foreach (var script in new[] { "build.gradle.kts", "build.gradle" })
            {
                var path = Path.Combine(directory, script);
                if (!File.Exists(path)) continue;

                var match = GradleNamespace().Match(File.ReadAllText(path));
                if (match.Success) return match.Groups["ns"].Value;
            }

            foreach (var relative in new[] { "AndroidManifest.xml", Path.Combine("src", "main", "AndroidManifest.xml") })
            {
                var path = Path.Combine(directory, relative);
                if (!File.Exists(path)) continue;

                var match = ManifestPackage().Match(File.ReadAllText(path));
                if (match.Success) return match.Groups["ns"].Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable module descriptor just leaves the package unknown.
        }

        return null;
    }

    /// <summary>What a source file allows, derived once from its text.</summary>
    private sealed class FileTraits
    {
        private readonly string _content;

        public FileTraits(string content)
        {
            _content = content;
            UsesCompose = content.Contains("androidx.compose", StringComparison.Ordinal);
            HasContextScope = ContextScopedType().IsMatch(content);

            // Either the file already names R - so it is imported or in scope - or it does not, and
            // a bare R in our replacement would be an unresolved reference.
            ResolvesR = ExistingRUsage().IsMatch(content);

            var package = PackageDeclaration().Match(content);
            PackageName = package.Success ? package.Groups["pkg"].Value : null;
        }

        /// <summary>True when a bare `R` already compiles in this file.</summary>
        public bool ResolvesR { get; }

        /// <summary>The file's own package; a file in the R package needs no qualification.</summary>
        public string? PackageName { get; }

        /// <summary>True when the file imports Compose at all.</summary>
        public bool UsesCompose { get; }

        /// <summary>True when the enclosing type is itself a Context, so getString() needs no receiver.</summary>
        public bool HasContextScope { get; }

        /// <summary>
        /// True when the offset falls inside a @Composable function. Determined from the nearest
        /// preceding function header and the annotations directly above it, which is where Kotlin
        /// style always puts them; anything more precise would need a real parser, and a wrong
        /// answer here produces code that does not compile.
        /// </summary>
        public bool IsInsideComposable(int offset)
        {
            var header = -1;

            foreach (Match m in FunctionHeader().Matches(_content))
            {
                if (m.Index > offset) break;
                header = m.Index;
            }

            if (header < 0) return false;

            // Annotations sit immediately above the header; a short look-back covers them without
            // reaching the previous declaration.
            var from = Math.Max(0, header - 200);
            return _content[from..header].Contains("@Composable", StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"com\.android\.(application|library)|\bandroid\s*\{|\bplugins?\s*\{[^}]*\bandroid\b",
        RegexOptions.Singleline)]
    private static partial Regex AndroidGradlePlugin();

    [GeneratedRegex(@"^\s*namespace\s*=?\s*[""'](?<ns>[\w.]+)[""']", RegexOptions.Multiline)]
    private static partial Regex GradleNamespace();

    [GeneratedRegex(@"<manifest\b[^>]*?\spackage\s*=\s*""(?<ns>[\w.]+)""", RegexOptions.Singleline)]
    private static partial Regex ManifestPackage();

    [GeneratedRegex(@"^\s*package\s+(?<pkg>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex PackageDeclaration();

    // An existing R reference or an explicit import; either means a bare R resolves in this file.
    [GeneratedRegex(@"^\s*import\s+[\w.]+\.R\b|\bR\.(string|layout|id|drawable|plurals|menu|color|dimen)\b",
        RegexOptions.Multiline)]
    private static partial Regex ExistingRUsage();

    [GeneratedRegex(@"^[ \t]*(?:(?:private|internal|public|protected|inline|suspend|open|override|abstract)[ \t]+)*fun[ \t]",
        RegexOptions.Multiline)]
    private static partial Regex FunctionHeader();

    [GeneratedRegex(
        @"\bclass\s+\w+[^{]*?\b(AppCompatActivity|ComponentActivity|FragmentActivity|Activity|Fragment|" +
        @"DialogFragment|BottomSheetDialogFragment|PreferenceFragmentCompat|Service|ContextWrapper|Application)\b")]
    private static partial Regex ContextScopedType();
}
