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
using Aegis.Localizer.Claude;
using Aegis.Localizer.Model;

namespace Aegis.Localizer.Ai;

/// <summary>
/// Pass one: decide which extracted strings are user-visible copy and name them. Language
/// independent on purpose, so its answers are reused for every target language.
/// </summary>
public sealed class StringClassifier(IStructuredModel claude, LocalizationRequest request, string platform, IRunLog log)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private sealed record ItemDto(int Id, string Text, string Kind, string File, string Member, string Context);

    private sealed class ToolResult
    {
        public List<StringVerdict> Items { get; set; } = [];
    }

    /// <summary>Returns a verdict per distinct source string.</summary>
    public async Task<IReadOnlyDictionary<string, StringVerdict>> ClassifyAsync(
        IReadOnlyList<StringCandidate> distinct, RunCache cache, CancellationToken ct)
    {
        var result = new Dictionary<string, StringVerdict>(StringComparer.Ordinal);
        var pending = new List<StringCandidate>();

        foreach (var candidate in distinct)
        {
            if (cache.TryGetVerdict(candidate.Text, out var cached)) result[candidate.Text] = cached;
            else pending.Add(candidate);
        }

        if (result.Count > 0) log.Info($"  {result.Count} verdicts reused from cache.");
        if (pending.Count == 0) return result;

        // Batches carry their index so answers map back without an O(n) lookup per string.
        var indexed = pending.Select((candidate, index) => (Index: index, Candidate: candidate)).ToList();
        var batches = Batching.Chunk(indexed, request.BatchSize);
        log.Info($"  classifying {pending.Count} strings in {batches.Count} request(s)");

        var verdicts = await Batching.RunAsync(
            batches,
            ClassifyBatchAsync,
            request.Concurrency,
            "classify",
            log,
            ct);

        foreach (var verdict in verdicts)
        {
            if (verdict.Id < 0 || verdict.Id >= pending.Count) continue;
            var text = pending[verdict.Id].Text;
            result[text] = verdict;
            cache.PutVerdict(text, verdict);
        }

        // A string the model skipped is treated as non-copy rather than guessed at.
        foreach (var candidate in pending.Where(c => !result.ContainsKey(c.Text)))
        {
            result[candidate.Text] = new StringVerdict
            {
                UserFacing = false,
                Reason = "No verdict returned by the model."
            };
        }

        return result;
    }

    private async Task<List<StringVerdict>> ClassifyBatchAsync(
        List<(int Index, StringCandidate Candidate)> batch, CancellationToken ct)
    {
        var items = batch.Select(b => new ItemDto(
            b.Index,
            b.Candidate.Text,
            b.Candidate.Kind.ToString(),
            $"{b.Candidate.RelativePath}:{b.Candidate.Line}",
            b.Candidate.Member ?? string.Empty,
            b.Candidate.Context)).ToList();

        // camelCase so the payload matches the field names in the response schema the model sees.
        var payload = JsonSerializer.Serialize(items, PayloadOptions);

        var result = await claude.ExtractAsync<ToolResult>(
            SystemPrompt(),
            "Classify these extracted strings. Return exactly one entry per id.\n\n" + payload,
            "report_verdicts",
            "Reports, for every input string, whether it is user-facing copy and what its resource key should be.",
            Schema,
            ct);

        // Ids are global indices, but a model that renumbers them per batch (0..n-1) is a real
        // failure mode. Unfiltered, batch 2's answers would land on batch 1's strings and the wrong
        // copy would be keyed, translated and rewritten into the source, silently.
        var expected = batch.Select(b => b.Index).ToHashSet();

        return result.Items.Where(item => expected.Contains(item.Id)).ToList();
    }

    private string SystemPrompt()
    {
        var context = string.IsNullOrWhiteSpace(request.ProjectContext)
            ? string.Empty
            : $"\n\nAbout this product: {request.ProjectContext}";

        return $"""
                You are a senior localization engineer auditing a {platform} codebase.

                You receive strings a static scanner pulled out of the source. For each one decide
                whether it is user-facing copy that belongs in a resource bundle, and give it a key.
                Do not translate anything in this pass.{context}

                Mark userFacing = true for: button and menu labels, page and window titles, form
                labels and placeholders, validation and error messages shown to a user, confirmation
                prompts, status text, tooltips, email and notification copy, empty-state text, and
                headings a user reads.

                Mark userFacing = false for: identifiers, dictionary and config keys, routes and
                URLs, HTTP header and claim names, culture and MIME names, SQL, regex, CSS classes
                and element ids, file paths and extensions, format patterns, enum and type names,
                connection strings, developer-only log or exception text, test fixtures, and any
                string that is compared against rather than displayed.

                When in doubt about a short ambiguous token with no surrounding UI context, prefer
                userFacing = false: a missed string is cheap to add later, a wrongly rewritten
                identifier breaks the build.

                Key rules: PascalCase, at most 40 characters, named after meaning and screen
                (SaveButton, InvalidEmailError, BookingConfirmedTitle). Never encode the language in
                the key. Give different copy different keys. Leave the key empty when userFacing is
                false, and say why in one short phrase.
                """;
    }

    private static readonly object Schema = new
    {
        type = "object",
        properties = new
        {
            items = new
            {
                type = "array",
                description = "One entry per input string.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "integer", description = "The id of the input string." },
                        userFacing = new { type = "boolean", description = "True when shown to an end user." },
                        reason = new { type = "string", description = "Short justification." },
                        key = new { type = "string", description = "PascalCase key, empty when not user-facing." }
                    },
                    required = new[] { "id", "userFacing", "reason", "key" }
                }
            }
        },
        required = new[] { "items" }
    };
}
