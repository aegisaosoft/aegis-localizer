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

using Aegis.Localizer.Platforms.Android;
using Aegis.Localizer.Platforms.Apple;
using Aegis.Localizer.Platforms.DotNet;
using Aegis.Localizer.Platforms.Flutter;
using Aegis.Localizer.Platforms.Web;

namespace Aegis.Localizer.Platforms;

/// <summary>Thrown when the request cannot be satisfied; hosts turn it into a user-facing message.</summary>
public sealed class LocalizerException(string message) : Exception(message);

public static class AdapterRegistry
{
    private static readonly List<ISourceAdapter> Adapters =
    [
        new DotNetAdapter(),
        new WebAdapter(),
        new AndroidAdapter(),
        new AppleAdapter(),
        new FlutterAdapter()
    ];

    public static IReadOnlyList<ISourceAdapter> All => Adapters;

    /// <summary>Registers or replaces an adapter by name. This is the extension point for new stacks.</summary>
    public static void Register(ISourceAdapter adapter)
    {
        Adapters.RemoveAll(a => string.Equals(a.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
        Adapters.Add(adapter);
    }

    /// <summary>Resolves the requested adapter, detecting it from the tree when asked for "auto".</summary>
    public static ISourceAdapter Resolve(LocalizationRequest request)
    {
        if (!string.Equals(request.Platform, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Adapters.FirstOrDefault(a => string.Equals(a.Name, request.Platform, StringComparison.OrdinalIgnoreCase))
                   ?? throw new LocalizerException(
                       $"Unknown platform '{request.Platform}'. Known: {string.Join(", ", Adapters.Select(a => a.Name))}.");
        }

        var ranked = Adapters
            .Select(a => (Adapter: a, Score: Score(a, request.ProjectPath)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count == 0 || ranked[0].Score <= 0)
            throw new LocalizerException(
                $"Could not detect the stack under {request.ProjectPath}. Pass --platform explicitly " +
                $"(known: {string.Join(", ", Adapters.Select(a => a.Name))}).");

        return ranked[0].Adapter;
    }

    /// <summary>A adapter that throws while sniffing an unfamiliar tree must not kill detection.</summary>
    private static int Score(ISourceAdapter adapter, string root)
    {
        try
        {
            return adapter.DetectionScore(root);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
