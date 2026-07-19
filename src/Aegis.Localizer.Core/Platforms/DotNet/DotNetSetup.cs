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

using System.Text;
using System.Text.RegularExpressions;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.DotNet;

/// <summary>Shape of .NET app the culture has to be selected in; each one selects it differently.</summary>
internal enum DotNetAppKind
{
    /// <summary>Console, worker or class library: the thread's culture is the only knob.</summary>
    Plain,

    /// <summary>ASP.NET Core, Blazor Server or a minimal API: culture is per request, not per process.</summary>
    AspNetCore,

    /// <summary>MAUI: one UI thread, culture set once at startup.</summary>
    Maui
}

/// <summary>
/// Checking and building the localization support a .NET app needs.
///
/// Nothing here is blocking, and that is a real difference from the other stacks rather than
/// leniency: the SDK embeds .resx files without being asked, satellite assemblies are produced by
/// the build, and the generated `L` accessor is ordinary C# that compiles on its own. The rewritten
/// project builds and runs whatever state this returns.
///
/// What it does not do is show anyone a translation. ResourceManager resolves against
/// CultureInfo.CurrentUICulture, and in a fresh app nothing ever assigns it, so every lookup lands
/// on the neutral resources and the app is fluently, permanently English.
/// </summary>
internal static partial class DotNetSetup
{
    public static LocalizationSetup Inspect(LocalizationRequest request, string resourceDir)
    {
        var missing = new List<SetupStep>();
        var project = MainProject(request.ProjectPath);
        var kind = DetectKind(request.ProjectPath, project);

        if (!SelectsCulture(request.ProjectPath))
            missing.Add(new SetupStep(
                "Select a UI culture at startup",
                CultureSnippet(kind, request),
                SetupSeverity.Recommended,
                Automatic: false));

        if (project is not null && !HasNeutralResourcesLanguage(project))
            missing.Add(new SetupStep(
                $"Add <NeutralResourcesLanguage> to {Path.GetFileName(project)}",
                $"<PropertyGroup>\n  <NeutralResourcesLanguage>{request.SourceLanguage}" +
                "</NeutralResourcesLanguage>\n</PropertyGroup>\n" +
                "This declares which language the resources compiled into the main assembly are " +
                $"written in. Without it, every lookup made while the culture is {request.SourceLanguage} " +
                $"sends the runtime hunting for a satellite assembly for {request.SourceLanguage} that " +
                "will never exist before it falls back to the neutral resources - and that is the common " +
                "case, not an edge one.",
                SetupSeverity.Recommended,
                Automatic: true,
                File: Rel(request.ProjectPath, project)));

        return new LocalizationSetup(missing);
    }

    /// <summary>
    /// Only the project-file edit. Selecting a culture means adding statements inside somebody's
    /// Program.cs or App.xaml.cs in the right order relative to the code already there, which is
    /// restructuring their app rather than editing a manifest; it stays a written instruction.
    /// </summary>
    public static IReadOnlyList<SetupStep> Apply(LocalizationRequest request, string resourceDir, IRunLog log)
    {
        var done = new List<SetupStep>();
        var setup = Inspect(request, resourceDir);
        var project = MainProject(request.ProjectPath);

        var step = setup.Missing.FirstOrDefault(s => s.Automatic && s.File is not null);
        if (step is null || project is null) return done;

        if (AddNeutralResourcesLanguage(project, request.SourceLanguage, log))
        {
            done.Add(step);
            log.Info($"  updated {Rel(request.ProjectPath, project)}: " +
                     $"NeutralResourcesLanguage = {request.SourceLanguage}");
        }

        return done;
    }

    /// <summary>
    /// The instruction has to fit the app: telling a web developer to assign
    /// CultureInfo.CurrentUICulture at startup produces a server that answers every request in one
    /// language, which is a subtler bug than having no localization at all.
    /// </summary>
    private static string CultureSnippet(DotNetAppKind kind, LocalizationRequest request)
    {
        var cultures = new List<string> { request.SourceLanguage };

        foreach (var language in request.Languages)
            if (!cultures.Contains(language, StringComparer.OrdinalIgnoreCase))
                cultures.Add(language);

        var list = string.Join(", ", cultures.Select(c => $"\"{c}\""));

        return kind switch
        {
            DotNetAppKind.AspNetCore =>
                "In Program.cs:\n" +
                "  builder.Services.AddLocalization();\n" +
                "\n" +
                $"  var supportedCultures = new[] {{ {list} }};\n" +
                "  app.UseRequestLocalization(new RequestLocalizationOptions()\n" +
                $"      .SetDefaultCulture(\"{cultures[0]}\")\n" +
                "      .AddSupportedCultures(supportedCultures)\n" +
                "      .AddSupportedUICultures(supportedCultures));\n" +
                "\n" +
                "UseRequestLocalization has to run before the middleware that renders anything - put it " +
                "above UseRouting - and it sets the culture per request, from the Accept-Language " +
                "header or a culture cookie, so two users can read the same page in two languages.",

            DotNetAppKind.Maui =>
                "In MauiProgram.CreateMauiApp(), before the app is built:\n" +
                "  var culture = new CultureInfo(\"" + cultures[0] + "\");   // or the user's saved choice\n" +
                "  CultureInfo.DefaultThreadCurrentCulture = culture;\n" +
                "  CultureInfo.DefaultThreadCurrentUICulture = culture;\n" +
                "  CultureInfo.CurrentUICulture = culture;\n" +
                "\n" +
                "(using System.Globalization;)\n" +
                "MAUI does pick the device language up on its own, so this is what you need for an " +
                "in-app language picker, and what makes the choice survive into background threads. " +
                $"Supported cultures in this bundle: {list}.",

            _ =>
                "Once at startup, before anything reads a resource:\n" +
                $"  var culture = new CultureInfo(\"{cultures.ElementAtOrDefault(1) ?? cultures[0]}\");\n" +
                "  CultureInfo.CurrentUICulture = culture;\n" +
                "  CultureInfo.DefaultThreadCurrentUICulture = culture;   // covers threads started later\n" +
                "\n" +
                "(using System.Globalization;)\n" +
                "CurrentUICulture is what ResourceManager resolves against; CurrentCulture only affects " +
                $"number and date formatting. Supported cultures in this bundle: {list}."
        };
    }

