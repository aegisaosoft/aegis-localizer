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

/// <summary>Resource file layouts the tool can read and write.</summary>
public enum ResourceFormat
{
    /// <summary>.NET: AppResources.resx / AppResources.ru.resx.</summary>
    Resx,

    /// <summary>i18next and friends: locales/ru/translation.json, flat keys.</summary>
    I18NextJson,

    /// <summary>Android: res/values-ru/strings.xml.</summary>
    AndroidXml,

    /// <summary>Apple: ru.lproj/Localizable.strings.</summary>
    AppleStrings,

    /// <summary>Flutter: l10n/app_ru.arb.</summary>
    FlutterArb,

    /// <summary>gettext: locale/ru/LC_MESSAGES/messages.po.</summary>
    GettextPo
}

/// <summary>
/// Identifies one bundle.
///
/// <paramref name="Culture"/> is null for the source-language bundle, which several formats place
/// differently from the translated ones - .NET has an unqualified AppResources.resx and Android has
/// a plain values/ folder. Formats with no such concept (i18next, Apple, Flutter) need a real
/// language code for that bundle, and take it from <paramref name="SourceCulture"/>; hardcoding
/// "en" there ships German source strings in an en/ folder.
/// </summary>
public sealed record ResourceLocation(string Directory, string BaseName, string? Culture, string SourceCulture = "en")
{
    /// <summary>The culture to name a file after, for formats that always need one.</summary>
    public string EffectiveCulture => Culture ?? SourceCulture;
}

public sealed record ResourceWriteResult(string Path, int Added, int Updated);

/// <summary>
/// Persists translations in one ecosystem's native format. Implementations must MERGE rather than
/// overwrite: hand-edited translations and keys from earlier runs have to survive, because the
/// user's already-rewritten code depends on them.
/// </summary>
public interface IResourceStore
{
    ResourceFormat Format { get; }

    /// <summary>Absolute path of the file backing this location.</summary>
    string ResolvePath(ResourceLocation location);

    /// <summary>Existing key/value pairs; empty when the bundle does not exist yet.</summary>
    IReadOnlyDictionary<string, string> Read(ResourceLocation location);

    /// <summary>
    /// Merges the values in, preserving anything already there that is not being replaced.
    ///
    /// <paramref name="comments"/> carries the source location of each string. It is provenance,
    /// not content: apply it only when a key is FIRST added, and never rewrite it afterwards.
    /// Refreshing it on every run makes the bundle churn - rewriting a source file inserts an
    /// import line, which shifts every line reference below it - so the user gets a dirty diff on
    /// every run even though nothing meaningful changed. It also protects a note a translator
    /// wrote by hand. Fresh locations always live in the report, which is regenerated each run.
    /// </summary>
    ResourceWriteResult Write(
        ResourceLocation location,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? comments = null);
}
