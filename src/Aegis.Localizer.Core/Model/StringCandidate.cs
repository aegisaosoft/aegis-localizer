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

namespace Aegis.Localizer.Model;

/// <summary>Where in the source a literal was found. Drives filtering and the AI prompt.</summary>
public enum CandidateKind
{
    /// <summary>Plain string literal in C# code.</summary>
    CSharpLiteral,

    /// <summary>String passed to a logger / Debug.WriteLine / exception constructor.</summary>
    Diagnostic,

    /// <summary>String literal used as an attribute argument, e.g. [Display(Name = "...")].</summary>
    Attribute,

    /// <summary>Value of a XAML attribute such as Text=, Title=, Placeholder=.</summary>
    XamlAttribute,

    /// <summary>Inline text node inside XAML markup.</summary>
    XamlText,

    /// <summary>Markup text in a Razor view (.cshtml / .razor).</summary>
    RazorText,

    /// <summary>Value of an HTML attribute in a Razor view, e.g. placeholder=, title=, alt=.</summary>
    RazorAttribute,

    // The members above name .NET constructs. Other stacks use the neutral ones below: the kind is
    // handed to the classifier as a plain string, and telling the model a Dart string is a
    // "CSharpLiteral" is a small but free way to mislead it. The precise construct always rides
    // along in StringCandidate.Member.

    /// <summary>A plain string literal in code, on any stack.</summary>
    CodeLiteral,

    /// <summary>Text content in a markup document, on any stack.</summary>
    MarkupText,

    /// <summary>An attribute value in a markup document, on any stack.</summary>
    MarkupAttribute
}

/// <summary>
/// A single hardcoded string found in the scanned project. A record so the scanner can stamp ids
/// with `with` instead of hand-copying every field.
/// </summary>
public sealed record StringCandidate
{
    /// <summary>Stable per-run identifier, used to correlate AI answers back to candidates.</summary>
    public int Id { get; init; }

    /// <summary>Absolute path of the file the literal lives in.</summary>
    public required string FilePath { get; init; }

    /// <summary>Path relative to the scanned project root, for reports.</summary>
    public required string RelativePath { get; init; }

    /// <summary>1-based line number of the literal.</summary>
    public int Line { get; init; }

    /// <summary>Offset of the replaceable span in the raw file text.</summary>
    public int SpanStart { get; init; }

    /// <summary>Length of the replaceable span in the raw file text.</summary>
    public int SpanLength { get; init; }

    /// <summary>The literal's decoded text (no quotes, escapes resolved).</summary>
    public required string Text { get; init; }

    /// <summary>The exact source text of the span, used for safe verification before rewriting.</summary>
    public required string RawSpanText { get; init; }

    public CandidateKind Kind { get; init; }

    /// <summary>One-line snippet around the literal, given to the model as context.</summary>
    public required string Context { get; init; }

    /// <summary>Enclosing member / element name, when the extractor can determine it. Display only.</summary>
    public string? Member { get; init; }

    /// <summary>
    /// Set by an extractor that can see this occurrence must never be rewritten, even though the
    /// stack normally supports rewriting it - a const expression, a scope with no localization
    /// context in reach, and so on. The rewriter honours it before consulting the adapter, and the
    /// reason is surfaced in the report so the user knows what was left alone and why.
    ///
    /// This is deliberately its own field: encoding "do not rewrite" into a display name couples
    /// safety to a string meant for humans, and the guard silently fails open when that name changes.
    /// </summary>
    public string? RewriteBlockedReason { get; init; }

    /// <summary>True when the C# literal is interpolated and therefore cannot be replaced verbatim.</summary>
    public bool IsInterpolated { get; init; }

    /// <summary>
    /// For interpolated strings: the source text of each hole, in order, matching the {0}, {1}, ...
    /// placeholders in <see cref="Text"/>. Null when the string cannot be rewritten mechanically.
    /// </summary>
    public IReadOnlyList<string>? InterpolationArgs { get; init; }
}
