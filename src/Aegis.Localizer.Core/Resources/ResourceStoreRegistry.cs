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

namespace Aegis.Localizer.Resources;

public static class ResourceStoreRegistry
{
    private static readonly Dictionary<ResourceFormat, IResourceStore> Stores = new()
    {
        [ResourceFormat.Resx] = new ResxStore(),
        [ResourceFormat.I18NextJson] = new I18NextJsonStore(),
        [ResourceFormat.AndroidXml] = new AndroidXmlStore(),
        [ResourceFormat.AppleStrings] = new AppleStringsStore(),
        [ResourceFormat.FlutterArb] = new FlutterArbStore()
    };

    /// <summary>Registers or replaces the store for a format. Used to plug in new ecosystems.</summary>
    public static void Register(IResourceStore store) => Stores[store.Format] = store;

    public static IResourceStore Get(ResourceFormat format) =>
        Stores.TryGetValue(format, out var store)
            ? store
            : throw new NotSupportedException($"No resource store is registered for {format}.");

    public static bool IsSupported(ResourceFormat format) => Stores.ContainsKey(format);

    public static IReadOnlyCollection<ResourceFormat> Supported => Stores.Keys;
}
