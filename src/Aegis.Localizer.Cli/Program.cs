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
using System.Text.Encodings.Web;
using System.Text.Json;
using Aegis.Localizer;
using Aegis.Localizer.Claude;
using Aegis.Localizer.Cli;
using Aegis.Localizer.Platforms;

Console.OutputEncoding = Encoding.UTF8;

try
{
    // `ui` is a verb rather than a flag: it starts a different process and takes its own options.
    if (args.Length > 0 && args[0] is "ui") return await UiCommand.RunAsync(args);

    var settings = CliParser.Parse(args);
    if (settings is null)
    {
        Console.WriteLine(CliParser.Usage);
        return 0;
    }

    return await RunAsync(settings);
}
catch (UsageException ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliParser.Usage);
    return 2;
}
catch (LocalizerException ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 2;
}
catch (ClaudeException ex)
{
    Console.Error.WriteLine("Model API error: " + ex.Message);
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex);
    return 1;
}

static async Task<int> RunAsync(CliSettings settings)
{
    var request = settings.Request;
    var log = new ConsoleRunLog(settings.Verbose);

    Console.WriteLine("aegis-localizer");
    Console.WriteLine($"  project   {request.ProjectPath}");
    Console.WriteLine($"  languages {(request.Languages.Count == 0 ? "-" : string.Join(", ", request.Languages))}");
    Console.WriteLine($"  mode      {Mode(request)}");
    Console.WriteLine();

    ClaudeClient? client = null;
    try
    {
        // --setup wants the model too: deciding what a project is missing means reading its build
        // files, which is the whole point of doing it with a model rather than a rule book. It is
        // not required though - without a key the run falls back to the built-in checks.
        if (!request.ScanOnly)
        {
            client = new ClaudeClient(new ClaudeOptions { ApiKey = ResolveApiKey(settings), Model = request.Model });
        }
        else if (request.Setup && TryResolveApiKey(settings) is { } key)
        {
            client = new ClaudeClient(new ClaudeOptions { ApiKey = key, Model = request.Model });
        }

        var result = await new LocalizationRunner(client, log).RunAsync(request);

        if (request.ScanOnly)
        {
            PrintCandidates(result);
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"Tokens: {result.InputTokens:N0} in / {result.OutputTokens:N0} out");
        if (result.ReportPath is not null) Console.WriteLine($"Report: {result.ReportPath}");

        if (!request.Apply && result.Localized.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run - no source file was changed. Review the report, then re-run with --apply.");
        }

        if (settings.Json) Console.WriteLine(Summary(result, request));

        return 0;
    }
    finally
    {
        client?.Dispose();
    }
}

static string Mode(LocalizationRequest request) => request.ScanOnly
    ? "scan only - no API call"
    : request.Apply
        ? "APPLY - sources will be rewritten"
        : "dry run - resources and report only";

static void PrintCandidates(LocalizationResult result)
{
    foreach (var group in result.Candidates
                 .GroupBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
                 .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(group.Key);
        foreach (var c in group.OrderBy(c => c.Line))
            Console.WriteLine($"  {c.Line,5}  {c.Kind,-15}  {Preview(c.Text)}");
        Console.WriteLine();
    }

    Console.WriteLine("--scan-only: nothing was sent to the API.");
}

static string Preview(string text)
{
    var one = text.Replace("\r", " ").Replace("\n", "\\n");
    return one.Length <= 90 ? one : one[..90] + "...";
}

/// <summary>Machine-readable summary for CI pipelines.</summary>
static string Summary(LocalizationResult result, LocalizationRequest request) =>
    JsonSerializer.Serialize(new
    {
        platform = result.Platform,
        format = result.Format.ToString(),
        filesScanned = result.FilesScanned,
        candidates = result.Candidates.Count,
        localized = result.Localized.Count,
        rejected = result.Rejected.Count,
        languages = request.Languages,
        bundles = result.Written.ToDictionary(kv => kv.Key, kv => kv.Value.Path),
        rewrite = result.Rewrite is null
            ? null
            : new { result.Rewrite.FilesChanged, result.Rewrite.Replacements, result.Rewrite.NotRewritable },
        tokens = new { input = result.InputTokens, output = result.OutputTokens },
        report = result.ReportPath
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

/// <summary>The key if one is configured, otherwise null. Used where a key is welcome but optional.</summary>
static string? TryResolveApiKey(CliSettings settings)
{
    try
    {
        return ResolveApiKey(settings);
    }
    catch (UsageException)
    {
        return null;
    }
}

/// <summary>--api-key, then ANTHROPIC_API_KEY, then appsettings next to the executable.</summary>
static string ResolveApiKey(CliSettings settings)
{
    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return settings.ApiKey!;

    var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

    foreach (var name in new[] { "appsettings.local.json", "appsettings.json" })
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path)) continue;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Claude", out var claude) &&
                claude.TryGetProperty("ApiKey", out var key) &&
                key.GetString() is { Length: > 0 } value)
                return value;
        }
        catch (JsonException)
        {
            // A malformed settings file just means we keep looking.
        }
    }

    throw new UsageException(
        "No Anthropic API key. Pass --api-key, set ANTHROPIC_API_KEY, or put Claude:ApiKey in appsettings.json. " +
        "Get a key at https://console.anthropic.com/settings/keys");
}
