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
using Aegis.Localizer.Claude;

namespace Aegis.Localizer.Tests;

/// <summary>
/// A deterministic stand-in for Claude. It reads the same batch payload the real prompt carries and
/// answers by rule, which lets the whole pipeline be tested offline, in CI, with no API key and no
/// cost. Hooks let a test force the failure modes that matter.
/// </summary>
public sealed class FakeModel : IStructuredModel
{
    /// <summary>
    /// Deliberately identical to ClaudeClient's, enum converter included. A fake that deserializes
    /// more leniently than the real client hides exactly the bugs it exists to catch.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
    };

    /// <summary>Decides the verdict for a source string. Default: anything with a capital letter is copy.</summary>
    public Func<string, bool> IsUserFacing { get; set; } = text => text.Any(char.IsUpper);

    /// <summary>Produces the translation. Default prefixes the culture, which preserves placeholders.</summary>
    public Func<string, string, string> Translate { get; set; } = (language, text) => $"[{language}] {text}";

    /// <summary>Ids the model should silently omit, to exercise the missing-answer path.</summary>
    public HashSet<int> DropIds { get; } = [];

    /// <summary>
    /// What the setup planner should answer. Empty means "the project is ready", which keeps the
    /// tests that are not about setup free of it.
    /// </summary>
    public Func<IEnumerable<object>> PlanSetup { get; set; } = () => [];

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public int Calls { get; private set; }

    /// <summary>System prompts seen, so a test can assert which pass did or did not run.</summary>
    public List<string> SystemPrompts { get; } = [];

    public Task<T> ExtractAsync<T>(
        string system, string userText, string toolName, string toolDescription, object inputSchema, CancellationToken ct)
    {
        Calls++;
        SystemPrompts.Add(system);
        InputTokens += 100;
        OutputTokens += 50;

        // Only the batch tools carry a numbered item list; the setup planner is handed the project's
        // build files instead, and trying to read that as a batch just throws.
        var items = toolName is "report_verdicts" or "report_translations" ? ParseBatch(userText) : [];

        object payload = toolName switch
        {
            "report_verdicts" => new
            {
                items = items
                    .Where(i => !DropIds.Contains(i.Id))
                    .Select(i => new
                    {
                        id = i.Id,
                        userFacing = IsUserFacing(i.Text),
                        reason = "fake",
                        key = IsUserFacing(i.Text) ? KeyFor(i.Text) : string.Empty
                    })
            },
            "report_translations" => new
            {
                items = items
                    .Where(i => !DropIds.Contains(i.Id))
                    .Select(i => new
                    {
                        id = i.Id,
                        translation = Translate(CurrentLanguage(system), i.Text)
                    })
            },
            // Setup planning: no steps by default, so tests that are not about setup are unaffected.
            "report_setup" => new { steps = PlanSetup() },

            _ => throw new InvalidOperationException($"Unexpected tool '{toolName}'.")
        };

        var json = JsonSerializer.Serialize(payload, Options);
        return Task.FromResult(JsonSerializer.Deserialize<T>(json, Options)!);
    }

    private sealed record Item(int Id, string Text);

    /// <summary>The batch payload is appended to the user message as JSON; parse it back out.</summary>
    private static List<Item> ParseBatch(string userText)
    {
        var start = userText.IndexOf('[');
        if (start < 0) return [];

        using var doc = JsonDocument.Parse(userText[start..]);

        return doc.RootElement.EnumerateArray()
            .Select(e => new Item(e.GetProperty("id").GetInt32(), e.GetProperty("text").GetString() ?? string.Empty))
            .ToList();
    }

    /// <summary>The translation prompt names the target culture; pull it back out for the fake answer.</summary>
    private static string CurrentLanguage(string system)
    {
        var marker = system.LastIndexOf(" into ", StringComparison.Ordinal);
        if (marker < 0) return "xx";

        var open = system.IndexOf('(', marker);
        var close = system.IndexOf(')', open + 1);
        return open > 0 && close > open ? system[(open + 1)..close] : "xx";
    }

    private static string KeyFor(string text)
    {
        var words = text
            .Split([' ', '.', ',', '!', '?', ':', '{', '}'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.All(char.IsLetterOrDigit))
            .Take(3)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());

        var key = string.Concat(words);
        return key.Length == 0 ? "Text" : key;
    }
}
