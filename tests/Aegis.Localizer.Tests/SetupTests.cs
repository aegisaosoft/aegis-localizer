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

using Aegis.Localizer.Platforms;
using Xunit;

namespace Aegis.Localizer.Tests;

/// <summary>
/// Localizing an app that was never built for localization.
///
/// The shipped samples all happen to have i18n wired up, which hid this entirely: the tool would
/// translate a bare project's strings and rewrite its code into something that does not compile.
/// These build bare projects on purpose.
/// </summary>
public class SetupTests
{
    /// <summary>A Flutter app with no localization support whatsoever.</summary>
    private static string BareFlutterApp(TempFolder temp)
    {
        var root = Path.Combine(temp.Path, "bare_app");
        Directory.CreateDirectory(Path.Combine(root, "lib"));

        File.WriteAllText(Path.Combine(root, "pubspec.yaml"),
            """
            name: bare_app
            description: A Flutter app that has never been localized.
            environment:
              sdk: ">=3.0.0 <4.0.0"

            dependencies:
              flutter:
                sdk: flutter
              http: ^1.0.0

            dev_dependencies:
              flutter_test:
                sdk: flutter

            flutter:
              uses-material-design: true
            """);

        File.WriteAllText(Path.Combine(root, "lib", "main.dart"),
            """
            import 'package:flutter/material.dart';

            void main() => runApp(const BareApp());

            class BareApp extends StatelessWidget {
              const BareApp({super.key});

              @override
              Widget build(BuildContext context) {
                return MaterialApp(
                  home: Scaffold(
                    body: Column(children: [
                      Text('Save changes'),
                      Text('Your bookings'),
                    ]),
                  ),
                );
              }
            }
            """);

        return root;
    }

    private static LocalizationRequest Request(string path, bool apply = false, bool setup = false) => new()
    {
        ProjectPath = path,
        Languages = ["ru"],
        Apply = apply,
        Setup = setup,
        UseCache = false
    };

    [Fact]
    public async Task ABareProjectIsReportedAsMissingItsLocalizationSupport()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(root));

        Assert.False(result.Setup.IsReady);
        Assert.True(result.Setup.HasBlocking);

        var titles = result.Setup.Missing.Select(s => s.Title).ToList();
        Assert.Contains(titles, t => t.Contains("flutter_localizations"));
        Assert.Contains(titles, t => t.Contains("l10n.yaml"));
        Assert.Contains(titles, t => t.Contains("delegates"));
    }

    /// <summary>
    /// The point of the whole feature: rewriting a bare project would leave it not compiling, so the
    /// rewrite is refused. Translating and writing bundles still happens — that work is useful and
    /// harmless.
    /// </summary>
    [Fact]
    public async Task RewritingIsRefusedWhileSupportIsMissing()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);
        var before = await File.ReadAllTextAsync(Path.Combine(root, "lib", "main.dart"));

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, apply: true));

        Assert.True(result.RewriteBlocked);
        Assert.Null(result.Rewrite);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "lib", "main.dart")));

        // The translations were still produced and written.
        Assert.NotEmpty(result.Localized);
        Assert.NotEmpty(result.Written);
    }

    [Fact]
    public async Task SetupAddsWhatItCanAndSaysWhatIsLeft()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, setup: true));

        var pubspec = await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml"));

        Assert.Contains("flutter_localizations:", pubspec);
        Assert.Contains("sdk: flutter", pubspec);
        Assert.Contains("intl:", pubspec);
        Assert.Contains("generate: true", pubspec);
        Assert.True(File.Exists(Path.Combine(root, "l10n.yaml")));

        // The pubspec it did not write is still the user's: nothing else was disturbed.
        Assert.Contains("http: ^1.0.0", pubspec);
        Assert.Contains("uses-material-design: true", pubspec);

        // Registering the delegates is a change inside their own widget, so it stays manual.
        var remaining = result.Setup.Missing.Select(s => s.Title).ToList();
        Assert.Contains(remaining, t => t.Contains("delegates"));
        Assert.DoesNotContain(remaining, t => t.Contains("flutter_localizations"));
    }

    [Fact]
    public async Task SetupIsIdempotent()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, setup: true));
        var afterFirst = await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml"));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, setup: true));

        Assert.Equal(afterFirst, await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml")));
    }

    /// <summary>Once the manual step is done too, the rewrite goes ahead.</summary>
    [Fact]
    public async Task RewritingProceedsOnceSupportIsComplete()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, setup: true));

        // Stand in for the developer wiring up the delegates.
        var mainPath = Path.Combine(root, "lib", "main.dart");
        var main = await File.ReadAllTextAsync(mainPath);
        await File.WriteAllTextAsync(mainPath, main.Replace(
            "return MaterialApp(",
            "return MaterialApp(\n        localizationsDelegates: AppLocalizations.localizationsDelegates,"));

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, apply: true));

        Assert.False(result.RewriteBlocked);
        Assert.NotNull(result.Rewrite);
        Assert.True(result.Setup.IsReady);
    }

    /// <summary>An adapter must not claim a project is ready when it never looked.</summary>
    [Theory]
    [InlineData("dotnet", "DemoApp")]
    [InlineData("web", "WebApp")]
    [InlineData("android", "AndroidApp")]
    [InlineData("apple", "SwiftApp")]
    [InlineData("flutter", "FlutterApp")]
    public void EverySampleReportsASetupVerdictWithoutThrowing(string adapterName, string sample)
    {
        var adapter = AdapterRegistry.All.Single(a => a.Name == adapterName);
        var root = SamplePath(sample);

        var request = new LocalizationRequest { ProjectPath = root, Languages = ["ru"] };
        var setup = adapter.InspectSetup(request, adapter.DefaultResourceDirectory(root));

        Assert.NotNull(setup);
        Assert.All(setup.Missing, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Detail));
        });
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

        throw new DirectoryNotFoundException($"Could not locate the '{name}' sample.");
    }
}
