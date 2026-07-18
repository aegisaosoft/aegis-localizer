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
using Aegis.Localizer;
using Aegis.Localizer.Platforms;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Web.Runs;

// Content root is pinned to the application's own folder rather than the current directory. The CLI
// starts this host as a child process and a hosted deployment may be launched from anywhere; with
// the default, wwwroot is looked for beside whatever directory the caller happened to be in, and the
// interface comes up as a blank page instead of an error.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Local mode is the desktop experience: the UI may point at a folder on this machine. Hosted mode
// never allows that - it only ever works on an uploaded archive inside a sandbox.
var localMode = builder.Configuration.GetValue("Localizer:LocalMode", defaultValue: true);
var maxUploadBytes = builder.Configuration.GetValue("Localizer:MaxUploadMegabytes", 100) * 1024L * 1024L;

builder.Services.AddSingleton(new WorkspaceProvider(localMode));
builder.Services.AddSingleton<RunManager>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Only trusted in local mode: on a hosted server the environment belongs to the operator, and
// silently spending their key on a stranger's request would be a billing hole.
var environmentKey = localMode
    ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    : null;

app.MapGet("/api/meta", (WorkspaceProvider workspaces) => new
{
    localMode = workspaces.LocalMode,
    hasEnvironmentKey = !string.IsNullOrWhiteSpace(environmentKey),
    platforms = AdapterRegistry.All.Select(a => new
    {
        name = a.Name,
        displayName = a.DisplayName,
        extensions = a.Extensions,
        defaultFormat = a.DefaultFormat.ToString()
    }),
    formats = ResourceStoreRegistry.Supported.Select(f => f.ToString())
});

