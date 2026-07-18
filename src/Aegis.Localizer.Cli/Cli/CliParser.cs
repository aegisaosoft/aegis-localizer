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

using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Localizer.Resources;

namespace Aegis.Localizer.Cli;

/// <summary>Thrown for bad or missing arguments; the entry point prints usage for it.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Everything the CLI knows that the core does not: which config file to read, how loud to be,
/// and where the API key comes from.
/// </summary>
public sealed class CliSettings
{
    public LocalizationRequest Request { get; set; } = null!;
    public string? ApiKey { get; set; }
    public bool Verbose { get; set; }
    public bool Json { get; set; }
}

/// <summary>
/// The on-disk config file. Lets a team commit its languages, glossary and exclusions once instead
/// of retyping flags, which is what makes the tool usable in CI.
/// </summary>
public sealed class ConfigFile
{
    public string? Path { get; set; }
    public List<string>? Languages { get; set; }
    public string? SourceLanguage { get; set; }
    public string? Platform { get; set; }
    public string? Out { get; set; }
    public string? Format { get; set; }
    public string? ResourceName { get; set; }
    public string? Namespace { get; set; }
    public string? Model { get; set; }
    public string? Context { get; set; }
    public List<string>? DoNotTranslate { get; set; }
    public List<string>? Exclude { get; set; }
    public bool? IncludeDiagnostics { get; set; }
    public int? BatchSize { get; set; }
    public int? Concurrency { get; set; }

    [JsonIgnore] public const string DefaultName = "aegis-localizer.json";
}

public static class CliParser
{
    /// <summary>Parses argv over an optional config file. Returns null when the user asked for help.</summary>
    public static CliSettings? Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help")) return null;

        // The config file is resolved first so flags can override it.
        var configPath = ValueOf(args, "--config");
        var projectPath = ValueOf(args, "--path") ?? Directory.GetCurrentDirectory();
        var config = LoadConfig(configPath, projectPath);

