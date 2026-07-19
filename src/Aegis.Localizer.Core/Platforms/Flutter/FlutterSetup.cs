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
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Flutter;

/// <summary>
/// Checking and building the localization support a Flutter app needs.
///
/// Flutter is the strictest of the supported stacks: `AppLocalizations` does not exist until
/// gen_l10n has generated it, and gen_l10n does not run until the pubspec says so. Rewriting a
/// project's Dart before that is set up leaves it not compiling at all, which is why the missing
/// pieces here are blocking rather than advisory.
/// </summary>
internal static class FlutterSetup
{
    public static LocalizationSetup Inspect(LocalizationRequest request, string resourceDir)
    {
        var missing = new List<SetupStep>();
        var pubspecPath = Path.Combine(request.ProjectPath, "pubspec.yaml");
        var pubspec = Read(pubspecPath);

        if (pubspec is null)
        {
            missing.Add(new SetupStep(
                "pubspec.yaml not found",
                "This does not look like a Flutter package, so localization cannot be set up automatically.",
                SetupSeverity.Blocking,
                Automatic: false));

            return new LocalizationSetup(missing);
        }

        if (!HasDependency(pubspec, "flutter_localizations"))
            missing.Add(new SetupStep(
                "Add flutter_localizations to pubspec.yaml",
                "dependencies:\n  flutter_localizations:\n    sdk: flutter\n" +
                "Without it the generated AppLocalizations class has nothing to build on.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "pubspec.yaml"));

        if (!HasDependency(pubspec, "intl"))
            missing.Add(new SetupStep(
                "Add intl to pubspec.yaml",
                "dependencies:\n  intl: any\nThe generated localizations import it.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "pubspec.yaml"));

        if (!HasGenerateFlag(pubspec))
            missing.Add(new SetupStep(
                "Enable generated localizations in pubspec.yaml",
                "flutter:\n  generate: true\nThis is what makes `flutter gen-l10n` run as part of the build.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "pubspec.yaml"));

        if (!File.Exists(Path.Combine(request.ProjectPath, "l10n.yaml")))
            missing.Add(new SetupStep(
                "Create l10n.yaml",
                $"Points gen_l10n at {Rel(request, resourceDir)} and at the " +
                $"app_{request.SourceLanguage}.arb template. Without it gen_l10n looks in lib/l10n " +
                "for an app_en.arb, which is only right by coincidence.",
                SetupSeverity.Blocking,
                Automatic: true,
                File: "l10n.yaml"));

        if (!WiresUpDelegates(request))
            missing.Add(new SetupStep(
                "Register the localization delegates on your app widget",
                "In your MaterialApp (or CupertinoApp/WidgetsApp), add:\n" +
                "  localizationsDelegates: AppLocalizations.localizationsDelegates,\n" +
                "  supportedLocales: AppLocalizations.supportedLocales,\n" +
                "Until this is there, AppLocalizations.of(context) returns null at run time and the " +
                "generated `!` throws. This one has to be done by hand: it is a change inside your " +
                "own widget tree, not a file this tool owns.",
                SetupSeverity.Blocking,
                Automatic: false));

        return new LocalizationSetup(missing);
    }

    public static IReadOnlyList<SetupStep> Apply(LocalizationRequest request, string resourceDir, IRunLog log)
    {
        var done = new List<SetupStep>();
        var setup = Inspect(request, resourceDir);

        foreach (var step in setup.Missing.Where(s => s.Automatic))
        {
            if (step.File == "l10n.yaml")
            {
                WriteL10nYaml(request, resourceDir);
                log.Info($"  created l10n.yaml");
                done.Add(step);
            }
        }

        // The pubspec edits are collected and written once, so the file is parsed and rewritten a
        // single time no matter how many of its keys were missing.
        var pubspecSteps = setup.Missing.Where(s => s.File == "pubspec.yaml" && s.Automatic).ToList();

        if (pubspecSteps.Count > 0 && PatchPubspec(request, log))
            done.AddRange(pubspecSteps);

        return done;
    }

    private static void WriteL10nYaml(LocalizationRequest request, string resourceDir)
    {
        var arbDir = Rel(request, resourceDir).Replace('\\', '/');

        var text = $"""
                    # Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
                    #
                    # Written by aegis-localizer. Points `flutter gen-l10n` at the generated ARB bundles.

                    arb-dir: {arbDir}
                    template-arb-file: app_{request.SourceLanguage}.arb
                    output-localization-file: app_localizations.dart
                    output-class: AppLocalizations
                    synthetic-package: false

                    """;

        File.WriteAllText(Path.Combine(request.ProjectPath, "l10n.yaml"), text, new UTF8Encoding(false));
    }

    /// <summary>
    /// Additive edits only: dependencies are appended under the block that already exists, and the
    /// generate flag under the existing `flutter:` key. Nothing is reordered or reformatted, because
    /// this is somebody's hand-maintained manifest.
    /// </summary>
    private static bool PatchPubspec(LocalizationRequest request, IRunLog log)
    {
        var path = Path.Combine(request.ProjectPath, "pubspec.yaml");
        var original = Read(path);
        if (original is null) return false;

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var wantsFlutterLocalizations = !HasDependency(original, "flutter_localizations");
        var wantsIntl = !HasDependency(original, "intl");
        var wantsGenerate = !HasGenerateFlag(original);

        if (wantsFlutterLocalizations || wantsIntl)
        {
            var at = TopLevelKey(lines, "dependencies:");

            if (at < 0)
            {
                log.Warn("pubspec.yaml has no dependencies: block; add flutter_localizations by hand.");
                return false;
            }

            var insert = EndOfBlock(lines, at);
            var additions = new List<string>();

            if (wantsFlutterLocalizations) additions.AddRange(["  flutter_localizations:", "    sdk: flutter"]);
            if (wantsIntl) additions.Add("  intl: any");

            lines.InsertRange(insert, additions);
        }

        if (wantsGenerate)
        {
            var at = TopLevelKey(lines, "flutter:");

            if (at < 0)
            {
                lines.Add("flutter:");
                lines.Add("  generate: true");
            }
            else
            {
                lines.Insert(at + 1, "  generate: true");
            }
        }

        File.WriteAllText(path, string.Join(newline, lines), new UTF8Encoding(false));
        log.Info("  updated pubspec.yaml");
        return true;
    }

    /// <summary>Index of a top-level key, i.e. one written hard against the left margin.</summary>
    private static int TopLevelKey(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].TrimEnd() == key.TrimEnd())
                return i;

        return -1;
    }

    /// <summary>First line after a block's indented body, which is where an addition belongs.</summary>
    private static int EndOfBlock(List<string> lines, int start)
    {
        var last = start;

        for (var i = start + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (!char.IsWhiteSpace(lines[i][0])) break;
            last = i;
        }

        return last + 1;
    }

    private static bool HasDependency(string pubspec, string name)
    {
        foreach (var raw in pubspec.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#')) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(name + ":", StringComparison.Ordinal) && line.Length > trimmed.Length)
                return true;
        }

        return false;
    }

    private static bool HasGenerateFlag(string pubspec) =>
        pubspec.Split('\n').Any(l => l.TrimEnd().TrimStart() is "generate: true");

    /// <summary>True when some Dart file already installs the localization delegates.</summary>
    private static bool WiresUpDelegates(LocalizationRequest request)
    {
        foreach (var file in DirectoryWalk.Files(request.ProjectPath, "*.dart"))
        {
            var text = Read(file);
            if (text is null) continue;

            if (text.Contains("localizationsDelegates", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string Rel(LocalizationRequest request, string path)
    {
        var relative = Path.GetRelativePath(request.ProjectPath, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
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
