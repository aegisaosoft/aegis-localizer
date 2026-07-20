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
using System.Globalization;
using Aegis.Localizer.Ai;
using Aegis.Localizer.Claude;
using Aegis.Localizer.Emit;
using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer;

/// <summary>
/// The whole pipeline, host-agnostic: scan, classify, translate, write resources, optionally
/// rewrite. The CLI, the desktop app and the web service all call this and differ only in how they
/// build the request and render <see cref="LocalizationResult"/>.
/// </summary>
public sealed class LocalizationRunner(IStructuredModel? model, IRunLog? log = null)
{
    private readonly IRunLog _log = log ?? NullRunLog.Instance;

    public async Task<LocalizationResult> RunAsync(LocalizationRequest request, CancellationToken ct = default)
    {
        Validate(request);

        var stopwatch = Stopwatch.StartNew();
        var adapter = AdapterRegistry.Resolve(request);
        var format = request.Format ?? adapter.DefaultFormat;
        var store = ResourceStoreRegistry.Get(format);
        var resourceDir = request.OutputDir ?? adapter.DefaultResourceDirectory(request.ProjectPath);

        var result = new LocalizationResult
        {
            Platform = adapter.DisplayName,
            Format = format,
            ResourceDirectory = resourceDir,
            ScanOnly = request.ScanOnly
        };

        // 1. Scan.
        _log.Info("Scanning...");
        var scan = new ProjectScanner(adapter, request, resourceDir).Scan();
        result.FilesScanned = scan.FilesScanned;
        result.FilesSkipped = scan.FilesSkipped;
        result.Candidates = scan.Candidates;

        var distinctCount = scan.Candidates.Select(c => c.Text).Distinct(StringComparer.Ordinal).Count();
        _log.Info($"  {scan.FilesScanned} files scanned, {scan.FilesSkipped} skipped, " +
                  $"{scan.Candidates.Count} candidates ({distinctCount} distinct).");

        // The source bundle from earlier runs. Read before anything else is decided, because a run
        // can be useful with zero candidates - filling a gap or adding a language to what is
        // already there - and that must not need the model or the source literals.
        var existing = store.Read(new ResourceLocation(resourceDir, request.ResourceName, null, request.SourceLanguage));

        // Inspected here, before any early exit, so even a free --scan-only tells the user their
        // project has no localization support. Finding that out after paying for a translation run
        // would be a poor way to learn it.
        result.Setup = adapter.InspectSetup(request, resourceDir);
        ReportSetup(result.Setup, request);

        if (request.ScanOnly || (scan.Candidates.Count == 0 && existing.Count == 0))
        {
            // --setup is honoured even here: "get my project ready for localization" is a reasonable
            // thing to want on its own, and it should not require paying for a translation run.
            await PrepareSetupAsync(adapter, request, resourceDir, result, ct);

            Finish(result, request, stopwatch);
            return result;
        }

        if (model is null)
            throw new LocalizerException("No model configured. Pass an API key, or run with ScanOnly.");

        var cache = RunCache.Load(request.WorkDir, request.UseCache, RunCache.Fingerprint(request));

        // 2. Classify once, language independent.
        var distinct = scan.Candidates
            .GroupBy(c => c.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var verdicts = distinct.Count == 0
            ? new Dictionary<string, StringVerdict>(StringComparer.Ordinal)
            : await new StringClassifier(model, request, adapter.DisplayName, _log).ClassifyAsync(distinct, cache, ct);

        // 3. Settle on final keys, reusing whatever the previous run already wrote.
        // Inverted to text -> key. Two keys sharing a value is ordinary in a real bundle (OkButton
        // and ConfirmButton both holding "OK"), so the first wins rather than throwing: a duplicate
        // must not abort a run the user has already paid the classification pass for.
        var byText = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in existing.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            byText.TryAdd(value, key);

        var namer = new KeyNamer(byText, adapter.NormalizeKey);

        var localized = new List<LocalizationEntry>();
        var rejected = new List<RejectedEntry>();

        foreach (var candidate in scan.Candidates)
        {
            if (!verdicts.TryGetValue(candidate.Text, out var verdict)) continue;

            if (!verdict.UserFacing)
            {
                rejected.Add(new RejectedEntry(candidate, verdict.Reason));
                continue;
            }

            localized.Add(new LocalizationEntry
            {
                Candidate = candidate,
                Key = namer.Resolve(candidate.Text, verdict.Key),
                Reason = verdict.Reason
            });
        }

        result.Localized = localized;
        result.Rejected = rejected;

        var distinctKeys = localized.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count();
        _log.Info($"  {localized.Count} occurrences of {distinctKeys} distinct strings to localize " +
                  $"({rejected.Count} rejected as non-UI).");

        // 4. Build the corpus: everything the source bundle already holds, plus whatever this scan
        //    found. The bundle is the authority, not the code - once a run has rewritten a literal
        //    into a lookup, the string only exists in the bundle, and a later run adding a language
        //    would otherwise translate the handful of strings that happened to survive in source.
        var corpus = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in localized.GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            var text = group.First().Candidate.Text;

            // The same key with different copy means someone edited the string. Every translation
            // of it is now stale and has to be redone.
            if (corpus.TryGetValue(group.Key, out var previous) && previous != text) changed.Add(group.Key);

            corpus[group.Key] = text;
        }

        if (corpus.Count == 0)
        {
            Finish(result, request, stopwatch);
            result.ReportPath = ReportWriter.Write(result, request);
            return result;
        }

        var contexts = localized
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Candidate.Context, StringComparer.Ordinal);

