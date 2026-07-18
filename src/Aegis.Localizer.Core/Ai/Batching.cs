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

namespace Aegis.Localizer.Ai;

/// <summary>Splitting work into batches and running them with a concurrency cap.</summary>
public static class Batching
{
    public static List<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        var batches = new List<List<T>>();
        for (var i = 0; i < source.Count; i += size)
            batches.Add(source.Skip(i).Take(size).ToList());
        return batches;
    }

    /// <summary>
    /// Runs every batch with at most <paramref name="concurrency"/> in flight. A batch that throws
    /// is reported and dropped rather than failing the whole run, so one bad response cannot lose
    /// the work already done.
    ///
    /// If EVERY batch fails the run stops instead. Otherwise a broken key, a network outage or a
    /// schema mismatch would surface as "the model rejected all your strings" - a plausible-looking
    /// empty result that sends the user hunting through their code for a problem that is ours.
    /// </summary>
    public static async Task<List<TResult>> RunAsync<TBatch, TResult>(
        IReadOnlyList<TBatch> batches,
        Func<TBatch, CancellationToken, Task<List<TResult>>> work,
        int concurrency,
        string stage,
        IRunLog log,
        CancellationToken ct)
    {
        if (batches.Count == 0) return [];

        var gate = new SemaphoreSlim(concurrency);
        var results = new List<TResult>[batches.Count];
        var done = 0;
        var failures = 0;
        Exception? firstFailure = null;

        await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = await work(batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                Interlocked.CompareExchange(ref firstFailure, ex, null);
                log.Warn($"{stage}: batch {index + 1} failed ({ex.Message})");
                results[index] = [];
            }
            finally
            {
                gate.Release();
                log.Progress(stage, Interlocked.Increment(ref done), batches.Count);
            }
        }));

        if (failures == batches.Count)
            throw new LocalizerException(
                $"Every {stage} request failed. First error: {firstFailure?.Message ?? "unknown"}");

        return results.Where(r => r is not null).SelectMany(r => r).ToList();
    }
}
