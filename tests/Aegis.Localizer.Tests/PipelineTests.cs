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
/// End-to-end runs against a throwaway project, driven by <see cref="FakeModel"/>. These cover the
/// behaviour that actually matters to a user: the right strings are picked, the bundles are
/// written, the sources still make sense, and running twice changes nothing.
/// </summary>
public class PipelineTests
{
    private const string Source =
        """
        namespace Demo;

        public class Screen
        {
            public string Title => "Your bookings";
            public string Query => "SELECT Id FROM Cars";
            public string Confirm(string car) => $"Booking for {car} confirmed";
            public bool Check(string s) => s == "pending";
        }
        """;

    private static string CreateProject(TempFolder temp)
    {
        File.WriteAllText(Path.Combine(temp.Path, "Demo.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <RootNamespace>Demo</RootNamespace>
              </PropertyGroup>
            </Project>
            """);

        var file = Path.Combine(temp.Path, "Screen.cs");
        File.WriteAllText(file, Source);
        return file;
    }

    private static LocalizationRequest Request(string path, bool apply = false, params string[] languages) => new()
    {
        ProjectPath = path,
        Languages = languages.Length == 0 ? ["ru"] : languages,
        Apply = apply,
        UseCache = false
    };

    [Fact]
    public async Task PicksCopyAndSkipsMachineStrings()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        Assert.Contains(result.Localized, e => e.Candidate.Text == "Your bookings");
        Assert.Contains(result.Localized, e => e.Candidate.Text == "Booking for {0} confirmed");

        // SQL is dropped by the pre-filter and the comparison by structural analysis: neither is
        // ever shown to the model, so neither can be localized by mistake.
        Assert.DoesNotContain(result.Candidates, c => c.Text.StartsWith("SELECT"));
        Assert.DoesNotContain(result.Candidates, c => c.Text == "pending");
    }

    [Fact]
    public async Task WritesOneBundlePerLanguagePlusTheSource()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var result = await new LocalizationRunner(new FakeModel())
            .RunAsync(Request(temp.Path, apply: false, "ru", "es"));

        Assert.Equal(3, result.Written.Count);
        Assert.All(result.Written.Values, w => Assert.True(File.Exists(w.Path)));

        var russian = new ResxStore().Read(new ResourceLocation(result.ResourceDirectory, "AppResources", "ru"));
        Assert.Contains(russian.Values, v => v.StartsWith("[ru] "));
    }

    [Fact]
    public async Task DryRunNeverTouchesTheSources()
    {
        using var temp = new TempFolder();
        var file = CreateProject(temp);

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        Assert.Equal(Source, File.ReadAllText(file));
    }

    [Fact]
    public async Task ApplyRewritesLiteralsAndBacksUpTheOriginal()
    {
        using var temp = new TempFolder();
        var file = CreateProject(temp);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));
        var rewritten = File.ReadAllText(file);

        Assert.NotNull(result.Rewrite);
        Assert.DoesNotContain("\"Your bookings\"", rewritten);
        Assert.Contains("L.", rewritten);

        // Untouchable constructs must survive verbatim.
        Assert.Contains("\"SELECT Id FROM Cars\"", rewritten);
        Assert.Contains("s == \"pending\"", rewritten);

        var backup = Path.Combine(result.Rewrite!.BackupDirectory, "Screen.cs");
        Assert.True(File.Exists(backup));
        Assert.Equal(Source, File.ReadAllText(backup));
    }

    [Fact]
    public async Task InterpolationBecomesAFormatCall()
    {
        using var temp = new TempFolder();
        var file = CreateProject(temp);

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        Assert.Contains("L.Format(", File.ReadAllText(file));
    }

    /// <summary>
    /// The regression that broke a rewritten project once: the generated accessor must be rebuilt
    /// from the merged bundle, so keys from earlier runs never disappear from it.
    /// </summary>
    [Fact]
    public async Task SecondRunIsANoOpAndKeepsEveryKey()
    {
        using var temp = new TempFolder();
        var file = CreateProject(temp);

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));
        var afterFirst = File.ReadAllText(file);
        var keysAfterFirst = KeysOf(first.ResourceDirectory);

        var second = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        Assert.Equal(afterFirst, File.ReadAllText(file));
        Assert.Equal(0, second.Rewrite!.Replacements);
        Assert.Equal(keysAfterFirst, KeysOf(second.ResourceDirectory));

        var accessor = File.ReadAllText(Path.Combine(second.ResourceDirectory, "L.cs"));
        Assert.All(keysAfterFirst, key => Assert.Contains(key, accessor));
    }

    /// <summary>
    /// A translation that loses a placeholder would crash the running app at format time, so it is
    /// refused. The key is then simply absent from the target bundle, which is how every one of
    /// these formats spells "not translated yet": the runtime falls back to the source language and
    /// the next run sees a gap to fill. Writing the English in instead would look done for ever.
    /// </summary>
    [Fact]
    public async Task DroppedPlaceholdersLeaveTheKeyUntranslated()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var model = new FakeModel { Translate = (_, text) => text.Replace("{0}", "the car") };

        var result = await new LocalizationRunner(model).RunAsync(Request(temp.Path));

        var russian = new ResxStore().Read(
            new ResourceLocation(result.ResourceDirectory, "AppResources", "ru", "en"));

        var interpolated = result.Localized.First(e => e.Candidate.Text.Contains("{0}"));

        Assert.False(russian.ContainsKey(interpolated.Key));
        Assert.NotEmpty(russian);   // the strings without placeholders still landed
    }

    [Fact]
    public async Task StringsTheModelIgnoresAreNeverLocalized()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var model = new FakeModel();
        model.DropIds.Add(0);

        var result = await new LocalizationRunner(model).RunAsync(Request(temp.Path));

        Assert.Equal(result.Candidates.Count, result.Localized.Count + result.Rejected.Count);
    }

    [Fact]
    public async Task ScanOnlyNeverCallsTheModel()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var model = new FakeModel();
        var request = new LocalizationRequest { ProjectPath = temp.Path, Languages = ["ru"], ScanOnly = true };

        var result = await new LocalizationRunner(model).RunAsync(request);

        Assert.Equal(0, model.Calls);
        Assert.NotEmpty(result.Candidates);
        Assert.Empty(result.Localized);
    }

    /// <summary>Adding a language later must not re-pay for classification.</summary>
    [Fact]
    public async Task VerdictsAreCachedAcrossLanguages()
    {
        using var temp = new TempFolder();
        CreateProject(temp);

        var model = new FakeModel();
        var runner = new LocalizationRunner(model);

        await runner.RunAsync(new LocalizationRequest { ProjectPath = temp.Path, Languages = ["ru"] });
        var afterFirst = model.Calls;

        await runner.RunAsync(new LocalizationRequest { ProjectPath = temp.Path, Languages = ["es"] });

        // One extra call: the Spanish translation batch. Classification came from the cache.
        Assert.Equal(afterFirst + 1, model.Calls);
    }

    private static List<string> KeysOf(string resourceDir) =>
        new ResxStore()
            .Read(new ResourceLocation(resourceDir, "AppResources", null))
            .Keys.OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
}
