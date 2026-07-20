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

namespace Aegis.Localizer.Emit;

/// <summary>
/// Carries out the edits <see cref="Ai.SetupPlanner"/> proposed - after checking every one of them.
///
/// The model decides what to change; this decides whether it is allowed to. An edit is applied only
/// if it names a file the adapter offered, its anchor occurs exactly once, and the file is backed up
/// first. Anything that fails a check is skipped and reported, never guessed at, because these are
/// build files: a manifest edited slightly wrong does not fail loudly, it fails at somebody's next
/// release.
/// </summary>
public static class SetupApplier
{
    public sealed record Outcome(
        IReadOnlyList<PlannedStep> Applied,
        IReadOnlyList<string> Warnings);

    public static Outcome Apply(
        SetupPlan plan, SetupContext context, LocalizationRequest request, IRunLog log)
    {
        var allowed = context.Files.ToDictionary(f => Normalize(f.RelativePath), f => f, StringComparer.OrdinalIgnoreCase);
        var backupRoot = Path.Combine(request.WorkDir, "backup");
        var applied = new List<PlannedStep>();
        var warnings = new List<string>();

        foreach (var step in plan.Steps.Where(s => s.IsAutomatic))
        {
            var edits = new List<(string Path, string Text, bool Existed)>();
            var refused = false;

            foreach (var edit in step.Edits)
            {
                var problem = Prepare(edit, allowed, request, out var prepared);

                if (problem is not null)
                {
                    warnings.Add($"{step.Title}: {problem}");
                    refused = true;
                    break;
                }

                edits.Add(prepared);
            }

            // All or nothing per step: half of a step is a build file in a state nobody designed.
            if (refused || edits.Count == 0)
            {
                step.Edits.Clear();
                continue;
            }

            try
            {
                foreach (var (path, text, existed) in edits)
                {
                    if (existed) Backup(path, request.ProjectPath, backupRoot);

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    SourceFile.Write(path, text, hasBom: false);

                    log.Info($"  {(existed ? "updated" : "created")} {Path.GetRelativePath(request.ProjectPath, path)}");
                }

                applied.Add(step);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{step.Title}: could not write ({ex.Message})");
            }
        }

        foreach (var warning in warnings) log.Warn(warning);

        return new Outcome(applied, warnings);
    }

    /// <summary>
    /// Validates one edit and computes the resulting file text. Returns a reason when the edit is
    /// refused, or null when <paramref name="prepared"/> is good to write.
    /// </summary>
    private static string? Prepare(
        SetupEdit edit,
        IReadOnlyDictionary<string, ProjectFile> allowed,
        LocalizationRequest request,
        out (string Path, string Text, bool Existed) prepared)
    {
        prepared = default;

        if (!allowed.TryGetValue(Normalize(edit.File), out var file))
            return $"refers to {edit.File}, which was not offered for editing";

        var path = Path.GetFullPath(Path.Combine(request.ProjectPath, edit.File));
        var root = Path.GetFullPath(request.ProjectPath) + Path.DirectorySeparatorChar;

        // Belt and braces against a path that escapes the project.
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return $"refers to {edit.File}, which is outside the project";

        var exists = File.Exists(path);

        switch (edit.Kind)
        {
            case SetupEditKind.CreateFile:
                if (exists) return $"would create {edit.File}, which already exists";
                prepared = (path, Normalise(edit.Content), false);
                return null;

            case SetupEditKind.AppendToFile:
                if (!exists) return $"would append to {edit.File}, which does not exist";
                prepared = (path, EnsureTrailingNewline(file.Content) + Normalise(edit.Content), true);
                return null;

            case SetupEditKind.ReplaceText:
            case SetupEditKind.InsertAfter:
                if (!exists) return $"would edit {edit.File}, which does not exist";
                if (string.IsNullOrEmpty(edit.Anchor)) return $"{edit.File}: no anchor given";

                // The model always speaks \n; the file may not. Matching without translating first
                // fails on every CRLF file - which on Windows is most of them - and looks like the
                // model inventing an anchor that is plainly there.
                var newline = file.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var anchor = ToNewline(edit.Anchor, newline);
                var occurrences = Count(file.Content, anchor);

                if (occurrences == 0) return $"{edit.File}: anchor not found";
                if (occurrences > 1) return $"{edit.File}: anchor occurs {occurrences} times, so the target is ambiguous";

                var replacement = edit.Kind == SetupEditKind.ReplaceText
                    ? ToNewline(edit.Content, newline)
                    : anchor + ToNewline(edit.Content, newline);

                prepared = (path, file.Content.Replace(anchor, replacement, StringComparison.Ordinal), true);
                return null;

            default:
                return $"{edit.File}: unsupported edit kind";
        }
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Retypes text into the line endings the file being edited already uses.</summary>
    private static string ToNewline(string text, string newline) =>
        newline == "\n" ? Normalise(text) : Normalise(text).Replace("\n", newline, StringComparison.Ordinal);

    private static string EnsureTrailingNewline(string text) =>
        text.EndsWith('\n') ? text : text + "\n";

    private static int Count(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;

        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('.', '/');

    private static void Backup(string path, string projectRoot, string backupRoot)
    {
        var relative = Path.GetRelativePath(projectRoot, path);
        var target = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target, overwrite: true);
    }
}
