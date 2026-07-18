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
using System.IO.Compression;

namespace Aegis.Localizer.Web.Runs;

/// <summary>
/// A folder a run is allowed to touch.
///
/// Local mode hands back the user's own path: it is their machine and their code. Hosted mode never
/// does that - it only ever exposes a per-upload sandbox under the server's own temp root, because
/// an HTTP caller must not be able to name a path on the server.
/// </summary>
public sealed class Workspace(string root, bool isSandbox)
{
    public string Root { get; } = root;

    public bool IsSandbox { get; } = isSandbox;
}

public sealed class WorkspaceProvider(bool localMode)
{
    private readonly string _sandboxRoot =
        Path.Combine(Path.GetTempPath(), "aegis-localizer-web", Guid.NewGuid().ToString("N"));

    private readonly ConcurrentDictionary<string, Workspace> _uploads = new(StringComparer.Ordinal);

    public bool LocalMode { get; } = localMode;

    public string Remember(Workspace workspace)
    {
        var id = Guid.NewGuid().ToString("N");
        _uploads[id] = workspace;
        return id;
    }

    public Workspace? Recall(string id) => _uploads.TryGetValue(id, out var workspace) ? workspace : null;

    /// <summary>Local mode only: use a folder the user already has on this machine.</summary>
    public Workspace FromLocalPath(string path)
    {
        if (!LocalMode)
            throw new InvalidOperationException("This server is hosted; upload a .zip instead of naming a path.");

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Folder not found: {full}");

        return new Workspace(full, isSandbox: false);
    }

    /// <summary>
    /// Extracts an uploaded archive into a fresh sandbox. Entry paths are resolved and checked
    /// against the sandbox root before anything is written: a crafted archive with `../` entries
    /// would otherwise write anywhere the server process can reach.
    /// </summary>
    public async Task<Workspace> FromArchiveAsync(Stream archive, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(_sandboxRoot, id);
        Directory.CreateDirectory(root);

        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing archive entry outside the sandbox: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var source = entry.Open();
            await using var destination = File.Create(target);
            await source.CopyToAsync(destination, ct);
        }

        // Archives usually wrap everything in a single top-level folder; scan that instead of a
        // directory whose only content is another directory.
        var entries = Directory.GetFileSystemEntries(root);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return new Workspace(entries[0], isSandbox: true);

        return new Workspace(root, isSandbox: true);
    }

    /// <summary>Zips the run's output so the browser can download it.</summary>
    public static byte[] PackResults(LocalizationResult result, LocalizationRequest request)
    {
        using var buffer = new MemoryStream();

        // A ZipArchive opened for writing cannot be queried for what it already holds, so the
        // names are tracked here. The same file is reachable through more than one list below, and
        // a zip with duplicate entries opens on Windows but confuses everything else.
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var written in result.Written.Values)
                AddFile(zip, written.Path, request.ProjectPath, added);

            if (Directory.Exists(result.ResourceDirectory))
                foreach (var file in Directory.EnumerateFiles(result.ResourceDirectory, "*", SearchOption.AllDirectories))
                    AddFile(zip, file, request.ProjectPath, added);

            if (result.ReportPath is not null) AddFile(zip, result.ReportPath, request.ProjectPath, added);

            // An applied run changed the sources too, so ship those rather than make the user
            // reconstruct them from a diff.
            if (result.Rewrite is { FilesChanged: > 0 })
                foreach (var file in result.Localized
                             .Select(e => e.Candidate.FilePath)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    AddFile(zip, file, request.ProjectPath, added);
        }

        return buffer.ToArray();
    }

    private static void AddFile(ZipArchive zip, string path, string root, HashSet<string> added)
    {
        if (!File.Exists(path)) return;

        var name = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (name.StartsWith("..", StringComparison.Ordinal)) name = Path.GetFileName(path);

        if (!added.Add(name)) return;

        zip.CreateEntryFromFile(path, name);
    }
}
