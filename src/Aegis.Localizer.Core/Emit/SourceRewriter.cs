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
using Aegis.Localizer.Io;
using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms;

namespace Aegis.Localizer.Emit;

/// <summary>
/// Replaces hardcoded literals with resource lookups. Three safety rules, because this is the only
/// stage that can damage someone else's codebase: every touched file is backed up first, a span is
/// edited only while its text still matches what the scanner saw, and a file that cannot be written
/// is left exactly as it was.
/// </summary>
public static class SourceRewriter
{
    public static RewriteSummary Apply(
        IReadOnlyList<LocalizationEntry> entries,
        ISourceAdapter adapter,
        LocalizationRequest request,
        IRunLog log)
    {
        var backupRoot = Path.Combine(request.WorkDir, "backup");
        var warnings = new List<string>();
        var filesChanged = 0;
        var replacements = 0;
        var notRewritable = 0;

        foreach (var group in entries.GroupBy(e => e.Candidate.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = group.Key;

            var file = SourceFile.TryRead(path);
            if (file is null)
            {
                warnings.Add($"{path}: not readable as UTF-8, left untouched");
                continue;
            }

            var original = file.Text;

            var text = new StringBuilder(original);
            var imports = new List<string>();
            var fileReplacements = 0;

            // Descending order keeps the offsets of the remaining spans valid as we edit.
            foreach (var entry in group.OrderByDescending(e => e.Candidate.SpanStart))
            {
                var candidate = entry.Candidate;

                // The extractor's veto wins over anything the adapter would produce.
                if (candidate.RewriteBlockedReason is not null)
                {
                    notRewritable++;
                    continue;
                }

                var plan = adapter.PlanRewrite(candidate, entry.Key, request);

                if (plan is null)
                {
                    notRewritable++;
                    continue;
                }

                // Verified against the buffer being edited, not the pristine original: with
                // descending spans the two agree, but only this version catches an adapter that
                // emits overlapping or duplicate spans, which would otherwise cut the wrong range.
                if (candidate.SpanStart < 0 ||
                    candidate.SpanLength <= 0 ||
                    candidate.SpanStart + candidate.SpanLength > text.Length ||
                    text.ToString(candidate.SpanStart, candidate.SpanLength) != candidate.RawSpanText)
                {
                    warnings.Add($"{candidate.RelativePath}:{candidate.Line}: source changed since the scan, left alone");
                    notRewritable++;
                    continue;
                }

                text.Remove(candidate.SpanStart, candidate.SpanLength);
                text.Insert(candidate.SpanStart, plan.Replacement);
                fileReplacements++;

                if (!string.IsNullOrWhiteSpace(plan.RequiredImport)) imports.Add(plan.RequiredImport!);
            }

            if (fileReplacements == 0) continue;

            var updated = text.ToString();

            // Imports go in last, after every span edit, so they cannot shift offsets mid-pass.
            foreach (var import in imports.Distinct(StringComparer.Ordinal))
                updated = EnsureImport(updated, import);

            try
            {
                Backup(path, request.ProjectPath, backupRoot);
                SourceFile.Write(path, updated, file.HasBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{path}: cannot write ({ex.Message}), left unchanged");
                continue;
            }

            filesChanged++;
            replacements += fileReplacements;
        }

        foreach (var warning in warnings.Take(20)) log.Warn(warning);
        if (warnings.Count > 20) log.Warn($"...and {warnings.Count - 20} more");

        return new RewriteSummary(filesChanged, replacements, notRewritable, warnings, backupRoot);
    }

    /// <summary>
    /// Adds an import line unless the file already has it, placing it after the file's prologue of
    /// comments, directives and existing imports.
    /// </summary>
    private static string EnsureImport(string content, string import)
    {
        var trimmed = import.Trim();

        // Whole-line comparison. A substring match would call the import present when the same text
        // appears inside a comment or as the prefix of a longer import, and the rewritten file
        // would then reference a symbol it never imported.
        var lines = Lines(content);
        if (lines.Any(l => content.AsSpan(l.Start, l.Length).Trim().SequenceEqual(trimmed))) return content;

        var insertAtLine = 0;
        var afterLastImport = -1;
        var inBlockComment = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = content.AsSpan(lines[i].Start, lines[i].Length).Trim().ToString();

            if (inBlockComment)
            {
                insertAtLine = i + 1;
                if (line.Contains("*/", StringComparison.Ordinal)) inBlockComment = false;
                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !line.Contains("*/", StringComparison.Ordinal);
                insertAtLine = i + 1;
                continue;
            }

            if (line.Length == 0 || IsPrologue(line))
            {
                if (IsImport(line)) afterLastImport = i + 1;
                insertAtLine = i + 1;
                continue;
            }

            break;
        }

        // Prefer joining the existing import block. Falling back to the end of the prologue would
        // drop the line below any trailing file comment, where it reads as part of the code.
        var target = afterLastImport >= 0 ? afterLastImport : insertAtLine;

        // Inserted at a character offset rather than by rebuilding the file from split lines: a
        // rebuild normalises every line ending in the file, turning a one-line change into a
        // whole-file diff.
        var offset = target < lines.Count ? lines[target].Start : content.Length;
        var newline = target < lines.Count ? lines[target].Ending : DominantEnding(content);
        if (newline.Length == 0) newline = DominantEnding(content);

        return content.Insert(offset, trimmed + newline);
    }

    private readonly record struct Line(int Start, int Length, string Ending);

    /// <summary>Line spans with each line's own ending, so nothing has to be normalised.</summary>
    private static List<Line> Lines(string content)
    {
        var lines = new List<Line>();
        var start = 0;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;

            var hasCr = i > start && content[i - 1] == '\r';
            lines.Add(new Line(start, i - start - (hasCr ? 1 : 0), hasCr ? "\r\n" : "\n"));
            start = i + 1;
        }

        if (start < content.Length) lines.Add(new Line(start, content.Length - start, string.Empty));

        return lines;
    }

    private static string DominantEnding(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static readonly string[] ProloguePrefixes =
    [
        "//", "#", "<?", "using ", "import ", "package ", "@page", "@using", "@model",
        "'use ", "\"use ", "@import"
    ];

    private static bool IsPrologue(string line) =>
        ProloguePrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));

    private static readonly string[] ImportPrefixes = ["using ", "import ", "@using", "@import", "package "];

    private static bool IsImport(string line) =>
        ImportPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Byte-for-byte copy, never a re-encoded string. A backup exists to undo us, so it has to be
    /// the file as it was, down to the encoding and the byte-order mark.
    /// </summary>
    private static void Backup(string path, string projectRoot, string backupRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, path);
        var target = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target, overwrite: true);
    }
}
