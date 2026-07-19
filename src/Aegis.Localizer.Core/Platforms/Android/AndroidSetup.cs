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

using System.Text.RegularExpressions;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Android;

/// <summary>
/// Checking the localization support an Android app needs, which is normally none.
///
/// Android resolves res/values-&lt;qualifier&gt;/ against the device locale in the resource loader
/// itself: aapt2 compiles every values folder it finds, R.string names do not change per locale, and
/// getString() picks the right table with no code, dependency or startup call involved. So the
/// expected answer really is Complete - but it is checked rather than assumed, because there is one
/// build setting that quietly undoes all of it.
///
/// That setting is the shipped-locale filter (resConfigs, resourceConfigurations, and localeFilters
/// in newer AGP). Its whole purpose is to strip resource folders the list does not name, so a
/// project carrying `resConfigs "en"` gets everything this tool just wrote deleted at package time.
/// The build succeeds and the app is still English, which is exactly what Recommended describes.
/// </summary>
internal static partial class AndroidSetup
{
    /// <summary>Enough to cover a large multi-module project without walking a whole monorepo.</summary>
    private const int MaxScripts = 40;

    public static LocalizationSetup Inspect(LocalizationRequest request, string resourceDir)
    {
        var missing = new List<SetupStep>();

        foreach (var script in GradleScripts(request.ProjectPath))
        {
            var text = Read(script);
            if (text is null) continue;

            var filter = LocaleFilter().Match(text);
            if (!filter.Success) continue;

            var shipped = Locales(filter.Groups["list"].Value);
            if (shipped.Count == 0) continue;

            var stripped = request.Languages
                .Where(l => !shipped.Contains(Language(l)))
                .ToList();

            if (stripped.Count == 0) continue;

            missing.Add(new SetupStep(
                $"Add the new locales to {filter.Groups["key"].Value} in {Path.GetFileName(script)}",
                $"{Rel(request.ProjectPath, script)} limits the locales packaged into the APK or bundle " +
                $"to: {string.Join(", ", shipped)}.\n" +
                $"Anything outside that list is stripped at package time, so the resources written for " +
                $"{string.Join(", ", stripped)} would never reach a device. Add them:\n" +
                $"  {filter.Groups["key"].Value} {Suggest(script, shipped, stripped)}\n" +
                "Left manual because this is your build configuration and the list may be deliberate - " +
                "some projects trim locales pulled in by libraries to keep the download small.",
                SetupSeverity.Recommended,
                Automatic: false,
                File: Rel(request.ProjectPath, script)));
        }

        // Nothing else to check. No i18n dependency exists to add, no bootstrap to import, and no
        // culture to select: the platform does all three.
        return missing.Count == 0 ? LocalizationSetup.Complete : new LocalizationSetup(missing);
    }

    /// <summary>
    /// Deliberately empty. The one gap this stack can have is a hand-written list in somebody's
    /// build script, in one of three syntaxes across two languages, and often written that way on
    /// purpose - so it is reported and left alone.
    /// </summary>
    public static IReadOnlyList<SetupStep> Apply(LocalizationRequest request, string resourceDir, IRunLog log) => [];

    /// <summary>Build scripts worth reading: real modules, not the copies under build output.</summary>
    private static IEnumerable<string> GradleScripts(string projectRoot) =>
        DirectoryWalk.Files(projectRoot, "build.gradle*")
            .Where(f => f.EndsWith("build.gradle", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOutput(projectRoot, f))
            .Take(MaxScripts);

    /// <summary>
    /// Locale tags out of a Gradle list, whatever quoting and separators it uses. Only the language
    /// subtag is kept: the filter matches on language, so listing "en" ships values-en-rGB too, and
    /// comparing full tags would report a gap that is not there.
    /// </summary>
    private static HashSet<string> Locales(string list)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match token in QuotedToken().Matches(list))
        {
            var value = token.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;

            // A resource qualifier may arrive as b+sr+Latn, pt-rBR or plain ru; the language is the
            // first subtag in every one of those spellings.
            var language = value.StartsWith("b+", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

            found.Add(Language(language));
        }

        return found;
    }

    private static string Language(string tag)
    {
        var cut = tag.IndexOfAny(['-', '_', '+']);
        return (cut < 0 ? tag : tag[..cut]).ToLowerInvariant();
    }

    /// <summary>The suggested replacement list, written in the dialect the script already uses.</summary>
    private static string Suggest(string script, HashSet<string> shipped, IReadOnlyList<string> stripped)
    {
        var all = shipped.Concat(stripped.Select(Language)).Distinct(StringComparer.OrdinalIgnoreCase);
        var quoted = string.Join(", ", all.Select(l => $"\"{l}\""));

        return script.EndsWith(".kts", StringComparison.OrdinalIgnoreCase)
            ? $"+= listOf({quoted})"
            : quoted;
    }

    private static bool IsBuildOutput(string projectRoot, string path)
    {
        try
        {
            return Path.GetRelativePath(projectRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => s.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                          s.Equals("intermediates", StringComparison.OrdinalIgnoreCase));
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

    // The three spellings of the same setting: resConfigs (Groovy and Kotlin DSL, AGP 7 and
    // earlier), resourceConfigurations (its AGP 8 replacement) and localeFilters (AGP 9). All of
    // them take a list of locale qualifiers and all of them strip what is not on it.
    [GeneratedRegex(
        @"(?<key>resConfigs|resourceConfigurations|localeFilters)\s*(?:\+?=)?\s*(?:listOf|setOf|mutableListOf)?\s*[\(\[]?(?<list>[^)\]\r\n]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LocaleFilter();

    [GeneratedRegex(@"[""'](?<value>[^""']*)[""']")]
    private static partial Regex QuotedToken();
}
