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

namespace Aegis.Localizer.Scanning;

/// <summary>
/// The only way this tool should walk a tree it did not create.
///
/// `SearchOption.AllDirectories` is wrong here twice over. It follows directory symlinks, so a link
/// back to an ancestor - which real repositories contain - makes it descend until the path runs out
/// and the tool simply never finishes. And it throws out of the entire walk at the first directory
/// it cannot read, killing a scan of thousands of files over one permissions quirk.
/// </summary>
public static class DirectoryWalk
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    /// <summary>Every file under <paramref name="root"/>, links not followed, unreadable folders skipped.</summary>
    public static IEnumerable<string> Files(string root, string pattern = "*.*")
    {
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            // Belt and braces: a junction whose LinkTarget the runtime does not report still cannot
            // send the walk round a second time.
            if (!seen.Add(Resolve(directory))) continue;

            string[] files;
            string[] children;

            try
            {
                files = Directory.GetFiles(directory, pattern, Options);
                children = Directory.GetDirectories(directory, "*", Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files) yield return file;

            foreach (var child in children)
                if (!IsLink(child))
                    pending.Push(child);
        }
    }

    /// <summary>
    /// Every directory under <paramref name="root"/>, on the same terms. Needed because some
    /// project markers are folders rather than files (.xcodeproj, .xcworkspace, res).
    /// </summary>
    public static IEnumerable<string> Directories(string root, string pattern = "*")
    {
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!seen.Add(Resolve(directory))) continue;

            string[] children;
            string[] matches;

            try
            {
                children = Directory.GetDirectories(directory, "*", Options);
                matches = pattern == "*" ? children : Directory.GetDirectories(directory, pattern, Options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var match in matches) yield return match;

            foreach (var child in children)
                if (!IsLink(child))
                    pending.Push(child);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string Resolve(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return path;
        }
    }
}
