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

using System.Collections.Concurrent;
using Aegis.Localizer.Claude;
using Aegis.Localizer.Platforms;

namespace Aegis.Localizer.Web.Runs;

public enum RunStatus
{
    Running,
    Completed,
    Failed
}

public sealed class RunSession
{
    public required string Id { get; init; }
    public required LocalizationRequest Request { get; init; }
    public required Workspace Workspace { get; init; }
    public required WebRunLog Log { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Running;
    public LocalizationResult? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The caller's API key, held only for the lifetime of this run and never written anywhere.
    /// Bring-your-own-key means exactly that: we do not store it, log it or return it.
    /// </summary>
    public required string ApiKey { private get; init; }

    internal string TakeKey() => ApiKey;
}

/// <summary>
/// Owns the in-flight runs. Deliberately in-memory: a run is bound to the workspace on this
/// machine, so surviving a restart would buy nothing.
/// </summary>
public sealed class RunManager
{
    private readonly ConcurrentDictionary<string, RunSession> _sessions = new(StringComparer.Ordinal);

    public RunSession Start(LocalizationRequest request, Workspace workspace, string apiKey)
    {
        var session = new RunSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Request = request,
            Workspace = workspace,
            Log = new WebRunLog(),
            ApiKey = apiKey
        };

        _sessions[session.Id] = session;

        // Fire and forget on purpose: the browser follows progress over the event stream, and the
        // HTTP request that started the run returns as soon as the id exists.
        _ = Task.Run(() => ExecuteAsync(session));

        return session;
    }

    public RunSession? Get(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    private static async Task ExecuteAsync(RunSession session)
    {
        ClaudeClient? client = null;

        try
        {
            if (!session.Request.ScanOnly)
                client = new ClaudeClient(new ClaudeOptions
                {
                    ApiKey = session.TakeKey(),
                    Model = session.Request.Model
                });

            session.Result = await new LocalizationRunner(client, session.Log).RunAsync(session.Request);
            session.Status = RunStatus.Completed;
            session.Log.Info("Done.");
        }
        catch (Exception ex)
        {
            session.Status = RunStatus.Failed;

            // Never surface the raw exception: it can carry the API key from a request dump.
            session.Error = ex is ClaudeException or LocalizerException or DirectoryNotFoundException
                ? ex.Message
                : "The run failed unexpectedly. Check the server log for details.";

            session.Log.Warn(session.Error);
        }
        finally
        {
            client?.Dispose();
            session.Log.Complete();
        }
    }
}
