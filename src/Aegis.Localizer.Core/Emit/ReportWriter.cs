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

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms;

namespace Aegis.Localizer.Emit;

/// <summary>
/// Writes the review artefacts. A dry run produces nothing else, which is the point: a human reads
/// the diff before any source file is touched.
/// </summary>
public static class ReportWriter
{
    public static string Write(LocalizationResult result, LocalizationRequest request)
    {
        Directory.CreateDirectory(request.WorkDir);

        var suffix = string.Join("-", request.Languages);
        if (suffix.Length == 0) suffix = "scan";

        var markdownPath = Path.Combine(request.WorkDir, $"report.{suffix}.md");
        File.WriteAllText(markdownPath, Markdown(result, request), new UTF8Encoding(false));

        var jsonPath = Path.Combine(request.WorkDir, $"report.{suffix}.json");
        File.WriteAllText(jsonPath, Json(result, request), new UTF8Encoding(false));

        return markdownPath;
    }

    private static string Json(LocalizationResult result, LocalizationRequest request)
    {
        var payload = new
        {
            project = request.ProjectPath,
            platform = result.Platform,
            format = result.Format.ToString(),
            sourceLanguage = request.SourceLanguage,
            languages = request.Languages,
            applied = request.Apply,
            generatedUtc = DateTime.UtcNow,
            elapsedSeconds = Math.Round(result.Elapsed.TotalSeconds, 1),
            tokens = new { input = result.InputTokens, output = result.OutputTokens },
            rewriteBlocked = result.RewriteBlocked,
            setup = new
            {
                ready = result.Setup.IsReady,
                applied = result.SetupApplied.Select(s => new { s.Title, s.File }),
                missing = result.Setup.Missing.Select(s => new
                {
                    s.Title,
                    s.Detail,
                    severity = s.Severity.ToString(),
                    s.Automatic,
                    s.File
                })
            },
            bundles = result.Written.ToDictionary(kv => kv.Key, kv => kv.Value.Path),
            localized = result.Localized.Select(e => new
            {
                key = e.Key,
                source = e.Candidate.Text,
                translations = e.Translations,
                file = e.Candidate.RelativePath,
                line = e.Candidate.Line,
                kind = e.Candidate.Kind.ToString(),
                reason = e.Reason
            }),
            rejected = result.Rejected.Select(e => new
            {
                source = e.Candidate.Text,
                file = e.Candidate.RelativePath,
                line = e.Candidate.Line,
                reason = e.Reason
            })
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string Markdown(LocalizationResult result, LocalizationRequest request)
    {
        var sb = new StringBuilder();
        var languages = request.Languages;

        sb.AppendLine("# Localization report");
        sb.AppendLine();
        sb.AppendLine($"- Project: `{request.ProjectPath}`");
        sb.AppendLine($"- Platform: {result.Platform} · format: {result.Format}");
        sb.AppendLine($"- Source language: {Describe(request.SourceLanguage)}");
        sb.AppendLine($"- Targets: {string.Join(", ", languages.Select(Describe))}");
        sb.AppendLine($"- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC in {result.Elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"- Mode: {(request.Apply ? "**applied** (sources rewritten)" : "dry run (sources untouched)")}");
        sb.AppendLine($"- Localized: **{result.Localized.Count}** occurrences · rejected as non-UI: {result.Rejected.Count}");
        sb.AppendLine($"- Tokens: {result.InputTokens:N0} in / {result.OutputTokens:N0} out");
        sb.AppendLine();

        if (!result.Setup.IsReady || result.SetupApplied.Count > 0)
        {
            sb.AppendLine("## Localization support");
            sb.AppendLine();

            if (result.SetupApplied.Count > 0)
            {
                sb.AppendLine("Added by this run:");
                sb.AppendLine();
                foreach (var step in result.SetupApplied)
                    sb.AppendLine($"- {step.Title}{(step.File is null ? "" : $" (`{step.File}`)")}");
                sb.AppendLine();
            }

            if (!result.Setup.IsReady)
            {
                sb.AppendLine("Still missing:");
                sb.AppendLine();

                foreach (var step in result.Setup.Missing)
                {
                    var severity = step.Severity == SetupSeverity.Blocking
                        ? "**required**"
                        : "recommended";

                    sb.AppendLine($"### {step.Title} ({severity})");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(step.Detail);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            if (result.RewriteBlocked)
            {
                sb.AppendLine("> **The sources were not rewritten.** Without the required support above, " +
                              "the rewritten project would not build. The translations were still written " +
                              "to the bundles, so nothing is lost — deal with the above and run again.");
                sb.AppendLine();
            }
        }

        if (result.Languages.Count > 0)
        {
            sb.AppendLine("## Coverage");
            sb.AppendLine();
            sb.AppendLine("| Language | Total | New | Changed | Redone | Already done | Still missing |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

            foreach (var outcome in result.Languages)
            {
                var missing = outcome.Total - outcome.AlreadyTranslated - outcome.SentTotal;

                sb.AppendLine(
                    $"| {outcome.Language} | {outcome.Total} " +
                    $"| {outcome.Sent.GetValueOrDefault(TranslationReason.Missing)} " +
                    $"| {outcome.Sent.GetValueOrDefault(TranslationReason.SourceChanged)} " +
                    $"| {outcome.Sent.GetValueOrDefault(TranslationReason.Forced)} " +
                    $"| {outcome.AlreadyTranslated} | {Math.Max(0, missing)} |");
            }

            sb.AppendLine();
        }

        if (result.Written.Count > 0)
        {
            sb.AppendLine("## Bundles");
            sb.AppendLine();
            foreach (var (culture, written) in result.Written)
                sb.AppendLine($"- `{culture}` → `{written.Path}` (+{written.Added} new, {written.Updated} updated)");
            sb.AppendLine();
        }

        if (result.Rewrite is { } rewrite)
        {
            sb.AppendLine("## Rewrite");
            sb.AppendLine();
            sb.AppendLine($"- {rewrite.Replacements} replacements in {rewrite.FilesChanged} files");
            sb.AppendLine($"- {rewrite.NotRewritable} occurrences left in place (not safely rewritable)");
            sb.AppendLine($"- Backups: `{rewrite.BackupDirectory}`");
            sb.AppendLine();
        }

        sb.AppendLine("## Localized strings");
        sb.AppendLine();

        foreach (var fileGroup in result.Localized
                     .GroupBy(e => e.Candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### `{fileGroup.Key}`");
            sb.AppendLine();
            sb.AppendLine("| Line | Key | Source | " + string.Join(" | ", languages) + " |");
            sb.AppendLine("|---:|---|---|" + string.Concat(languages.Select(_ => "---|")));

            foreach (var e in fileGroup.OrderBy(e => e.Candidate.Line))
            {
                var cells = languages.Select(l =>
                    Cell(e.Translations.TryGetValue(l, out var t) ? t : string.Empty));

                sb.AppendLine($"| {e.Candidate.Line} | `{e.Key}` | {Cell(e.Candidate.Text)} | {string.Join(" | ", cells)} |");
            }

            sb.AppendLine();
        }

        if (result.Rejected.Count > 0)
        {
            sb.AppendLine("## Skipped as non-UI");
            sb.AppendLine();
            sb.AppendLine("| File | Line | String | Why |");
            sb.AppendLine("|---|---:|---|---|");

            foreach (var e in result.Rejected
                         .OrderBy(e => e.Candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(e => e.Candidate.Line))
            {
                sb.AppendLine($"| `{e.Candidate.RelativePath}` | {e.Candidate.Line} | {Cell(e.Candidate.Text)} | {Cell(e.Reason)} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Describe(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return $"{culture.EnglishName} ({culture.Name})";
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    /// <summary>Makes a string safe to drop into a markdown table cell.</summary>
    private static string Cell(string s)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        if (one.Length > 160) one = one[..160] + "...";
        return one.Length == 0 ? "*(empty)*" : one;
    }
}
