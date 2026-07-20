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

/// <summary>
/// What the tool learns from the project it is working on.
///
/// The valuable signal a localized project produces is its people correcting the machine. Those
/// corrections have to survive, be reused, and teach the next translation — otherwise every run
/// starts from nothing and the team learns not to touch the bundles.
/// </summary>
public class LearningTests
{
    private static void Write(TempFolder temp, params string[] strings)
    {
        File.WriteAllText(Path.Combine(temp.Path, "Demo.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><RootNamespace>Demo</RootNamespace></PropertyGroup>
            </Project>
            """);

        var members = string.Join("\n", strings.Select((s, i) => $"    public string S{i} => \"{s}\";"));
        File.WriteAllText(Path.Combine(temp.Path, "Screen.cs"),
            $"namespace Demo;\n\npublic class Screen\n{{\n{members}\n}}\n");
    }

    private static LocalizationRequest Request(string path, bool retranslate = false) => new()
    {
        ProjectPath = path,
        Languages = ["ru"],
        Retranslate = retranslate,
        UseCache = false
    };

    private static readonly ResxStore Store = new();

    private static ResourceLocation Ru(string dir) => new(dir, "AppResources", "ru", "en");

    private static void HandEdit(string dir, string key, string value)
    {
        var current = Store.Read(Ru(dir)).ToDictionary(kv => kv.Key, kv => kv.Value);
        current[key] = value;

        File.Delete(Store.ResolvePath(Ru(dir)));
        Store.Write(Ru(dir), current);
    }

    /// <summary>
    /// The one that would teach people never to touch a bundle: a correction must survive the flag
    /// whose whole job is to redo everything.
    /// </summary>
    [Fact]
    public async Task AHandCorrectedTranslationSurvivesRetranslate()
    {
        using var temp = new TempFolder();
        Write(temp, "Save changes");

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));
        var key = Store.Read(Ru(first.ResourceDirectory)).Keys.Single();

        HandEdit(first.ResourceDirectory, key, "Сохранить");

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, retranslate: true));

        Assert.Equal("Сохранить", Store.Read(Ru(result.ResourceDirectory))[key]);
        Assert.Equal(1, Assert.Single(result.Languages).HumanEdited);
    }

    /// <summary>The same copy elsewhere inherits the wording a person chose, without asking again.</summary>
    [Fact]
    public async Task ACorrectedWordingIsReusedForTheSameCopyUnderAnotherKey()
    {
        using var temp = new TempFolder();
        Write(temp, "Save changes");

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));
        var key = Store.Read(Ru(first.ResourceDirectory)).Keys.Single();
        HandEdit(first.ResourceDirectory, key, "Сохранить");

        // Teach the tool the correction, then introduce the same copy under a second key.
        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        var neutral = new ResourceLocation(first.ResourceDirectory, "AppResources", null, "en");
        var source = Store.Read(neutral).ToDictionary(kv => kv.Key, kv => kv.Value);
        source["SecondPlace"] = "Save changes";
        Store.Write(neutral, source);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));
        var russian = Store.Read(Ru(result.ResourceDirectory));

        Assert.Equal("Сохранить", russian["SecondPlace"]);
        Assert.Equal(1, Assert.Single(result.Languages).ReusedApproved);
    }

    /// <summary>New copy is translated in the light of what the project has already decided.</summary>
    [Fact]
    public async Task CorrectionsAreShownToTheModelWhenTranslatingNewCopy()
    {
        using var temp = new TempFolder();
        Write(temp, "Save changes");

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));
        var key = Store.Read(Ru(first.ResourceDirectory)).Keys.Single();
        HandEdit(first.ResourceDirectory, key, "Сохранить");

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        // Now add genuinely new copy, so a translation request actually happens.
        Write(temp, "Save changes", "Discard changes");

        var model = new FakeModel();
        await new LocalizationRunner(model).RunAsync(Request(temp.Path));

        var prompt = Assert.Single(model.SystemPrompts, p => p.Contains("professional software localizer"));

        Assert.Contains("already settled on the following wordings", prompt);
        Assert.Contains("Сохранить", prompt);
    }

    /// <summary>
    /// The tool's own output is not a correction. Treating it as one would freeze every translation
    /// after the first run and quietly stop the tool from ever improving a bundle.
    /// </summary>
    [Fact]
    public async Task TheToolsOwnOutputIsNotMistakenForAHumanEdit()
    {
        using var temp = new TempFolder();
        Write(temp, "Save changes", "Your bookings");

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, retranslate: true));
        var outcome = Assert.Single(result.Languages);

        Assert.Equal(0, outcome.HumanEdited);
        Assert.Equal(outcome.Total, outcome.Sent.Values.Sum());
        Assert.NotEqual(0, first.Localized.Count);
    }

    /// <summary>
    /// A bundle written before this record existed must not be mistaken for a wall of corrections,
    /// or an upgrade would freeze every project's translations where they stand.
    /// </summary>
    [Fact]
    public async Task AnUnknownBundleIsNotAssumedToBeHandWritten()
    {
        using var temp = new TempFolder();
        Write(temp, "Save changes");

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        // Simulate an older version: bundles present, no memory of who wrote them.
        File.Delete(Path.Combine(temp.Path, ".aegis-localizer", "state.json"));

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, retranslate: true));
        var outcome = Assert.Single(result.Languages);

        Assert.Equal(0, outcome.HumanEdited);
        Assert.NotEqual(0, first.Localized.Count);
    }
}
