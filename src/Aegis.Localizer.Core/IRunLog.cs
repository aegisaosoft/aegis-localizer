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

namespace Aegis.Localizer;

/// <summary>
/// Where a run reports what it is doing. The console implements it by printing, a GUI by updating
/// a progress panel, a web service by pushing events to the browser.
/// </summary>
public interface IRunLog
{
    /// <summary>A step the user cares about.</summary>
    void Info(string message);

    /// <summary>Extra detail, shown only in verbose modes.</summary>
    void Detail(string message);

    /// <summary>Something went wrong but the run continues.</summary>
    void Warn(string message);

    /// <summary>Progress within a long stage.</summary>
    void Progress(string stage, int completed, int total);
}

/// <summary>Discards everything. Useful for tests and for callers that only want the result.</summary>
public sealed class NullRunLog : IRunLog
{
    public static readonly NullRunLog Instance = new();

    public void Info(string message) { }
    public void Detail(string message) { }
    public void Warn(string message) { }
    public void Progress(string stage, int completed, int total) { }
}

/// <summary>Prints to stdout. Used by the CLI.</summary>
public sealed class ConsoleRunLog(bool verbose = false) : IRunLog
{
    public void Info(string message) => Console.WriteLine(message);

    public void Detail(string message)
    {
        if (verbose) Console.WriteLine(message);
    }

    public void Warn(string message) => Console.WriteLine("  ! " + message);

    public void Progress(string stage, int completed, int total) =>
        Console.WriteLine($"  {stage} {completed}/{total}");
}