app.MapPost("/api/uploads", async (HttpRequest http, WorkspaceProvider workspaces, CancellationToken ct) =>
{
    if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected a multipart upload." });

    var form = await http.ReadFormAsync(ct);
    var file = form.Files.GetFile("archive");

    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No archive was uploaded." });
    if (file.Length > maxUploadBytes)
        return Results.BadRequest(new { error = $"Archive is larger than {maxUploadBytes / 1024 / 1024} MB." });

    try
    {
        await using var stream = file.OpenReadStream();
        var workspace = await workspaces.FromArchiveAsync(stream, ct);
        return Results.Ok(new { uploadId = workspaces.Remember(workspace), root = Path.GetFileName(workspace.Root) });
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).DisableAntiforgery();

app.MapPost("/api/runs", (StartRunRequest body, WorkspaceProvider workspaces, RunManager runs) =>
{
    var apiKey = string.IsNullOrWhiteSpace(body.ApiKey) ? environmentKey : body.ApiKey;

    if (string.IsNullOrWhiteSpace(apiKey) && !body.ScanOnly)
        return Results.BadRequest(new { error = "An Anthropic API key is required." });

    Workspace workspace;
    try
    {
        workspace = body.UploadId is { Length: > 0 }
            ? workspaces.Recall(body.UploadId) ?? throw new DirectoryNotFoundException("That upload has expired.")
            : workspaces.FromLocalPath(body.Path ?? string.Empty);
    }
    catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var request = new LocalizationRequest
    {
        ProjectPath = workspace.Root,
        Languages = body.Languages ?? [],
        SourceLanguage = string.IsNullOrWhiteSpace(body.SourceLanguage) ? "en" : body.SourceLanguage,
        Platform = string.IsNullOrWhiteSpace(body.Platform) ? "auto" : body.Platform,
        Format = ParseFormat(body.Format),
        ProjectContext = body.Context,
        DoNotTranslate = body.DoNotTranslate ?? [],
        Exclude = body.Exclude ?? [],
        IncludeDiagnostics = body.IncludeDiagnostics,
        ScanOnly = body.ScanOnly,
        Apply = body.Apply,
        Model = string.IsNullOrWhiteSpace(body.Model) ? "claude-sonnet-5" : body.Model
    };

    try
    {
        var session = runs.Start(request, workspace, apiKey ?? string.Empty);
        return Results.Ok(new { runId = session.Id });
    }
    catch (LocalizerException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/runs/{id}", (string id, RunManager runs) =>
{
    var session = runs.Get(id);
    if (session is null) return Results.NotFound();

    return Results.Ok(Describe(session));
});

// Server-sent events: the browser gets the log as it happens instead of polling.
app.MapGet("/api/runs/{id}/stream", async (string id, RunManager runs, HttpResponse response, CancellationToken ct) =>
{
    var session = runs.Get(id);
    if (session is null)
    {
        response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers["X-Accel-Buffering"] = "no";

    // Each client walks the log with its own cursor, so connecting late or reconnecting replays
    // everything from the start rather than showing a blank panel.
    var cursor = 0;

    while (!ct.IsCancellationRequested)
    {
        var (events, complete) = session.Log.Since(cursor);

        foreach (var e in events) await Send(response, e, ct);
        cursor += events.Count;

        // Drained and finished. A complete run with events still pending loops once more first.
        if (complete && events.Count == 0) break;

        if (!complete) await session.Log.WaitForChangeAsync().WaitAsync(ct);
    }

    await response.WriteAsync(
        $"event: done\ndata: {JsonSerializer.Serialize(Describe(session), Json.Web)}\n\n", ct);
    await response.Body.FlushAsync(ct);
});

app.MapGet("/api/runs/{id}/download", (string id, RunManager runs) =>
{
    var session = runs.Get(id);
    if (session?.Result is null) return Results.NotFound();

    var bytes = WorkspaceProvider.PackResults(session.Result, session.Request);
    return Results.File(bytes, "application/zip", "localization.zip");
});

app.Run();
return;

static async Task Send(HttpResponse response, RunEvent e, CancellationToken ct)
{
    await response.WriteAsync($"data: {JsonSerializer.Serialize(e, Json.Web)}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

static object Describe(RunSession session) => new
{
    id = session.Id,
    status = session.Status.ToString(),
    error = session.Error,
    result = session.Result is null
        ? null
        : new
        {
            platform = session.Result.Platform,
            format = session.Result.Format.ToString(),
            filesScanned = session.Result.FilesScanned,
            candidates = session.Result.Candidates.Count,
            rejected = session.Result.Rejected.Count,
            tokens = new { input = session.Result.InputTokens, output = session.Result.OutputTokens },
            elapsedSeconds = Math.Round(session.Result.Elapsed.TotalSeconds, 1),
            bundles = session.Result.Written.ToDictionary(kv => kv.Key, kv => Path.GetFileName(kv.Value.Path)),
            rewrite = session.Result.Rewrite is null
                ? null
                : new
                {
                    filesChanged = session.Result.Rewrite.FilesChanged,
                    replacements = session.Result.Rewrite.Replacements,
                    notRewritable = session.Result.Rewrite.NotRewritable
                },
            // The scan-only view needs the raw candidates; a full run needs keys and translations.
            candidateList = session.Result.ScanOnly
                ? session.Result.Candidates.Select(c => new
                {
                    file = c.RelativePath,
                    line = c.Line,
                    kind = c.Kind.ToString(),
                    text = c.Text
                }).ToList<object>()
                : [],
            strings = session.Result.Localized.Select(e => new
            {
                key = e.Key,
                source = e.Candidate.Text,
                file = e.Candidate.RelativePath,
                line = e.Candidate.Line,
                blocked = e.Candidate.RewriteBlockedReason,
                translations = e.Translations
            })
        }
};

static ResourceFormat? ParseFormat(string? name) =>
    string.IsNullOrWhiteSpace(name) || name == "auto"
        ? null
        : Enum.TryParse<ResourceFormat>(name, ignoreCase: true, out var format)
            ? format
            : null;

internal static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>What the browser posts to start a run. The API key is used and then forgotten.</summary>
internal sealed record StartRunRequest(
    string? Path,
    string? UploadId,
    List<string>? Languages,
    string? SourceLanguage,
    string? Platform,
    string? Format,
    string? Context,
    List<string>? DoNotTranslate,
    List<string>? Exclude,
    bool IncludeDiagnostics,
    bool ScanOnly,
    bool Apply,
    string? Model,
    string? ApiKey);
