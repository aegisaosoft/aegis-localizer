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

        var model = new FakeModel
        {
            PlanSetup = () =>
            [
                new
                {
                    title = "Add flutter_localizations",
                    detail = "AppLocalizations does not exist without it, so the rewrite would not compile.",
                    severity = "Blocking",
                    edits = Array.Empty<object>()
                }
            ]
        };

        var result = await new LocalizationRunner(model).RunAsync(Request(root, apply: true));

        Assert.True(result.RewriteBlocked);
        Assert.Null(result.Rewrite);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "lib", "main.dart")));

        // The translations were still produced and written.
        Assert.NotEmpty(result.Localized);
        Assert.NotEmpty(result.Written);
    }

    /// <summary>
    /// A model that proposes edits gets them applied — after the tool has checked each one. This is
    /// the half that matters offline: the model decides, but the verification is ours.
    /// </summary>
    [Fact]
    public async Task ProposedEditsAreApplied()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var model = new FakeModel
        {
            PlanSetup = () =>
            [
                new
                {
                    title = "Add flutter_localizations",
                    detail = "The generated AppLocalizations needs it.",
                    severity = "Blocking",
                    edits = new object[]
                    {
                        new
                        {
                            file = "pubspec.yaml",
                            kind = "InsertAfter",
                            anchor = "  flutter:\n    sdk: flutter",
                            content = "\n  flutter_localizations:\n    sdk: flutter",
                            reason = "adds the dependency"
                        }
                    }
                }
            ]
        };

        var result = await new LocalizationRunner(model).RunAsync(Request(root, setup: true));
        var pubspec = await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml"));

        Assert.Contains("flutter_localizations:", pubspec);

        // Everything the edit did not name is untouched.
        Assert.Contains("http: ^1.0.0", pubspec);
        Assert.Contains("uses-material-design: true", pubspec);

        Assert.Single(result.SetupApplied);
        Assert.True(File.Exists(Path.Combine(root, ".aegis-localizer", "backup", "pubspec.yaml")));
    }

    /// <summary>
    /// An anchor that matches more than once names no particular place. Guessing which one was meant
    /// is how a build file quietly acquires a change nobody designed.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousAnchorIsRefused()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);
        var before = await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml"));

        var model = new FakeModel
        {
            PlanSetup = () =>
            [
                new
                {
                    title = "Ambiguous edit",
                    detail = "sdk: flutter appears twice in the file.",
                    severity = "Blocking",
                    edits = new object[]
                    {
                        new
                        {
                            file = "pubspec.yaml",
                            kind = "InsertAfter",
                            anchor = "    sdk: flutter",
                            content = "\n  intl: any",
                            reason = "adds intl"
                        }
                    }
                }
            ]
        };

        var result = await new LocalizationRunner(model).RunAsync(Request(root, setup: true));

        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml")));
        Assert.Empty(result.SetupApplied);
    }

    /// <summary>
    /// The model may only touch files the adapter offered, and only inside the project. Entry points
    /// ARE offered - a web app needs its bootstrap imported there - but an arbitrary source file is
    /// not, and neither is anything outside the tree.
    /// </summary>
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("lib/widgets/booking_card.dart")]
    public async Task EditsToFilesThatWereNotOfferedAreRefused(string file)
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);
        var before = await File.ReadAllTextAsync(Path.Combine(root, "lib", "main.dart"));

        var model = new FakeModel
        {
            PlanSetup = () =>
            [
                new
                {
                    title = "Out of bounds",
                    detail = "Should be refused.",
                    severity = "Blocking",
                    edits = new object[]
                    {
                        new { file, kind = "CreateFile", anchor = "", content = "// injected", reason = "no" }
                    }
                }
            ]
        };

        var result = await new LocalizationRunner(model).RunAsync(Request(root, setup: true));

        Assert.Empty(result.SetupApplied);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "lib", "main.dart")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(root, "lib", "widgets", "booking_card.dart")));
    }

    /// <summary>A step with no edits is guidance, not automation, and must leave the tree alone.</summary>
    [Fact]
    public async Task AManualStepChangesNothingButIsStillReported()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var model = new FakeModel
        {
            PlanSetup = () =>
            [
                new
                {
                    title = "Register the localization delegates",
                    detail = "Add localizationsDelegates to your MaterialApp.",
                    severity = "Blocking",
                    edits = Array.Empty<object>()
                }
            ]
        };

        var result = await new LocalizationRunner(model).RunAsync(Request(root, apply: true));

        Assert.True(result.RewriteBlocked);
        Assert.Empty(result.SetupApplied);
        Assert.Contains(result.Setup.Missing, s => s.Title.Contains("delegates"));
    }

    /// <summary>When the model reports nothing outstanding, the rewrite goes ahead.</summary>
    [Fact]
    public async Task RewritingProceedsWhenNothingIsOutstanding()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(root, apply: true));

        Assert.False(result.RewriteBlocked);
        Assert.NotNull(result.Rewrite);
        Assert.True(result.Setup.IsReady);
    }

    /// <summary>Without a model the built-in per-stack fixes still run, so --setup works offline.</summary>
    [Fact]
    public async Task SetupFallsBackToTheBuiltInChecksWithoutAModel()
    {
        using var temp = new TempFolder();
        var root = BareFlutterApp(temp);

        var request = new LocalizationRequest
        {
            ProjectPath = root, Languages = ["ru"], ScanOnly = true, Setup = true, UseCache = false
        };

        var result = await new LocalizationRunner(model: null).RunAsync(request);

        Assert.True(File.Exists(Path.Combine(root, "l10n.yaml")));
        Assert.Contains("flutter_localizations:", await File.ReadAllTextAsync(Path.Combine(root, "pubspec.yaml")));
        Assert.NotEmpty(result.SetupApplied);
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
