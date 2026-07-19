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

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aegis.Localizer.Platforms.Web;

/// <summary>
/// Checking and building the localization support a JavaScript app needs.
///
/// Everything here is blocking, because the rewriter turns copy into `t("Key")` and `$t("Key")`:
/// without the packages those calls do not resolve, and without the bootstrap being imported they
/// resolve to an uninitialised i18next that hands back the key itself. Either way the user ends up
/// with a worse app than the one they started with, so the rewrite is refused until this is closed.
/// </summary>
internal static partial class WebSetup
{
    // Caret ranges rather than pins: these are the current majors, and a project that already
    // resolved a newer patch keeps it. i18next 25 is what react-i18next 15 and vue-i18n 11 are
    // built against, so the three agree with each other.
    private const string I18NextVersion = "^25.0.0";
    private const string ReactI18NextVersion = "^15.0.0";
    private const string VueI18NVersion = "^11.0.0";

    /// <summary>
    /// Entry points a bundler is pointed at, in the order Vite, CRA and Next-style trees use them.
    /// The first one that exists is where the bootstrap import belongs.
    /// </summary>
    private static readonly string[] EntryCandidates =
    [
        "src/main.tsx", "src/main.ts", "src/main.jsx", "src/main.js",
        "src/index.tsx", "src/index.ts", "src/index.jsx", "src/index.js"
    ];

