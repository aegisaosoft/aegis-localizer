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

using System.Text.RegularExpressions;
using Aegis.Localizer.Emit;
using Aegis.Localizer.Model;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.DotNet;

/// <summary>
/// The .NET stack: C# code, Razor views and XAML pages, which together cover ASP.NET Core, Blazor,
/// MAUI, WPF and Avalonia.
/// </summary>
public sealed class DotNetAdapter : ISourceAdapter
{
    /// <summary>Name of the generated static class the rewritten code calls into.</summary>
    public const string AccessorClassName = "L";

    public string Name => "dotnet";

    public string DisplayName => ".NET (C#, Razor, XAML / MAUI)";

    public IReadOnlyCollection<string> Extensions { get; } = [".cs", ".cshtml", ".razor", ".xaml", ".axaml"];

    public ResourceFormat DefaultFormat => ResourceFormat.Resx;

    public int DetectionScore(string projectRoot)
    {
        if (DirectoryWalk.Files(projectRoot, "*.csproj").Any()) return 100;
        if (Directory.EnumerateFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly).Any()) return 95;
        return DirectoryWalk.Files(projectRoot, "*.cs").Any() ? 60 : 0;
    }

    public string DefaultResourceDirectory(string projectRoot) => Path.Combine(projectRoot, "Localization");

    public IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request)
    {
        var candidates = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => CSharpExtractor.Extract(filePath, relativePath, content),
            ".cshtml" or ".razor" => MarkupExtractor.ExtractRazor(filePath, relativePath, content),
            ".xaml" or ".axaml" => MarkupExtractor.ExtractXaml(filePath, relativePath, content),
            _ => []
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Kind == CandidateKind.Diagnostic && !request.IncludeDiagnostics) continue;
            yield return candidate;
        }
    }

    public RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request)
    {
        var ns = ResolveNamespace(request);

        return candidate.Kind switch
        {
            // Attribute arguments must be compile-time constants.
            CandidateKind.Attribute => null,

            // XAML would need an xmlns plus a markup extension per file; too invasive to patch blindly.
            CandidateKind.XamlAttribute or CandidateKind.XamlText => null,

            // Razor resolves the accessor through an @using directive, not a C# using line.
            CandidateKind.RazorText or CandidateKind.RazorAttribute =>
                new RewritePlan($"@{AccessorClassName}.{key}", $"@using {ns}"),

            CandidateKind.CSharpLiteral or CandidateKind.Diagnostic => candidate.IsInterpolated
                ? candidate.InterpolationArgs is { Count: > 0 }
                    ? new RewritePlan(
                        $"{AccessorClassName}.Format(\"{key}\", {string.Join(", ", candidate.InterpolationArgs)})",
                        $"using {ns};")
                    : null
                : new RewritePlan($"{AccessorClassName}.{key}", $"using {ns};"),

            _ => null
        };
    }

    public LocalizationSetup InspectSetup(LocalizationRequest request, string resourceDir) =>
        DotNetSetup.Inspect(request, resourceDir);

    public IReadOnlyList<SetupStep> ApplySetup(LocalizationRequest request, string resourceDir, IRunLog log) =>
        DotNetSetup.Apply(request, resourceDir, log);

    public void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log)
    {
        var ns = ResolveNamespace(request);
        var path = Path.Combine(resourceDir, $"{AccessorClassName}.cs");

        AccessorWriter.Write(path, ns, AccessorClassName, ResourceBaseName(request, resourceDir, ns), keys);
        log.Info($"  {path}  {keys.Count} accessor properties");
    }

    /// <summary>Explicit namespace wins; otherwise RootNamespace from the nearest .csproj.</summary>
    private static string ResolveNamespace(LocalizationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Namespace)) return request.Namespace!;

        var csproj = DirectoryWalk.Files(request.ProjectPath, "*.csproj")
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();

        if (csproj is null) return Sanitize(new DirectoryInfo(request.ProjectPath).Name);

        try
        {
            var match = Regex.Match(
                File.ReadAllText(csproj), @"<RootNamespace>\s*([^<]+?)\s*</RootNamespace>", RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim() : Sanitize(Path.GetFileNameWithoutExtension(csproj));
        }
        catch (IOException)
        {
            return Sanitize(Path.GetFileNameWithoutExtension(csproj));
        }
    }

    /// <summary>
    /// ResourceManager base name: root namespace plus the resource folder's path relative to the
    /// project, which is how the SDK names embedded .resx resources.
    /// </summary>
    private static string ResourceBaseName(LocalizationRequest request, string resourceDir, string ns)
    {
        var relative = Path.GetRelativePath(request.ProjectPath, resourceDir);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return $"{ns}.{request.ResourceName}";

        var folder = relative
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.')
            .Trim('.');

        return string.IsNullOrEmpty(folder) || folder == "."
            ? $"{ns}.{request.ResourceName}"
            : $"{ns}.{folder}.{request.ResourceName}";
    }

    private static string Sanitize(string s)
    {
        var cleaned = new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
        if (cleaned.Length == 0) return "App";
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }
}
