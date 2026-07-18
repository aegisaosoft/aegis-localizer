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

using Aegis.Localizer.Model;
using Aegis.Localizer.Resources;
using Xunit;

namespace Aegis.Localizer.Tests;

/// <summary>
/// Localizing a project is not one event. Strings get added and edited, translations get skipped,
/// languages get added months later. These cover running the tool repeatedly over a living project.
/// </summary>
public class IncrementalTests
{
    private static string Screen(params string[] strings) =>
        $$"""
          namespace Demo;

          public class Screen
          {
          {{string.Join("\n", strings.Select((s, i) => $"    public string S{i} => \"{s}\";"))}}
          }
          """;

    private static void Write(TempFolder temp, string code)
    {
        File.WriteAllText(Path.Combine(temp.Path, "Demo.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><RootNamespace>Demo</RootNamespace></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(temp.Path, "Screen.cs"), code);
    }

    private static LocalizationRequest Request(
        string path, string[] languages, bool apply = false, bool retranslate = false) => new()
    {
        ProjectPath = path,
        Languages = languages,
        Apply = apply,
        Retranslate = retranslate,
        UseCache = false
    };

    private static IReadOnlyDictionary<string, string> Bundle(string resourceDir, string? culture) =>
        new ResxStore().Read(new ResourceLocation(resourceDir, "AppResources", culture, "en"));

    /// <summary>
    /// The one that was broken: after a rewrite the literals are gone from the source, so a later
    /// run adding a language found almost nothing and wrote a bundle that looked complete but was
    /// missing most of the app.
    /// </summary>
    [Fact]
    public async Task ALanguageAddedAfterARewriteStillGetsEveryString()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings", "Waiting for approval"));

        var first = await new LocalizationRunner(new FakeModel())
            .RunAsync(Request(temp.Path, ["ru"], apply: true));

        var keys = Bundle(first.ResourceDirectory, null).Count;
        Assert.Equal(3, keys);

        // The source no longer holds the literals at all.
        Assert.DoesNotContain("\"Save changes\"", await File.ReadAllTextAsync(Path.Combine(temp.Path, "Screen.cs")));

        var second = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["de"]));

        Assert.Equal(keys, Bundle(second.ResourceDirectory, "de").Count);
        Assert.All(Bundle(second.ResourceDirectory, "de").Values, v => Assert.StartsWith("[de]", v));
    }

    /// <summary>Adding a string to the code translates that one and leaves the rest alone.</summary>
    [Fact]
    public async Task AStringAddedLaterIsTheOnlyOneTranslatedOnTheNextRun()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        Write(temp, Screen("Save changes", "Your bookings", "Cancel booking"));

        var model = new FakeModel();
        var result = await new LocalizationRunner(model).RunAsync(Request(temp.Path, ["ru"]));

        var outcome = Assert.Single(result.Languages);
        Assert.Equal(1, outcome.Sent.GetValueOrDefault(TranslationReason.Missing));
        Assert.Equal(2, outcome.AlreadyTranslated);
        Assert.Equal(3, Bundle(result.ResourceDirectory, "ru").Count);
    }

    /// <summary>A gap in a target bundle is filled without redoing everything around it.</summary>
    [Fact]
    public async Task AMissingTranslationIsFilledOnTheNextRun()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        // Simulate a translation that never landed: drop one key from the Russian bundle.
        var store = new ResxStore();
        var location = new ResourceLocation(first.ResourceDirectory, "AppResources", "ru", "en");
        var russian = Bundle(first.ResourceDirectory, "ru").ToDictionary(kv => kv.Key, kv => kv.Value);
        var dropped = russian.Keys.First();
        russian.Remove(dropped);
        File.Delete(store.ResolvePath(location));
        store.Write(location, russian);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        var outcome = Assert.Single(result.Languages);
        Assert.Equal(1, outcome.Sent.GetValueOrDefault(TranslationReason.Missing));
        Assert.True(Bundle(result.ResourceDirectory, "ru").ContainsKey(dropped));
    }

    /// <summary>
    /// Once a project has been rewritten the code no longer holds copy, so the source bundle is
    /// where people edit the English. A translation made from the old wording is stale and has to
    /// be redone - and nothing but recorded state can tell that apart from "the key is present".
    /// </summary>
    [Fact]
    public async Task EditingTheEnglishInTheBundleRetranslatesThatKey()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        var first = await new LocalizationRunner(new FakeModel())
            .RunAsync(Request(temp.Path, ["ru"], apply: true));

        var store = new ResxStore();
        var neutral = new ResourceLocation(first.ResourceDirectory, "AppResources", null, "en");
        var key = store.Read(neutral).First(kv => kv.Value == "Save changes").Key;

        // Reword the English the way a person would, now that the literal is gone from the code.
        store.Write(neutral, new Dictionary<string, string> { [key] = "Save your changes" });

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));
        var outcome = Assert.Single(result.Languages);

        Assert.Equal(1, outcome.Sent.GetValueOrDefault(TranslationReason.SourceChanged));
        Assert.Equal("[ru] Save your changes", Bundle(result.ResourceDirectory, "ru")[key]);
    }

    /// <summary>A run with nothing to do costs nothing and says so.</summary>
    [Fact]
    public async Task ARunWithNothingLeftToDoCallsNoModel()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        var model = new FakeModel();
        var result = await new LocalizationRunner(model).RunAsync(Request(temp.Path, ["ru"]));

        var outcome = Assert.Single(result.Languages);
        Assert.Equal(0, outcome.SentTotal);
        Assert.Equal(outcome.Total, outcome.AlreadyTranslated);

        // Classification still runs for the strings still visible in source; translation must not.
        Assert.DoesNotContain(model.SystemPrompts, p => p.Contains("professional software localizer"));
    }

    [Fact]
    public async Task RetranslateRedoesWorkThatWasAlreadyDone()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        var result = await new LocalizationRunner(new FakeModel())
            .RunAsync(Request(temp.Path, ["ru"], retranslate: true));

        var outcome = Assert.Single(result.Languages);
        Assert.Equal(outcome.Total, outcome.Sent.GetValueOrDefault(TranslationReason.Forced));
        Assert.Equal(0, outcome.AlreadyTranslated);
    }

    /// <summary>
    /// A refused translation must leave the bundle alone rather than filling it with the source
    /// string: a bundle full of English that claims to be Russian never gets retried.
    /// </summary>
    [Fact]
    public async Task ARefusedTranslationLeavesAGapForTheNextRunToFill()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes"));

        var broken = new FakeModel { Translate = (_, _) => string.Empty };
        var first = await new LocalizationRunner(broken).RunAsync(Request(temp.Path, ["ru"]));

        Assert.Empty(Bundle(first.ResourceDirectory, "ru"));

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));

        Assert.Single(Bundle(result.ResourceDirectory, "ru"));
    }

    /// <summary>Adding a second language must not disturb the first one's translations.</summary>
    [Fact]
    public async Task AddingALanguageLeavesTheExistingOnesUntouched()
    {
        using var temp = new TempFolder();
        Write(temp, Screen("Save changes", "Your bookings"));

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["ru"]));
        var russianBefore = Bundle(first.ResourceDirectory, "ru");

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, ["es"]));

        Assert.Equal(russianBefore, Bundle(result.ResourceDirectory, "ru"));
        Assert.Equal(russianBefore.Count, Bundle(result.ResourceDirectory, "es").Count);
    }
}
