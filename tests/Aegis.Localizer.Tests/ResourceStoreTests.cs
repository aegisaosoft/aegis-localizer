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

using Aegis.Localizer.Resources;
using Xunit;

namespace Aegis.Localizer.Tests;

/// <summary>Gives each test its own throwaway folder so nothing leaks between runs.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aegis-localizer-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp folder must not fail a green test run.
        }
    }
}

public class ResourceStoreTests
{
    private static readonly IResourceStore[] AllStores =
    [
        new ResxStore(),
        new I18NextJsonStore(),
        new AndroidXmlStore(),
        new AppleStringsStore(),
        new FlutterArbStore()
    ];

    public static TheoryData<IResourceStore> Stores
    {
        get
        {
            var data = new TheoryData<IResourceStore>();
            foreach (var store in AllStores) data.Add(store);
            return data;
        }
    }

    /// <summary>A format registered without a test would silently ship untested.</summary>
    [Fact]
    public void EveryRegisteredFormatIsCoveredByTheSuite()
    {
        var covered = AllStores.Select(s => s.Format).ToHashSet();

        Assert.All(ResourceStoreRegistry.Supported, format =>
            Assert.True(covered.Contains(format), $"Format {format} has no store test."));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void RoundTripsValues(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");

        var values = new Dictionary<string, string>
        {
            ["SaveButton"] = "Сохранить",
            ["Greeting"] = "Привет, {0}!"
        };

        var written = store.Write(location, values);

        Assert.Equal(2, written.Added);
        Assert.True(File.Exists(written.Path));
        Assert.Equal(values, store.Read(location));
    }

    /// <summary>
    /// The rule every store must obey: a run only adds and updates. Hand-edited translations and
    /// keys from earlier runs are what the user's already-rewritten code depends on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public void MergesInsteadOfOverwriting(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");

        store.Write(location, new Dictionary<string, string> { ["Kept"] = "Старый перевод" });
        var second = store.Write(location, new Dictionary<string, string> { ["Added"] = "Новый" });

        var all = store.Read(location);

        Assert.Equal(1, second.Added);
        Assert.Equal("Старый перевод", all["Kept"]);
        Assert.Equal("Новый", all["Added"]);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void RewritingTheSameValueIsANoOp(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");
        var values = new Dictionary<string, string> { ["SaveButton"] = "Сохранить" };

        store.Write(location, values);
        var second = store.Write(location, values);

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void ReadingAMissingBundleIsEmptyRatherThanAnError(IResourceStore store)
    {
        using var temp = new TempFolder();

        Assert.Empty(store.Read(new ResourceLocation(temp.Path, "Nothing", "ru")));
    }

    /// <summary>
    /// Source references are provenance, written once. Refreshing them every run churns the bundle,
    /// because rewriting a source inserts an import line and shifts every reference below it - the
    /// user would get a dirty diff on every run forever.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public void SourceReferencesAreWrittenOnceAndNeverRefreshed(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");
        var values = new Dictionary<string, string> { ["SaveButton"] = "Сохранить" };

        store.Write(location, values, new Dictionary<string, string> { ["SaveButton"] = "Screen.cs:14" });
        var afterFirst = File.ReadAllText(store.ResolvePath(location));

        // The same run a second time, with the line reference moved by an inserted import.
        store.Write(location, values, new Dictionary<string, string> { ["SaveButton"] = "Screen.cs:15" });

        Assert.Equal(afterFirst, File.ReadAllText(store.ResolvePath(location)));
    }

    /// <summary>Byte-for-byte stability under a repeated identical write, which is the common case.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public void RepeatedIdenticalWritesLeaveTheFileUnchanged(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");
        var values = new Dictionary<string, string> { ["A"] = "Один", ["B"] = "Два {0}" };
        var comments = new Dictionary<string, string> { ["A"] = "a.cs:1", ["B"] = "b.cs:2" };

        store.Write(location, values, comments);
        var afterFirst = File.ReadAllText(store.ResolvePath(location));

        store.Write(location, values, comments);

        Assert.Equal(afterFirst, File.ReadAllText(store.ResolvePath(location)));
    }

    /// <summary>Placeholders and quotes must survive whatever escaping a format needs.</summary>
    [Theory]
    [MemberData(nameof(Stores))]
    public void EscapingRoundTripsAwkwardValues(IResourceStore store)
    {
        using var temp = new TempFolder();
        var location = new ResourceLocation(temp.Path, "AppResources", "ru");

        var values = new Dictionary<string, string>
        {
            ["Placeholders"] = "Привет, {0}! У вас {1} сообщений.",
            ["Quotes"] = "He said \"stop\" and didn't move",
            ["Markup"] = "Read the <b>terms</b> & conditions",
            ["Percent"] = "%s items at %d%%"
        };

        store.Write(location, values);

        Assert.Equal(values, store.Read(location));
    }

    [Theory]
    [InlineData("ru", "values-ru")]
    [InlineData("pt-BR", "values-pt-rBR")]
    [InlineData("zh-Hans", "values-b+zh+Hans")]
    public void AndroidUsesTheLegacyLocaleFolderGrammar(string culture, string folder)
    {
        var path = new AndroidXmlStore().ResolvePath(new ResourceLocation("/x", "strings", culture));

        Assert.Contains(Path.Combine(folder, "strings.xml"), path);
    }

    [Fact]
    public void AppleUsesLprojFolders()
    {
        var path = new AppleStringsStore().ResolvePath(new ResourceLocation("/x", "Localizable", "ru"));

        Assert.Contains(Path.Combine("ru.lproj", "Localizable.strings"), path);
    }

    [Theory]
    [InlineData("ru", "app_ru.arb")]
    [InlineData("pt-BR", "app_pt_BR.arb")]
    public void FlutterUsesIntlFileNaming(string culture, string file)
    {
        var path = new FlutterArbStore().ResolvePath(new ResourceLocation("/x", "app", culture));

        Assert.EndsWith(file, path);
    }

    [Fact]
    public void ResxSeparatesTheSourceBundleFromTranslations()
    {
        var store = new ResxStore();

        var neutral = store.ResolvePath(new ResourceLocation("/x", "AppResources", null));
        var russian = store.ResolvePath(new ResourceLocation("/x", "AppResources", "ru"));

        Assert.EndsWith("AppResources.resx", neutral);
        Assert.EndsWith("AppResources.ru.resx", russian);
    }

    [Fact]
    public void I18NextUsesAFolderPerCulture()
    {
        var path = new I18NextJsonStore().ResolvePath(new ResourceLocation("/x", "translation", "pt-BR"));

        Assert.Contains(Path.Combine("pt-BR", "translation.json"), path);
    }
}
