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

using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms;
using Xunit;

namespace Aegis.Localizer.Tests;

/// <summary>
/// The contract every stack adapter must satisfy, driven off the sample project that ships with it.
///
/// These run against all adapters at once rather than one file per stack: a new adapter is added to
/// <see cref="Samples"/> and inherits the whole suite, and a rule that only holds for .NET stops
/// being invisible. The span check in particular is load-bearing - the rewriter refuses to touch a
/// span whose text does not match, so an adapter with off-by-one offsets silently rewrites nothing
/// and still reports success.
/// </summary>
public class AdapterContractTests
{
    public static TheoryData<string, string> Samples => new()
    {
        { "dotnet", "DemoApp" },
        { "web", "WebApp" },
        { "android", "AndroidApp" },
        { "apple", "SwiftApp" },
        { "flutter", "FlutterApp" }
    };

    [Fact]
    public void EverySampleIsCoveredByTheSuite()
    {
        var covered = Samples.Select(row => (string)row[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = AdapterRegistry.All.Select(a => a.Name);

        Assert.All(registered, name =>
            Assert.True(covered.Contains(name), $"Adapter '{name}' has no sample in AdapterContractTests.Samples."));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void DetectsItsOwnSample(string adapterName, string sample)
    {
        var request = Request(SamplePath(sample));

        Assert.Equal(adapterName, AdapterRegistry.Resolve(request).Name);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void FindsCopyInItsOwnSample(string adapterName, string sample)
    {
        Assert.NotEmpty(Extract(adapterName, SamplePath(sample)));
    }

    /// <summary>
    /// Every candidate must quote its source byte for byte. This is what the rewriter verifies
    /// before editing, so a mismatch here is the difference between "rewrote nothing, reported
    /// success" and a working adapter.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void SpansQuoteTheSourceExactly(string adapterName, string sample)
    {
        foreach (var (candidate, content) in ExtractWithSource(adapterName, SamplePath(sample)))
        {
            Assert.True(candidate.SpanStart >= 0, $"{Where(candidate)}: negative span start");
            Assert.True(candidate.SpanLength > 0, $"{Where(candidate)}: empty span");
            Assert.True(
                candidate.SpanStart + candidate.SpanLength <= content.Length,
                $"{Where(candidate)}: span runs past the end of the file");

            Assert.Equal(
                content.Substring(candidate.SpanStart, candidate.SpanLength),
                candidate.RawSpanText);
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void CandidatesCarryUsableMetadata(string adapterName, string sample)
    {
        foreach (var candidate in Extract(adapterName, SamplePath(sample)))
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Text), $"{Where(candidate)}: empty text");
            Assert.True(candidate.Line > 0, $"{Where(candidate)}: line number not set");
            Assert.False(string.IsNullOrWhiteSpace(candidate.Context), $"{Where(candidate)}: no context for the model");

            // The pre-filter runs inside every extractor; anything it would drop must not survive.
            Assert.False(NoiseFilter.IsNoise(candidate.Text), $"{Where(candidate)}: noise reached the model: {candidate.Text}");
        }
    }

    /// <summary>Line numbers are what the report points at; an off-by-one sends people to the wrong line.</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void LineNumbersMatchTheSpan(string adapterName, string sample)
    {
        foreach (var (candidate, content) in ExtractWithSource(adapterName, SamplePath(sample)))
        {
            var expected = content[..candidate.SpanStart].Count(c => c == '\n') + 1;
            Assert.Equal(expected, candidate.Line);
        }
    }

    /// <summary>
    /// The whole pipeline per stack: translate, write bundles, rewrite sources, then do it again.
    /// The second run must change nothing, which is what makes the tool safe to put in a loop or a
    /// pipeline. Runs offline against <see cref="FakeModel"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public async Task ApplyingTwiceChangesNothingTheSecondTime(string adapterName, string sample)
    {
        using var temp = new TempFolder();
        var project = Path.Combine(temp.Path, sample);
        CopyDirectory(SamplePath(sample), project);

        var request = Request(project, apply: true);

        var first = await new LocalizationRunner(new FakeModel()).RunAsync(request);
        Assert.NotNull(first.Rewrite);
        Assert.Equal(adapterName, AdapterRegistry.Resolve(request).Name);

        var afterFirst = Snapshot(project);

        var second = await new LocalizationRunner(new FakeModel()).RunAsync(request);

        Assert.Equal(0, second.Rewrite!.Replacements);

        // Compared file by file: a whole-dictionary assert only says "something differs", which is
        // useless when the tree has dozens of files.
        var afterSecond = Snapshot(project);

        Assert.Equal(afterFirst.Keys.OrderBy(k => k), afterSecond.Keys.OrderBy(k => k));

        foreach (var (file, before) in afterFirst)
            Assert.True(before == afterSecond[file], $"{file} changed on the second run.\n\nfirst:\n{before}\n\nsecond:\n{afterSecond[file]}");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task WritesBundlesInTheStacksOwnFormat(string adapterName, string sample)
    {
        using var temp = new TempFolder();
        var project = Path.Combine(temp.Path, sample);
        CopyDirectory(SamplePath(sample), project);

        var adapter = AdapterRegistry.All.Single(a => a.Name == adapterName);
        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(project));

        Assert.Equal(adapter.DefaultFormat, result.Format);
        Assert.NotEmpty(result.Written);
        Assert.All(result.Written.Values, w => Assert.True(File.Exists(w.Path), $"missing bundle: {w.Path}"));
    }

    // -- helpers ----------------------------------------------------------------------------

    private static LocalizationRequest Request(string path, bool apply = false) => new()
    {
        ProjectPath = path,
        Languages = ["ru"],
        Apply = apply,
        UseCache = false
    };

    private static List<StringCandidate> Extract(string adapterName, string root) =>
        ExtractWithSource(adapterName, root).Select(x => x.Candidate).ToList();

    private static List<(StringCandidate Candidate, string Content)> ExtractWithSource(string adapterName, string root)
    {
        var adapter = AdapterRegistry.All.Single(a => a.Name == adapterName);
        var request = Request(root);
        var found = new List<(StringCandidate, string)>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!adapter.Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

            var content = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);

            foreach (var candidate in adapter.Extract(file, relative, content, request))
                found.Add((candidate, content));
        }

        return found;
    }

    private static string Where(StringCandidate candidate) => $"{candidate.RelativePath}:{candidate.Line}";

    /// <summary>Content of every file in the tree, so a run can be compared against a previous one.</summary>
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".aegis-localizer", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                f => Path.GetRelativePath(root, f),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(from, to));

        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from, "*.*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    private static string SamplePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var samples = Path.Combine(directory.FullName, "samples", name);
            if (Directory.Exists(samples)) return samples;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the '{name}' sample from {AppContext.BaseDirectory}.");
    }
}
