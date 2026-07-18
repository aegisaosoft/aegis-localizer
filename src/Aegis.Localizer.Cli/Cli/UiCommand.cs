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

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Aegis.Localizer.Cli;

/// <summary>
/// `aegis-localizer ui` - the graphical front end.
///
/// It is the same web application that can be hosted as a service, started here in local mode and
/// opened in the browser. One UI to build and maintain, and running it locally keeps the user's
/// code and API key on their own machine.
/// </summary>
public static class UiCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var port = PortFrom(args) ?? FreePort();
        var url = $"http://127.0.0.1:{port}";
        var noBrowser = args.Contains("--no-browser");

        var host = LocateHost();
        if (host is null)
        {
            Console.Error.WriteLine(
                "The UI component was not found next to this executable. It ships with the full " +
                "package; a tool-only install can use the command line instead.");
            return 4;
        }

        Console.WriteLine($"aegis-localizer UI on {url}");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(host.Value.Command)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(host.Value.Path)!
            }
        };

        foreach (var argument in host.Value.Arguments) process.StartInfo.ArgumentList.Add(argument);

        process.StartInfo.Environment["ASPNETCORE_URLS"] = url;
        process.StartInfo.Environment["Localizer__LocalMode"] = "true";

        process.Start();

        if (!noBrowser)
        {
            // Give Kestrel a moment to bind before the browser races it to the port.
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            OpenBrowser(url);
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>The web host ships beside the CLI, either as an executable or as a framework dll.</summary>
    private static (string Command, string[] Arguments, string Path)? LocateHost()
    {
        var baseDir = AppContext.BaseDirectory;

        foreach (var folder in ProbeFolders(baseDir))
        {
            var executable = Path.Combine(folder, OperatingSystem.IsWindows()
                ? "Aegis.Localizer.Web.exe"
                : "Aegis.Localizer.Web");

            if (File.Exists(executable)) return (executable, [], executable);

            var dll = Path.Combine(folder, "Aegis.Localizer.Web.dll");
            if (File.Exists(dll)) return ("dotnet", [dll], dll);
        }

        return null;
    }

    private static IEnumerable<string> ProbeFolders(string baseDir)
    {
        // Where the published package puts it.
        yield return baseDir;
        yield return Path.Combine(baseDir, "ui");

        // Development convenience: running the CLI straight out of its own build output, where the
        // web host sits in a sibling project rather than next door. Without this, `ui` only works
        // from a published build, which is a poor way to find out your change broke it.
        var directory = new DirectoryInfo(baseDir);

        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var sibling = Path.Combine(directory.FullName, "Aegis.Localizer.Web", "bin");
            if (!Directory.Exists(sibling)) continue;

            foreach (var candidate in Directory
                         .EnumerateFiles(sibling, "Aegis.Localizer.Web.dll", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
                yield return Path.GetDirectoryName(candidate)!;
        }
    }

    private static int? PortFrom(string[] args)
    {
        var index = Array.IndexOf(args, "--port");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var port) ? port : null;
    }

    /// <summary>Binding to port 0 lets the OS pick one that is definitely free.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // A headless box has no browser to open; the URL is already on screen.
        }
    }
}