    /// <summary>
    /// True when something in the tree already picks a culture. Any one of the three markers counts:
    /// a direct assignment, the DI registration, or the ASP.NET middleware.
    /// </summary>
    private static bool SelectsCulture(string projectRoot)
    {
        foreach (var file in DirectoryWalk.Files(projectRoot, "*.cs"))
        {
            // The tool's own generated accessor lives in the tree by the time this runs and says
            // nothing about how the app behaves.
            if (Path.GetFileName(file).Equals($"{DotNetAdapter.AccessorClassName}.cs", StringComparison.Ordinal))
                continue;

            var text = Read(file);
            if (text is null) continue;

            if (text.Contains("CurrentUICulture", StringComparison.Ordinal) ||
                text.Contains("AddLocalization", StringComparison.Ordinal) ||
                text.Contains("UseRequestLocalization", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static DotNetAppKind DetectKind(string projectRoot, string? project)
    {
        // The host builder is the definitive marker: it is what turns a project into a web app,
        // whatever the SDK attribute says.
        foreach (var file in DirectoryWalk.Files(projectRoot, "*.cs"))
        {
            var text = Read(file);
            if (text is null) continue;

            if (text.Contains("WebApplication.CreateBuilder", StringComparison.Ordinal))
                return DotNetAppKind.AspNetCore;
        }

        if (project is not null && Read(project) is { } csproj &&
            csproj.Contains("UseMaui", StringComparison.OrdinalIgnoreCase))
            return DotNetAppKind.Maui;

        return DotNetAppKind.Plain;
    }

    /// <summary>
    /// Shallowest .csproj in the tree, which is the app in a single-project repo and the host in a
    /// layered solution. Same rule <see cref="DotNetAdapter"/> uses to resolve the namespace, so
    /// both answers are about the same project.
    /// </summary>
    private static string? MainProject(string projectRoot) =>
        DirectoryWalk.Files(projectRoot, "*.csproj")
            .OrderBy(p => p.Count(c => c is '/' or '\\'))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool HasNeutralResourcesLanguage(string project) =>
        Read(project) is { } text && text.Contains("<NeutralResourcesLanguage", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds the property to the first PropertyGroup already in the file, or appends a group of its
    /// own when there is none. Additive either way: no existing element is moved or reformatted,
    /// because a .csproj carries conditions and ordering that mean something.
    /// </summary>
    private static bool AddNeutralResourcesLanguage(string project, string language, IRunLog log)
    {
        var text = Read(project);
        if (text is null) return false;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var element = $"<NeutralResourcesLanguage>{language}</NeutralResourcesLanguage>";

        var group = PropertyGroupOpen().Match(text);
        string patched;

        if (group.Success)
        {
            var end = group.Index + group.Length;
            var indent = IndentOf(text, group.Index) + "  ";
            patched = text[..end] + newline + indent + element + text[end..];
        }
        else
        {
            // No PropertyGroup at all: append one just inside the closing Project tag, which is the
            // only place a new group is unambiguously valid.
            var close = text.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                log.Warn($"{project} has no </Project> element; add NeutralResourcesLanguage by hand.");
                return false;
            }

            patched = text[..close] +
                      "  <PropertyGroup>" + newline +
                      "    " + element + newline +
                      "  </PropertyGroup>" + newline + newline +
                      text[close..];
        }

        try
        {
            File.WriteAllText(project, patched, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"{project} could not be written; add NeutralResourcesLanguage by hand.");
            return false;
        }
    }

    /// <summary>Leading whitespace of the line the offset falls on, so additions line up with it.</summary>
    private static string IndentOf(string text, int offset)
    {
        var start = text.LastIndexOf('\n', Math.Min(offset, text.Length - 1)) + 1;
        var end = start;

        while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;

        return text[start..end];
    }

    private static string Rel(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Deliberately only an unconditional group: a PropertyGroup carrying a Condition applies to one
    // configuration or target framework, and a neutral language that only holds for Debug|x64 is
    // worse than none at all. When the file has no plain group, one is appended instead.
    [GeneratedRegex(@"<PropertyGroup\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex PropertyGroupOpen();
}
