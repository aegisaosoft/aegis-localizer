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

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Localizer.Claude;
using Aegis.Localizer.Model;

namespace Aegis.Localizer.Ai;

/// <summary>
/// Pass two: translate the strings pass one kept. Runs once per target language and is cached per
/// language, so adding a language later never re-pays for classification.
/// </summary>
public sealed partial class Translator(
    IStructuredModel claude, LocalizationRequest request, string platform, IRunLog log)
{
    /// <summary>camelCase so the payload matches the field names in the response schema.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private sealed record ItemDto(int Id, string Key, string Text, string Context);

    private sealed class ToolResult
    {
        public List<TranslatedString> Items { get; set; } = [];
    }

    /// <summary>
    /// Translates the given units into <paramref name="language"/>. Returns key to translated text
    /// for the units that produced a usable answer; a unit whose translation was refused is absent
    /// rather than mapped to its own source, so the caller can leave the bundle untouched and try
    /// again next run.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationUnit> units, string language, RunCache cache, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (units.Count == 0) return result;

        // One request per distinct source string; the same copy under two keys is translated once.
        var byText = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<TranslationUnit>();

        foreach (var group in units.GroupBy(u => u.SourceText, StringComparer.Ordinal))
        {
            if (cache.TryGetTranslation(language, group.Key, out var cached)) byText[group.Key] = cached;
            else pending.Add(group.First());
        }

        if (byText.Count > 0)
            log.Info($"  {language}: {byText.Count} translations reused from cache.");

        if (pending.Count > 0)
        {
            var indexed = pending.Select((unit, index) => (Index: index, Unit: unit)).ToList();
            var batches = Batching.Chunk(indexed, request.BatchSize);
            log.Info($"  {language}: translating {pending.Count} strings in {batches.Count} request(s)");

            var produced = await Batching.RunAsync(
                batches,
                (batch, token) => TranslateBatchAsync(batch, language, token),
                request.Concurrency,
                $"translate:{language}",
                log,
                ct);

            foreach (var item in produced)
            {
                if (item.Id < 0 || item.Id >= pending.Count) continue;

                var source = pending[item.Id].SourceText;
                var text = item.Translation;

                if (string.IsNullOrEmpty(text)) continue;

                if (!PlaceholdersMatch(source, text))
                {
                    log.Warn($"{language}: placeholders differ for \"{Shorten(source)}\", left untranslated for now");
                    continue;
                }

                byText[source] = text;
                cache.PutTranslation(language, source, text);
            }
        }

        foreach (var unit in units)
            if (byText.TryGetValue(unit.SourceText, out var translated))
                result[unit.Key] = translated;

        return result;
    }

    private async Task<List<TranslatedString>> TranslateBatchAsync(
        List<(int Index, TranslationUnit Unit)> batch, string language, CancellationToken ct)
    {
        var items = batch.Select(b => new ItemDto(
            b.Index,
            b.Unit.Key,
            b.Unit.SourceText,
            b.Unit.Context)).ToList();

        var result = await claude.ExtractAsync<ToolResult>(
            SystemPrompt(language),
            "Translate these strings. Return exactly one entry per id.\n\n" +
            JsonSerializer.Serialize(items, PayloadOptions),
            "report_translations",
            "Returns the translated text for every input string.",
            Schema,
            ct);

        // See StringClassifier: an id outside this batch means the model renumbered, and applying
        // it would attach a translation to a different string.
        var expected = batch.Select(b => b.Index).ToHashSet();

        return result.Items.Where(item => expected.Contains(item.Id)).ToList();
    }

    private string SystemPrompt(string language)
    {
        var target = Describe(language);
        var source = Describe(request.SourceLanguage);

        var glossary = request.DoNotTranslate.Count == 0
            ? string.Empty
            : $"\n\nNever translate these terms, keep them exactly as written: " +
              string.Join(", ", request.DoNotTranslate) + ".";

        var context = string.IsNullOrWhiteSpace(request.ProjectContext)
            ? string.Empty
            : $"\n\nAbout this product: {request.ProjectContext}";

        // Triple-brace interpolation: the prompt itself must show {0}, {name} and {{count}} verbatim.
        return $$$"""
                You are a professional software localizer translating the user interface of a
                {{{platform}}} application from {{{source}}} into {{{target}}}.

                These strings appear in a shipping product, so translate them the way a native
                speaker would expect to read them in an app of this kind, not word for word. Match
                the register a UI uses in {{{target}}}: follow the platform's own conventions for
                formality, imperative verbs on buttons, and sentence or title casing.{{{context}}}{{{glossary}}}

                Hard requirements:
                - Preserve every placeholder exactly as written and in the same number: {0}, {1},
                  %s, %d, %1$s, {name}, {{count}}, :name. You may move them if {{{target}}} grammar
                  requires a different order, but never rename, drop or add one.
                - Preserve HTML and markup tags, markdown, escape sequences, leading and trailing
                  whitespace, and trailing punctuation.
                - Keep product, brand and company names untranslated.
                - Keep the string a single line if the source is a single line.
                - Never add explanations, quotes or notes to the translated text itself. If you had
                  to make a judgement call, put it in the separate note field.

                If a string genuinely should not be translated, return it unchanged.
                """;
    }

    private static string Describe(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return $"{culture.EnglishName} ({culture.Name})";
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    /// <summary>
    /// Guards the one failure mode that silently breaks a running app: a translation that lost or
    /// invented a placeholder. Mismatches fall back to the source string instead of shipping.
    /// </summary>
    private static bool PlaceholdersMatch(string source, string translation)
    {
        var a = Placeholder().Matches(source).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
        var b = Placeholder().Matches(translation).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "...";

    /// <summary>Covers .NET, printf, ICU, i18next and Rails placeholder styles in one sweep.</summary>
    [GeneratedRegex(@"\{\{[^}]+\}\}|\{[^}]*\}|%\d+\$[sd@]|%[sdf@]|:[a-zA-Z_]\w*")]
    private static partial Regex Placeholder();

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
                        translation = new { type = "string", description = "The translated text." },
                        note = new { type = "string", description = "Optional remark about a judgement call." }
                    },
                    required = new[] { "id", "translation" }
                }
            }
        },
        required = new[] { "items" }
    };
}