    public static LocalizationSetup Inspect(LocalizationRequest request, string resourceDir)
    {
        var missing = new List<SetupStep>();
        var manifestPath = FindPackageJson(request.ProjectPath);

        if (manifestPath is null)
        {
            missing.Add(new SetupStep(
                "package.json not found",
                "No package.json was found at the project root or one folder below it, so the i18next " +
                "packages the rewritten code imports cannot be added automatically. Create the manifest " +
                "(`npm init -y`), then run again.",
                SetupSeverity.Blocking,
                Automatic: false));

            return new LocalizationSetup(missing);
        }

        var dependencies = ReadDependencies(manifestPath);

        if (dependencies is null)
        {
            missing.Add(new SetupStep(
                "package.json could not be read",
                $"{manifestPath} is missing, unreadable or not valid JSON. It has to be repaired by hand: " +
                "editing a manifest this tool cannot parse would be guesswork.",
                SetupSeverity.Blocking,
                Automatic: false,
                File: "package.json"));

            return new LocalizationSetup(missing);
        }

        // i18next itself backs every flavour of the rewrite, including the Vue one, because vue-i18n
        // is layered on it.
        if (!dependencies.Contains("i18next"))
            missing.Add(new SetupStep(
                "Add i18next to package.json",
                $"dependencies: \"i18next\": \"{I18NextVersion}\"\n" +
                "The rewritten code calls t(\"Key\"), which is i18next's translator.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "package.json"));

        if (dependencies.Contains("react") && !dependencies.Contains("react-i18next"))
            missing.Add(new SetupStep(
                "Add react-i18next to package.json",
                $"dependencies: \"react-i18next\": \"{ReactI18NextVersion}\"\n" +
                "The generated bootstrap calls i18n.use(initReactI18next), which lives in this package; " +
                "without it the bootstrap module fails to resolve and the whole app fails to start.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "package.json"));

        if (dependencies.Contains("vue") && !dependencies.Contains("vue-i18n"))
            missing.Add(new SetupStep(
                "Add vue-i18n to package.json",
                $"dependencies: \"vue-i18n\": \"{VueI18NVersion}\"\n" +
                "The generated bootstrap calls createI18n from this package, and $t in a rewritten " +
                "template is what it injects.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "package.json"));

        var appRoot = Path.GetDirectoryName(manifestPath) ?? request.ProjectPath;
        var entry = FindEntry(appRoot);
        var specifier = BootstrapSpecifier(entry, appRoot, resourceDir);

        if (!BootstrapIsImported(entry))
            missing.Add(new SetupStep(
                "Import the i18n bootstrap from the app entry point",
                entry is null
                    ? "No entry file was found (looked for src/main and src/index with .tsx, .ts, .jsx and " +
                      $".js). Add `import \"{specifier}\";` at the top of whichever module boots your app. " +
                      "Nothing calls i18n.init() until something imports it, and until then every " +
                      "rewritten t(\"Key\") renders the key instead of the copy."
                    : $"Add `import \"{specifier}\";` to {Rel(appRoot, entry)}, above the code that mounts " +
                      "the app. The bootstrap is what calls i18n.init(); until it is imported every " +
                      "rewritten t(\"Key\") renders the key instead of the copy.",
                SetupSeverity.Blocking,
                Automatic: entry is not null,
                File: entry is null ? null : Rel(appRoot, entry)));

        return new LocalizationSetup(missing);
    }

    public static IReadOnlyList<SetupStep> Apply(LocalizationRequest request, string resourceDir, IRunLog log)
    {
        var done = new List<SetupStep>();
        var setup = Inspect(request, resourceDir);

        var manifestPath = FindPackageJson(request.ProjectPath);
        if (manifestPath is null) return done;

        var appRoot = Path.GetDirectoryName(manifestPath) ?? request.ProjectPath;

        // The manifest is parsed and rewritten once however many packages were missing, so the file
        // sees a single edit rather than one per dependency.
        var packageSteps = setup.Missing
            .Where(s => s.File == "package.json" && s.Automatic)
            .ToList();

        if (packageSteps.Count > 0)
        {
            var wanted = new List<(string Name, string Version)>();

            foreach (var step in packageSteps)
            {
                if (step.Title.Contains("react-i18next", StringComparison.Ordinal))
                    wanted.Add(("react-i18next", ReactI18NextVersion));
                else if (step.Title.Contains("vue-i18n", StringComparison.Ordinal))
                    wanted.Add(("vue-i18n", VueI18NVersion));
                else if (step.Title.Contains("i18next", StringComparison.Ordinal))
                    wanted.Add(("i18next", I18NextVersion));
            }

            if (wanted.Count > 0 && AddDependencies(manifestPath, wanted, log))
            {
                done.AddRange(packageSteps);
                log.Info($"  updated {Rel(request.ProjectPath, manifestPath)}: " +
                         string.Join(", ", wanted.Select(w => w.Name)));
                log.Info("  run `npm install` (or yarn/pnpm) before building: the new packages are " +
                         "declared but not yet downloaded.");
            }
        }

        var entryStep = setup.Missing.FirstOrDefault(
            s => s.Automatic && s.Title.StartsWith("Import the i18n bootstrap", StringComparison.Ordinal));

        if (entryStep is not null)
        {
            var entry = FindEntry(appRoot);

            if (entry is not null && InsertBootstrapImport(entry, BootstrapSpecifier(entry, appRoot, resourceDir)))
            {
                done.Add(entryStep);
                log.Info($"  updated {Rel(request.ProjectPath, entry)}: imported the i18n bootstrap");
            }
        }

        return done;
    }

    /// <summary>
    /// Adds the packages to the existing dependencies block, or creates that block when the manifest
    /// has none. The file is edited as text rather than reserialised: a manifest is hand-maintained,
    /// and round-tripping it through a JSON writer would reorder keys and reflow the whole thing for
    /// the sake of two added lines.
    /// </summary>
    private static bool AddDependencies(
        string path, IReadOnlyList<(string Name, string Version)> packages, IRunLog log)
    {
        var text = Read(path);
        if (text is null) return false;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var key = DependenciesKey().Match(text);
        var entries = packages.Select(p => $"\"{p.Name}\": \"{p.Version}\"").ToList();

        string patched;

        if (key.Success)
        {
            var open = text.IndexOf('{', key.Index + key.Length - 1);
            if (open < 0)
            {
                log.Warn($"{path} has a dependencies key that is not an object; add the packages by hand.");
                return false;
            }

            var outer = IndentOf(text, key.Index);
            var indent = outer + "  ";
            var block = string.Join(newline, entries.Select(e => indent + e + ","));

            // An empty block has nothing for the trailing comma to precede, and a stray comma is a
            // parse error in JSON however forgiving the reader. It also has its closing brace on the
            // same line, so one is put on a line of its own rather than left glued to the last entry.
            if (IsEmptyObject(text, open))
                block = block.TrimEnd(',') + newline + outer;

            patched = text[..(open + 1)] + newline + block + text[(open + 1)..];
        }
        else
        {
            var root = text.IndexOf('{');
            if (root < 0)
            {
                log.Warn($"{path} is not a JSON object; add the packages by hand.");
                return false;
            }

            var block = string.Join(newline, entries.Select(e => "    " + e + ",")).TrimEnd(',');
            patched = text[..(root + 1)] + newline + "  \"dependencies\": {" + newline + block + newline +
                      "  }," + text[(root + 1)..];
        }

        try
        {
            File.WriteAllText(path, patched, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{path} could not be written; add the packages by hand.");
            return false;
        }
    }

    /// <summary>
    /// Puts the bootstrap import directly below the file's existing imports, which is where a side
    /// effect import has to sit: i18n.init() must have run before the first component renders.
    /// </summary>
    private static bool InsertBootstrapImport(string path, string specifier)
    {
        var text = Read(path);
        if (text is null) return false;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var at = EndOfImportBlock(lines);
        lines.Insert(at, $"import \"{specifier}\";");

        try
        {
            File.WriteAllText(path, string.Join(newline, lines), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Line index just past the leading import block. Multi-line imports are tracked to their
    /// terminator so the insertion never lands in the middle of one, and a file whose first
    /// statement is not an import gets the line placed under any leading comments or "use client"
    /// directive instead.
    /// </summary>
    private static int EndOfImportBlock(List<string> lines)
    {
        var last = -1;
        var preamble = 0;
        var inImport = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (inImport)
            {
                if (line.EndsWith(';') || line.Contains(" from ", StringComparison.Ordinal))
                {
                    last = i;
                    inImport = false;
                }

                continue;
            }

            if (line.StartsWith("import ", StringComparison.Ordinal) ||
                line.StartsWith("import\"", StringComparison.Ordinal) ||
                line.StartsWith("import'", StringComparison.Ordinal))
            {
                if (line.EndsWith(';') || line.Contains(" from ", StringComparison.Ordinal)) last = i;
                else inImport = true;

                continue;
            }

            // Comments and a leading directive prologue may precede the imports; real code ends the
            // search, because anything after it has already had a chance to run.
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*') ||
                line.StartsWith("\"use ", StringComparison.Ordinal) || line.StartsWith("'use ", StringComparison.Ordinal))
            {
                if (last < 0) preamble = i + 1;
                continue;
            }

            break;
        }

        return last >= 0 ? last + 1 : preamble;
    }

    /// <summary>
    /// Module specifier the entry file should import, relative to itself. The bootstrap is written
    /// into the resource directory, which is only next to the entry file some of the time, so the
    /// path is computed rather than assumed - a wrong specifier is a build error, and this tool
    /// exists to avoid handing people those.
    /// </summary>
    private static string BootstrapSpecifier(string? entry, string appRoot, string resourceDir)
    {
        var from = entry is null ? appRoot : Path.GetDirectoryName(entry) ?? appRoot;

        try
        {
            var relative = Path.GetRelativePath(from, Path.Combine(resourceDir, "i18n")).Replace('\\', '/');

            return relative.StartsWith('.') ? relative : "./" + relative;
        }
        catch (ArgumentException)
        {
            return "./i18n";
        }
    }

    /// <summary>
    /// True when the entry already pulls in a local i18n module. Only relative and aliased
    /// specifiers count: `import { t } from "i18next"` names the library, not the bootstrap, and
    /// importing the library is exactly what leaves it uninitialised.
    /// </summary>
    private static bool BootstrapIsImported(string? entry)
    {
        if (entry is null) return false;

        var text = Read(entry);
        if (text is null) return false;

        return LocalI18nImport().IsMatch(text);
    }

    private static string? FindEntry(string appRoot)
    {
        foreach (var candidate in EntryCandidates)
        {
            var path = Path.Combine(appRoot, candidate.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable candidate simply is not the entry point.
            }
        }

        return null;
    }

    /// <summary>
    /// Every declared package name, from dependencies and devDependencies alike. devDependencies
    /// counts for the "is it already there" question because bundled apps legitimately keep runtime
    /// libraries there; anything added goes into dependencies regardless.
    /// </summary>
    private static HashSet<string>? ReadDependencies(string path)
    {
        var text = Read(path);
        if (text is null) return null;

        try
        {
            using var document = JsonDocument.Parse(
                text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(section, out var block) &&
                    block.ValueKind == JsonValueKind.Object)
                    foreach (var entry in block.EnumerateObject())
                        names.Add(entry.Name);

            return names;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Root manifest, or the first one a directory down, matching how <see cref="WebAdapter"/>
    /// itself locates the manifest so inspection and detection never disagree about which app is
    /// being looked at.
    /// </summary>
    private static string? FindPackageJson(string projectRoot)
    {
        var root = Path.Combine(projectRoot, "package.json");
        if (File.Exists(root)) return root;

        try
        {
            return Directory
                .EnumerateDirectories(projectRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.') &&
                            !Path.GetFileName(d).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "package.json"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsEmptyObject(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] == '}';
        }

        return true;
    }

    /// <summary>Leading whitespace of the line the offset falls on, so additions line up with it.</summary>
    private static string IndentOf(string text, int offset)
    {
        var start = text.LastIndexOf('\n', Math.Min(offset, text.Length - 1)) + 1;
        var end = start;

        while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;

        return text[start..end];
    }

    private static string Rel(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
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

    [GeneratedRegex(@"""dependencies""\s*:\s*", RegexOptions.None)]
    private static partial Regex DependenciesKey();

    // A relative or alias-rooted specifier whose last segment is an i18n module; "i18next" and
    // "react-i18next" are bare package names and deliberately do not match.
    [GeneratedRegex(@"import\s+[^;""']*[""'](?:\.{1,2}/|@/|~/)[^""']*i18n[^""']*[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LocalI18nImport();
}
