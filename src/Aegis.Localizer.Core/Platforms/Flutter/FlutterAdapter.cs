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
using Aegis.Localizer.Model;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Flutter;

/// <summary>
/// The Flutter stack: Dart sources plus the ARB bundles that `flutter gen-l10n` compiles into the
/// generated AppLocalizations class.
/// </summary>
public sealed class FlutterAdapter : ISourceAdapter
{
    /// <summary>Import the generated accessor needs; added once per rewritten file.</summary>
    private const string GeneratedImport = "import 'package:flutter_gen/gen_l10n/app_localizations.dart';";

    public string Name => "flutter";

    public string DisplayName => "Flutter (Dart)";

    public IReadOnlyCollection<string> Extensions { get; } = [".dart"];

    public ResourceFormat DefaultFormat => ResourceFormat.FlutterArb;

    public int DetectionScore(string projectRoot)
    {
        var pubspec = Path.Combine(projectRoot, "pubspec.yaml");

        if (File.Exists(pubspec))
        {
            try
            {
                // A Dart package becomes a Flutter app the moment it depends on the SDK.
                if (File.ReadAllText(pubspec).Contains("flutter", StringComparison.OrdinalIgnoreCase)) return 100;
            }
            catch (IOException)
            {
                // Unreadable pubspec: fall through to the weaker signals below.
            }

            return 70;
        }

        return DirectoryWalk.Files(projectRoot, "*.dart").Any() ? 50 : 0;
    }

    /// <summary>Where gen_l10n looks by default (arb-dir in l10n.yaml).</summary>
    public string DefaultResourceDirectory(string projectRoot) => Path.Combine(projectRoot, "lib", "l10n");

    public IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request)
    {
        foreach (var candidate in DartExtractor.Extract(filePath, relativePath, content))
        {
            if (candidate.Kind == CandidateKind.Diagnostic && !request.IncludeDiagnostics) continue;
            yield return candidate;
        }
    }

    public RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request)
    {
        // AppLocalizations.of(context) needs a BuildContext in scope and cannot live in a const
        // expression. Only the extractor can tell, so it vetoes those positions through
        // RewriteBlockedReason; the rewriter honours it before ever reaching this method.
        if (candidate.RewriteBlockedReason is not null) return null;

        // A localized log line is not worth a resource, and interpolated copy would need a
        // placeholder-aware ARB entry rather than a plain lookup.
        if (candidate.Kind != CandidateKind.CodeLiteral) return null;
        if (candidate.IsInterpolated) return null;

        return new RewritePlan($"AppLocalizations.of(context)!.{key}", GeneratedImport);
    }

    public LocalizationSetup InspectSetup(LocalizationRequest request, string resourceDir) =>
        FlutterSetup.Inspect(request, resourceDir);

    public IReadOnlyList<SetupStep> ApplySetup(LocalizationRequest request, string resourceDir, IRunLog log) =>
        FlutterSetup.Apply(request, resourceDir, log);

    public void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log) =>
        // gen_l10n owns the accessor class, so generating one here would only collide with it.
        log.Info($"  no runtime glue needed: run `flutter gen-l10n` to build AppLocalizations " +
                 $"from the {keys.Count} keys in {resourceDir}");

    /// <summary>
    /// ARB keys become getters on the generated AppLocalizations class, which are
    /// lowerCamelCase. Applied here rather than in PlanRewrite, so the bundle and the rewritten
    /// Dart can never disagree about a name.
    /// </summary>
    public string NormalizeKey(string key) => ToLowerCamel(key);

    /// <summary>
    /// Keys arrive PascalCase because they double as C# identifiers elsewhere in the tool; ARB and
    /// the accessor gen_l10n generates are lowerCamelCase. A leading run of capitals is an acronym
    /// and is lowered whole ("URLTitle" -&gt; "urlTitle"), which is what dart format expects.
    /// </summary>
    private static string ToLowerCamel(string key)
    {
        if (key.Length == 0) return key;

        var upper = 0;
        while (upper < key.Length && char.IsUpper(key[upper])) upper++;

        // A single leading capital is just PascalCase; a run of them ending in a lower-case letter
        // is an acronym followed by the next word, whose first capital must survive.
        if (upper > 1 && upper < key.Length) upper--;
        if (upper == 0) return key;

        return new StringBuilder(key.Length)
            .Append(key[..upper].ToLowerInvariant())
            .Append(key[upper..])
            .ToString();
    }
}
