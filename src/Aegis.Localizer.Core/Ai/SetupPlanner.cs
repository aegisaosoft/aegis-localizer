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
/// Works out what a project is missing before its localized strings will do anything, by reading
/// the project's own build files.
///
/// This replaces a pile of hand-written rules, one per stack, that could only recognise the shapes
/// their author thought of. Real build files do not cooperate: a Gradle script can strip the very
/// locale folders we just wrote, a manifest can live three directories down, an entry point can be
/// called anything. Reading them is the part a model is actually good at; verifying and applying
/// what it proposes stays with the tool.
/// </summary>
public sealed class SetupPlanner(IStructuredModel model, LocalizationRequest request, IRunLog log)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Largest file we will show the model; build files are small, and a lockfile is noise.</summary>
    private const int MaxFileChars = 20_000;

    public async Task<SetupPlan> PlanAsync(SetupContext context, CancellationToken ct)
    {
        var files = context.Files
            .Select(f => new
            {
                path = f.RelativePath,
                exists = f.Exists,
                content = f.Content.Length <= MaxFileChars
                    ? f.Content
                    : f.Content[..MaxFileChars] + "\n... (truncated)"
            })
            .ToList();

        if (files.Count == 0) return new SetupPlan();

        var payload = JsonSerializer.Serialize(new
        {
            stack = context.Stack,
            rewriteContract = context.RewriteContract,
            resourceLayout = context.ResourceLayout,
            sourceLanguage = request.SourceLanguage,
            targetLanguages = request.Languages,
            files
        }, PayloadOptions);

        log.Detail($"  inspecting {files.Count} project file(s) for localization support");

        var plan = await model.ExtractAsync<SetupPlan>(
            SystemPrompt(),
            "Here is the project. Report what it still needs.\n\n" + payload,
            "report_setup",
            "Reports what the project is missing before localized strings will work, with the edits that would provide it.",
            Schema,
            ct);

        // A step with no title is unusable in a report and cannot be reviewed; drop it rather than
        // show the user a blank bullet.
        plan.Steps = plan.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Title)).ToList();

        foreach (var step in plan.Steps)
            step.Edits = step.Edits.Where(e => !string.IsNullOrWhiteSpace(e.File)).ToList();

        return plan;
    }

    private string SystemPrompt() =>
        $"""
         You are a build engineer preparing an application for localization.

         A tool has just extracted the app's hardcoded UI strings, translated them, and written
         translation bundles. It is about to rewrite the source so those strings are read from the
         bundles instead. Your job is to decide what the project still needs for that to actually
         work, by reading the project's own build and configuration files.

         Report a step for each thing that is missing. For each step decide a severity:

         - "Blocking": after the rewrite the app would be broken for the people who use it. That
           includes code that no longer compiles, and also an app that builds but now shows resource
           keys where readable text used to be. The tool refuses to rewrite anything while a
           blocking step is outstanding, so use it when the rewrite would make the app worse than it
           is today.
         - "Recommended": the app keeps working exactly as it does now, it just does not gain
           anything yet - most often because nothing ever selects a language, so every user still
           sees the source language.

         Where you can express the fix as a concrete edit, attach one. Rules for edits, all enforced
         by the tool, so an edit that breaks them is discarded and the step becomes manual:

         - Only the files listed in the input may be edited. A file marked exists=false may only be
           created (kind "CreateFile"); one that exists may not.
         - "ReplaceText" and "InsertAfter" need an "anchor": an exact, verbatim snippet copied from
           that file's content, including its indentation. It must occur EXACTLY ONCE in the file.
           Keep it short but unambiguous - one or two lines is usually right.
         - Change as little as possible. These are files people maintain by hand: do not reorder
           keys, reformat, upgrade versions, or tidy anything you were not asked to. Match the
           file's existing indentation and quoting style.
         - Do NOT propose edits inside the application's own source code - a widget tree, a startup
           method, a view. Those belong in the step's detail as an instruction with the exact
           snippet, and no edit attached. Build files, manifests, and configuration are fair game.

         The detail is read by a developer deciding whether to trust you: say what is wrong, what
         the consequence is, and what the change does. Be specific and brief. If the project is
         already properly set up, return no steps at all - do not invent work.
         """;

    private static readonly object Schema = new
    {
        type = "object",
        properties = new
        {
            steps = new
            {
                type = "array",
                description = "One entry per thing the project is missing. Empty when it is ready.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string", description = "Short imperative summary." },
                        detail = new { type = "string", description = "What is wrong, the consequence, and what the fix does." },
                        severity = new
                        {
                            type = "string",
                            @enum = new[] { "Blocking", "Recommended" },
                            description = "Blocking when the rewrite would leave the app worse for its users."
                        },
                        edits = new
                        {
                            type = "array",
                            description = "Concrete changes. Empty when this must be done by hand in the app's own code.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    file = new { type = "string", description = "Path exactly as given in the input." },
                                    kind = new
                                    {
                                        type = "string",
                                        @enum = new[] { "CreateFile", "ReplaceText", "InsertAfter", "AppendToFile" }
                                    },
                                    anchor = new
                                    {
                                        type = "string",
                                        description = "Verbatim snippet occurring exactly once. Required for ReplaceText and InsertAfter."
                                    },
                                    content = new { type = "string", description = "Replacement, inserted text, or whole file body." },
                                    reason = new { type = "string", description = "One line on what this edit does." }
                                },
                                required = new[] { "file", "kind", "content", "reason" }
                            }
                        }
                    },
                    required = new[] { "title", "detail", "severity", "edits" }
                }
            }
        },
        required = new[] { "steps" }
    };
}
