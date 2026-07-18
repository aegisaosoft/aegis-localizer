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

namespace Aegis.Localizer.Web.Runs;

public sealed record RunEvent(string Kind, string Message, int Completed = 0, int Total = 0);

/// <summary>
/// The browser's view of <see cref="IRunLog"/>.
///
/// Events are kept in a list and clients read it through their own cursor, rather than consuming a
/// queue: a queue hands each event to whichever reader grabs it first, so two open browser tabs
/// would each see half the log. A cursor also makes reconnecting free - the client asks for
/// everything after the last index it saw, and a tab opened late still gets the whole run.
/// </summary>
public sealed class WebRunLog : IRunLog
{
    private readonly List<RunEvent> _history = [];
    private readonly Lock _gate = new();

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _complete;

    public void Info(string message) => Emit(new RunEvent("log", message));

    public void Detail(string message) => Emit(new RunEvent("detail", message));

    public void Warn(string message) => Emit(new RunEvent("warn", message));

    public void Progress(string stage, int completed, int total) =>
        Emit(new RunEvent("progress", stage, completed, total));

    /// <summary>Events after <paramref name="cursor"/>, and whether the run has finished emitting.</summary>
    public (IReadOnlyList<RunEvent> Events, bool Complete) Since(int cursor)
    {
        lock (_gate)
        {
            var slice = cursor >= _history.Count
                ? []
                : _history.GetRange(cursor, _history.Count - cursor);

            return (slice, _complete);
        }
    }

    /// <summary>Completes as soon as anything is emitted, or the run ends.</summary>
    public Task WaitForChangeAsync()
    {
        lock (_gate) return _complete ? Task.CompletedTask : _changed.Task;
    }

    public void Complete()
    {
        TaskCompletionSource signal;

        lock (_gate)
        {
            if (_complete) return;
            _complete = true;
            signal = _changed;
        }

        signal.TrySetResult();
    }

    private void Emit(RunEvent e)
    {
        TaskCompletionSource signal;

        lock (_gate)
        {
            _history.Add(e);
            signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Released outside the lock: a waiter resuming inline would otherwise re-enter it.
        signal.TrySetResult();
    }
}