        string? outDir = null;
        string? ns = null;
        string? resourceName = null;
        string? model = null;
        string? platform = null;
        string? sourceLanguage = null;
        string? formatName = null;
        string? context = null;
        string? apiKey = null;
        var languages = new List<string>();
        var exclude = new List<string>();
        var doNotTranslate = new List<string>();
        var apply = false;
        var includeDiagnostics = false;
        var scanOnly = false;
        var noCache = false;
        var retranslate = false;
        var verbose = false;
        var json = false;
        var batchSize = 0;
        var concurrency = 0;
        var maxFiles = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--path": projectPath = Next(args, ref i); break;
                case "--config": _ = Next(args, ref i); break;   // already consumed above
                case "--lang":
                case "--langs": languages.AddRange(Split(Next(args, ref i))); break;
                case "--source-lang": sourceLanguage = Next(args, ref i); break;
                case "--platform": platform = Next(args, ref i); break;
                case "--out": outDir = Next(args, ref i); break;
                case "--format": formatName = Next(args, ref i); break;
                case "--resource-name": resourceName = Next(args, ref i); break;
                case "--namespace": ns = Next(args, ref i); break;
                case "--model": model = Next(args, ref i); break;
                case "--api-key": apiKey = Next(args, ref i); break;
                case "--context": context = Next(args, ref i); break;
                case "--keep": doNotTranslate.AddRange(Split(Next(args, ref i))); break;
                case "--exclude": exclude.AddRange(Split(Next(args, ref i))); break;
                case "--apply": apply = true; break;
                case "--include-diagnostics": includeDiagnostics = true; break;
                case "--scan-only": scanOnly = true; break;
                case "--no-cache": noCache = true; break;
                case "--retranslate": retranslate = true; break;
                case "--verbose": verbose = true; break;
                case "--json": json = true; break;
                case "--batch-size": batchSize = Int(Next(args, ref i), a); break;
                case "--concurrency": concurrency = Int(Next(args, ref i), a); break;
                case "--max-files": maxFiles = Int(Next(args, ref i), a); break;
                default: throw new UsageException($"Unknown argument: {a}");
            }
        }

        projectPath = System.IO.Path.GetFullPath(projectPath.Trim('"'));
        if (!Directory.Exists(projectPath))
            throw new UsageException($"Project folder not found: {projectPath}");

        if (languages.Count == 0 && config?.Languages is { Count: > 0 }) languages.AddRange(config.Languages);
        if (languages.Count == 0 && !scanOnly)
            throw new UsageException("At least one target language is required: --lang ru or --lang ru,es,de.");

        if (config?.Exclude is { } configExclude) exclude.AddRange(configExclude);
        if (config?.DoNotTranslate is { } configKeep) doNotTranslate.AddRange(configKeep);

        var request = new LocalizationRequest
        {
            ProjectPath = projectPath,
            Languages = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SourceLanguage = sourceLanguage ?? config?.SourceLanguage ?? "en",
            Platform = platform ?? config?.Platform ?? "auto",
            OutputDir = Resolve(outDir ?? config?.Out, projectPath),
            Format = ParseFormat(formatName ?? config?.Format),
            ResourceName = resourceName ?? config?.ResourceName ?? "AppResources",
            Namespace = ns ?? config?.Namespace,
            Model = model ?? config?.Model ?? "claude-sonnet-5",
            ProjectContext = context ?? config?.Context,
            DoNotTranslate = doNotTranslate.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Exclude = exclude.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Apply = apply,
            IncludeDiagnostics = includeDiagnostics || config?.IncludeDiagnostics == true,
            ScanOnly = scanOnly,
            UseCache = !noCache,
            Retranslate = retranslate,
            BatchSize = Positive(batchSize, config?.BatchSize, 25, "--batch-size", 100),
            Concurrency = Positive(concurrency, config?.Concurrency, 4, "--concurrency", 16),
            MaxFiles = maxFiles
        };

        return new CliSettings { Request = request, ApiKey = apiKey, Verbose = verbose, Json = json };
    }

    private static ConfigFile? LoadConfig(string? explicitPath, string projectPath)
    {
        var path = explicitPath ?? System.IO.Path.Combine(projectPath, ConfigFile.DefaultName);
        if (!File.Exists(path))
        {
            if (explicitPath is not null) throw new UsageException($"Config file not found: {explicitPath}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ConfigFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new UsageException($"{path} is not valid JSON: {ex.Message}");
        }
    }

    private static ResourceFormat? ParseFormat(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return name.Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "resx" or "dotnet" => ResourceFormat.Resx,
            "json" or "i18next" or "i18nextjson" => ResourceFormat.I18NextJson,
            "androidxml" or "android" or "strings" => ResourceFormat.AndroidXml,
            "applestrings" or "apple" or "ios" => ResourceFormat.AppleStrings,
            "arb" or "flutter" or "flutterarb" => ResourceFormat.FlutterArb,
            "po" or "gettext" or "gettextpo" => ResourceFormat.GettextPo,
            _ => throw new UsageException(
                $"Unknown --format '{name}'. Known: resx, json, android, apple, arb, po.")
        };
    }

    private static string? Resolve(string? dir, string projectPath) =>
        string.IsNullOrWhiteSpace(dir)
            ? null
            : System.IO.Path.IsPathRooted(dir)
                ? System.IO.Path.GetFullPath(dir)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectPath, dir));

    private static int Positive(int flag, int? config, int fallback, string name, int max)
    {
        var value = flag > 0 ? flag : config ?? fallback;
        if (value < 1 || value > max) throw new UsageException($"{name} must be 1..{max}.");
        return value;
    }

    private static string[] Split(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ValueOf(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new UsageException($"{args[i]} needs a value.");
        return args[++i];
    }

    private static int Int(string value, string flag) =>
        int.TryParse(value, out var n) ? n : throw new UsageException($"{flag} needs a number, got '{value}'.");

    public static string Usage =>
        """
        aegis-localizer - finds hardcoded UI strings with Claude and localizes them.

        Usage:
          aegis-localizer --path <project-dir> --lang <cultures> [options]
          aegis-localizer ui [--port <n>] [--no-browser]      Open the graphical interface.

        Required:
          --path <dir>            Project root. Defaults to the current directory.
          --lang <a,b,c>          Target cultures: ru, or ru,es,pt-BR. Repeatable.

        Common:
          --scan-only             List what the scanner found and exit. No API call, no cost.
          --apply                 Rewrite the sources. Without it every run is a dry run.
          --context "<text>"      Tell the model what the product is, for better wording.
          --keep <a,b>            Terms never to translate (brand names, product names).
          --config <file>         Defaults to aegis-localizer.json in the project root.

        Targeting:
          --platform <name>       auto (default), or an adapter name such as dotnet.
          --format <name>         resx | json | android | apple | arb | po. Adapter default if unset.
          --out <dir>             Resource folder. Adapter convention if unset.
          --resource-name <n>     Bundle base name. Default AppResources.
          --namespace <ns>        Namespace/module for generated glue. Inferred if unset.
          --source-lang <c>       Language the source strings are written in. Default en.

        Model and cost:
          --api-key <key>         Overrides ANTHROPIC_API_KEY and appsettings.
          --model <id>            Default claude-sonnet-5.
          --no-cache              Ignore .aegis-localizer/cache.json.
          --retranslate           Redo translations that already exist, not just the missing ones.
          --batch-size <n>        Strings per request. Default 25.
          --concurrency <n>       Parallel requests. Default 4.

        Scope and output:
          --exclude <a;b>         Extra path fragments to skip.
          --max-files <n>         Stop after N files. 0 means no limit.
          --include-diagnostics   Treat log and exception text as localizable too.
          --json                  Emit the run summary as JSON, for CI.
          --verbose               Per-stage detail.
          -h, --help              This text.
        """;
}