        var comments = localized
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => $"{g.First().Candidate.RelativePath}:{g.First().Candidate.Line}",
                StringComparer.Ordinal);

        _log.Info("Writing resources...");
        var neutral = store.Write(
            new ResourceLocation(resourceDir, request.ResourceName, null, request.SourceLanguage), corpus, comments);
        result.Written[request.SourceLanguage] = neutral;
        _log.Info($"  {neutral.Path}  +{neutral.Added} new, {neutral.Updated} updated");

        // 5. Per language, translate only what is missing or stale.
        var translator = new Translator(model, request, adapter.DisplayName, _log);
        var state = TranslationState.Load(request.WorkDir);

        foreach (var language in request.Languages)
        {
            var target = new ResourceLocation(resourceDir, request.ResourceName, language, request.SourceLanguage);
            var already = store.Read(target);

            var outcome = new LanguageOutcome { Language = language, Total = corpus.Count };
            var units = new List<TranslationUnit>();
            var reused = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, text) in corpus)
            {
                // A wording somebody corrected by hand outranks anything the model would produce,
                // including under --retranslate. Losing a translator's work to a flag would teach
                // people never to touch the bundles, which is the opposite of what we want.
                if (already.TryGetValue(key, out var current) &&
                    state.IsHumanEdited(language, key, current))
                {
                    state.RecordApproved(language, text, current);
                    outcome.HumanEdited++;
                    outcome.AlreadyTranslated++;
                    continue;
                }

                var reason = Needed(key, text, language, already, changed, state, request.Retranslate);

                if (reason is null)
                {
                    outcome.AlreadyTranslated++;
                    continue;
                }

                // The same copy a person has already settled on elsewhere: reuse their wording
                // rather than paying to have it invented again, slightly differently.
                if (state.ApprovedFor(language, text) is { } approved)
                {
                    reused[key] = approved;
                    outcome.ReusedApproved++;
                    continue;
                }

                outcome.Sent[reason.Value] = outcome.Sent.GetValueOrDefault(reason.Value) + 1;
                units.Add(new TranslationUnit(key, text, contexts.GetValueOrDefault(key, string.Empty)));
            }

            result.Languages.Add(outcome);

            if (units.Count == 0 && reused.Count == 0)
            {
                _log.Info($"  {language}: already complete, {outcome.Total} strings.");
                state.Save();
                continue;
            }

            _log.Info($"  {language}: {Describe(outcome)}");

            // The project's own corrections are shown to the model, so new copy comes back in the
            // wording this project has already settled on rather than a fresh invention each time.
            var examples = state.ApprovedExamples(language, 20);
            if (examples.Count > 0)
                _log.Detail($"  {language}: following {examples.Count} wording(s) corrected in this project");

            var translated = units.Count > 0
                ? new Dictionary<string, string>(
                    await translator.TranslateAsync(units, language, cache, examples, ct), StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            cache.Save();

            foreach (var (key, value) in reused) translated[key] = value;

            // Only what came back is written. A string the model refused stays absent from the
            // bundle rather than being filled with its own English, so the next run retries it
            // instead of believing it is done.
            if (translated.Count > 0)
            {
                var written = store.Write(target, translated, comments);
                result.Written[language] = written;
                _log.Info($"  {written.Path}  +{written.Added} new, {written.Updated} updated");

                foreach (var (key, value) in translated) state.RecordWritten(language, key, corpus[key], value);
            }

            state.Save();

            if (translated.Count < units.Count + reused.Count)
                _log.Warn($"{language}: {units.Count + reused.Count - translated.Count} string(s) still untranslated; run again to retry");

            foreach (var entry in localized)
                if (translated.TryGetValue(entry.Key, out var text)) entry.Translations[language] = text;
                else if (already.TryGetValue(entry.Key, out var old)) entry.Translations[language] = old;
        }

        result.InputTokens = model.InputTokens;
        result.OutputTokens = model.OutputTokens;

        // 5. Runtime glue, generated from the merged bundle so earlier keys never disappear.
        var allKeys = store.Read(new ResourceLocation(resourceDir, request.ResourceName, null, request.SourceLanguage)).Keys.ToList();
        adapter.EmitRuntime(allKeys, request, resourceDir, _log);

        // 6. Close the gaps we can, now that the bundles the config will point at actually exist.
        await PrepareSetupAsync(adapter, request, resourceDir, result, ct);
        var setup = result.Setup;

        // 7. Rewrite, only when asked, and never into a project that will not build afterwards.
        if (request.Apply)
        {
            if (setup.HasBlocking)
            {
                _log.Warn("Sources were NOT rewritten: the project is missing localization support " +
                          "listed above, and the rewritten code would not build. " +
                          (setup.BlockingIsAutomatic
                              ? "Re-run with --setup to add it."
                              : "Add it by hand, then run again."));

                result.RewriteBlocked = true;
            }
            else
            {
                _log.Info("Rewriting sources...");
                result.Rewrite = SourceRewriter.Apply(localized, adapter, request, _log);
                _log.Info($"  {result.Rewrite.Replacements} replacements in {result.Rewrite.FilesChanged} files " +
                          $"({result.Rewrite.NotRewritable} left in place).");
            }
        }

        Finish(result, request, stopwatch);
        result.ReportPath = ReportWriter.Write(result, request);
        return result;
    }

    /// <summary>
    /// Works out what the project still needs, and closes the gaps when asked.
    ///
    /// When a model is available it reads the project's own build files and decides; the built-in
    /// per-stack checks are the offline fallback, so a free --scan-only still says something useful
    /// without an API key. Deciding is the model's half, verifying and writing is ours.
    /// </summary>
    private async Task PrepareSetupAsync(
        ISourceAdapter adapter, LocalizationRequest request, string resourceDir, LocalizationResult result,
        CancellationToken ct)
    {
        if (!(request.Apply || request.Setup)) return;

        // No key, no model: fall back to the built-in per-stack fixes so `--scan-only --setup` still
        // does something useful for free. They only know the shapes they were written for, which is
        // exactly why the model is preferred when one is available.
        if (model is null)
        {
            if (!request.Setup || result.Setup.IsReady) return;

            _log.Info("Setting up localization (built-in checks; no model configured)...");

            foreach (var done in adapter.ApplySetup(request, resourceDir, _log))
                result.SetupApplied.Add(done);

            result.Setup = adapter.InspectSetup(request, resourceDir);
            ReportSetup(result.Setup, request);
            return;
        }

        // Planning costs a request, so it is spent only when it changes what happens next: a rewrite
        // that could break the app, or an explicit request to set the project up.

        var context = adapter.DescribeSetupContext(request, resourceDir);
        if (context.Files.Count == 0) return;

        SetupPlan plan;
        try
        {
            plan = await new SetupPlanner(model, request, _log).PlanAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad: the built-in checks have already run and the translations are the
            // point of the run. Losing the richer answer is a downgrade, not a reason to throw away
            // work the user has paid for.
            _log.Warn($"Could not inspect the project's build files ({ex.Message}); " +
                      "falling back to the built-in checks.");
            return;
        }

        if (request.Setup && plan.Steps.Any(s => s.IsAutomatic))
        {
            _log.Info("Setting up localization...");

            var outcome = SetupApplier.Apply(plan, context, request, _log);

            foreach (var step in outcome.Applied)
                result.SetupApplied.Add(new SetupStep(step.Title, step.Detail, step.Severity, Automatic: true));

            // Applied steps are no longer outstanding; everything else still is.
            plan.Steps = plan.Steps.Except(outcome.Applied).ToList();
        }

        result.Setup = new LocalizationSetup(plan.Steps
            .Select(s => new SetupStep(s.Title, s.Detail, s.Severity, s.IsAutomatic))
            .ToList());

        ReportSetup(result.Setup, request);
    }

    private void ReportSetup(LocalizationSetup setup, LocalizationRequest request)
    {
        if (setup.IsReady) return;

        _log.Info(string.Empty);
        _log.Info("This project is missing localization support:");

        foreach (var step in setup.Missing)
        {
            var mark = step.Severity == SetupSeverity.Blocking ? "!" : "-";
            _log.Info($"  {mark} {step.Title}");

            // Details are multi-line on purpose - they carry the snippet to paste - so every line
            // is indented, not just the first.
            foreach (var line in step.Detail.Replace("\r\n", "\n").Split('\n'))
                _log.Info("      " + line);

            _log.Info(string.Empty);
        }

        if (!request.Setup && setup.Missing.Any(s => s.Automatic))
            _log.Info("  Run again with --setup to add the parts that can be added automatically.");

        _log.Info(string.Empty);
    }

    /// <summary>Why this key has to go to the model for this language, or null when it does not.</summary>
    private static TranslationReason? Needed(
        string key,
        string sourceText,
        string language,
        IReadOnlyDictionary<string, string> already,
        IReadOnlySet<string> changed,
        TranslationState state,
        bool forced)
    {
        if (forced) return TranslationReason.Forced;
        if (!already.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)) return TranslationReason.Missing;

        // Two ways the copy can move under a translation: this scan saw new text for a key the
        // bundle already had, or somebody edited the source bundle by hand between runs. The second
        // is the normal path once a project has been rewritten and the code no longer holds copy.
        if (changed.Contains(key) || state.IsStale(language, key, sourceText)) return TranslationReason.SourceChanged;

        return null;
    }

    private static string Describe(LanguageOutcome outcome)
    {
        var parts = new List<string>();

        if (outcome.Sent.TryGetValue(TranslationReason.Missing, out var missing)) parts.Add($"{missing} new");
        if (outcome.Sent.TryGetValue(TranslationReason.SourceChanged, out var stale)) parts.Add($"{stale} changed");
        if (outcome.Sent.TryGetValue(TranslationReason.Forced, out var forced)) parts.Add($"{forced} forced");
        if (outcome.AlreadyTranslated > 0) parts.Add($"{outcome.AlreadyTranslated} already done");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Closes out a run on every exit path. An --apply run always reports a rewrite summary, even
    /// an all-zero one: a caller reading the JSON output must be able to tell "nothing needed
    /// rewriting" from "the rewrite stage never ran".
    /// </summary>
    private static void Finish(LocalizationResult result, LocalizationRequest request, Stopwatch stopwatch)
    {
        // An all-zero summary means "the stage ran and found nothing to change". A refused rewrite
        // is a different thing entirely and must stay distinguishable: null plus RewriteBlocked.
        if (request.Apply && !request.ScanOnly && !result.RewriteBlocked)
            result.Rewrite ??= new RewriteSummary(0, 0, 0, [], Path.Combine(request.WorkDir, "backup"));

        result.Elapsed = stopwatch.Elapsed;
    }

    private static void Validate(LocalizationRequest request)
    {
        if (!Directory.Exists(request.ProjectPath))
            throw new LocalizerException($"Project folder not found: {request.ProjectPath}");

        if (request.Languages.Count == 0 && !request.ScanOnly)
            throw new LocalizerException("At least one target language is required.");

        foreach (var language in request.Languages)
        {
            try
            {
                _ = CultureInfo.GetCultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                throw new LocalizerException($"'{language}' is not a known culture name.");
            }
        }

        if (request.Format is { } format && !ResourceStoreRegistry.IsSupported(format))
            throw new LocalizerException($"Resource format {format} is not supported yet.");
    }
}
