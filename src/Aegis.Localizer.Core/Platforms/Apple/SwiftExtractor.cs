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

using System.Text;
using System.Text.RegularExpressions;
using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;

namespace Aegis.Localizer.Platforms.Apple;

/// <summary>
/// Pulls user-visible copy out of Swift.
///
/// The rule everywhere here is accept-list, not reject-list: a literal is kept only when it sits in
/// a place we recognise as UI (a SwiftUI view initialiser, a known modifier, a UI argument label, a
/// UI property assignment). Swift string literals are used for far more than copy - keys, selectors,
/// asset names, identifiers - and guessing wrong costs the user a broken build, so anything we
/// cannot place is left alone.
/// </summary>
public static partial class SwiftExtractor
{
    /// <summary>SwiftUI initialisers and modifiers whose string argument is shown to the user.</summary>
    private static readonly HashSet<string> UiCalls = new(StringComparer.Ordinal)
    {
        "Text", "Button", "Label", "TextField", "SecureField", "TextEditor", "Toggle", "Picker",
        "Section", "NavigationLink", "Link", "Menu", "Stepper", "LabeledContent", "DisclosureGroup",
        "GroupBox", "Alert", "TextFieldAlert",
        "navigationTitle", "navigationBarTitle", "navigationBarTitleDisplayMode", "alert",
        "confirmationDialog", "actionSheet", "help", "searchable", "accessibilityLabel",
        "accessibilityHint", "accessibilityValue", "tabItem",
        // UIKit / AppKit.
        "setTitle", "setPlaceholder", "setPrompt"
    };

    /// <summary>Argument labels that carry copy regardless of which call they belong to.</summary>
    private static readonly HashSet<string> UiLabels = new(StringComparer.Ordinal)
    {
        "placeholder", "prompt", "title", "message", "header", "footer", "description", "hint",
        "label", "titleKey", "actionTitle", "confirmationTitle", "subtitle"
    };

    /// <summary>Properties whose assigned value ends up on screen.</summary>
    private static readonly HashSet<string> UiProperties = new(StringComparer.Ordinal)
    {
        "text", "placeholder", "title", "prompt", "message", "stringValue", "toolTip", "headerTitle",
        "footerTitle"
    };

    /// <summary>Calls whose string argument is developer-facing; gated behind --include-diagnostics.</summary>
    private static readonly HashSet<string> DiagnosticCalls = new(StringComparer.Ordinal)
    {
        "print", "debugPrint", "dump", "NSLog", "os_log", "fatalError", "assertionFailure",
        "preconditionFailure", "assert", "precondition"
    };

    /// <summary>
    /// Calls that already produce a localized string. Skipping them is what makes a second --apply
    /// run a no-op instead of wrapping the key again.
    /// </summary>
    private static readonly HashSet<string> AlreadyLocalized = new(StringComparer.Ordinal)
    {
        "NSLocalizedString", "NSLocalizedStringFromTable", "NSLocalizedStringWithDefaultValue",
        "LocalizedStringKey", "String", "AttributedString"
    };

    /// <summary>
    /// Labels that mean "do not translate this": Text(verbatim:) is the SwiftUI opt-out, and the
    /// rest name a lookup key rather than copy.
    /// </summary>
    private static readonly HashSet<string> OptOutLabels = new(StringComparer.Ordinal)
    {
        "verbatim", "localized", "key", "tableName", "bundle", "comment", "forKey", "named",
        "systemName", "identifier", "withIdentifier", "reuseIdentifier", "withReuseIdentifier",
        "rawValue", "string", "url", "path", "nibName", "font", "ofType"
    };

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var (literals, masked) = Scan(content);
        var declarations = Declarations(masked);

        foreach (var literal in literals)
        {
            // Interpolated copy would need a format string plus reordered arguments; reported only
            // when the model asks for it is still better than a rewrite that drops the values.
            if (!literal.Rewritable) continue;
            if (NoiseFilter.IsNoise(literal.Value)) continue;

            var kind = Classify(masked, literal.Start);
            if (kind is null) continue;

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = CodeContext.LineOf(content, literal.Start),
                SpanStart = literal.Start,
                SpanLength = literal.Length,
                Text = literal.Value,
                RawSpanText = content.Substring(literal.Start, literal.Length),
                // No Swift-specific kind exists; CSharpLiteral is the enum's "plain code literal".
                Kind = kind.Value,
                Context = CodeContext.Snippet(content, literal.Start, literal.Length),
                Member = CodeContext.DeclarationBefore(declarations, literal.Start)
            };
        }
    }

    /// <summary>Decides what the literal is, or null when it should not be touched at all.</summary>
    private static CandidateKind? Classify(string masked, int start)
    {
        var call = CodeContext.StripDot(CodeContext.EnclosingCall(masked, start));
        var label = CodeContext.ArgumentLabel(masked, start);

        if (call is not null && AlreadyLocalized.Contains(call)) return null;
        if (label is not null && OptOutLabels.Contains(label)) return null;

        if (call is not null && DiagnosticCalls.Contains(call)) return CandidateKind.Diagnostic;
        if (call is not null && UiCalls.Contains(call)) return CandidateKind.CodeLiteral;
        if (label is not null && UiLabels.Contains(label)) return CandidateKind.CodeLiteral;

        var target = CodeContext.AssignmentTarget(masked, start);
        if (target is not null && UiProperties.Contains(CodeContext.LastComponent(target)))
            return CandidateKind.CodeLiteral;

        return null;
    }

    /// <summary>A literal found by the lexer. Non-rewritable ones are interpolated.</summary>
    private readonly record struct Literal(int Start, int Length, string Value, bool Rewritable);

    /// <summary>
    /// Single pass over the file that both collects literals with exact offsets and produces the
    /// masked copy the classifier questions. Handles the four Swift spellings: "plain", """multi
    /// line""", #"raw"# and \(interpolated) holes.
    /// </summary>
    private static (List<Literal> Literals, string Masked) Scan(string source)
    {
        var literals = new List<Literal>();
        var masked = new StringBuilder(source);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    CodeContext.Blank(masked, i);
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i = SkipBlockComment(source, masked, i);
                continue;
            }

            if (c == '#')
            {
                var hashes = 0;
                var j = i;
                while (j < source.Length && source[j] == '#')
                {
                    hashes++;
                    j++;
                }

                // #"raw"# has no escapes and no interpolation; masked out, never a candidate,
                // because raw strings are regexes and paths far more often than they are copy.
                if (j < source.Length && source[j] == '"')
                {
                    i = SkipRawString(source, masked, i, j, hashes);
                    continue;
                }

                i = j;
                continue;
            }

            if (c == '"')
            {
                i = ReadQuoted(source, masked, i, literals);
                continue;
            }

            i++;
        }

        return (literals, masked.ToString());
    }

    /// <summary>Swift block comments nest, so the depth has to be counted rather than searched for.</summary>
    private static int SkipBlockComment(string source, StringBuilder masked, int i)
    {
        var depth = 0;

        while (i < source.Length)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                depth++;
                CodeContext.Blank(masked, i);
                CodeContext.Blank(masked, i + 1);
                i += 2;
                continue;
            }

            if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
            {
                depth--;
                CodeContext.Blank(masked, i);
                CodeContext.Blank(masked, i + 1);
                i += 2;
                if (depth == 0) return i;
                continue;
            }

            CodeContext.Blank(masked, i);
            i++;
        }

        return i;
    }

    private static int SkipRawString(string source, StringBuilder masked, int start, int quote, int hashes)
    {
        var closing = "\"" + new string('#', hashes);
        var end = source.IndexOf(closing, quote + 1, StringComparison.Ordinal);
        var stop = end < 0 ? source.Length : end + closing.Length;

        for (var k = start; k < stop; k++) CodeContext.Blank(masked, k);
        return stop;
    }

    private static int ReadQuoted(string source, StringBuilder masked, int start, List<Literal> literals)
    {
        // Multi-line literals are masked but never offered: their content is usually a template and
        // the surrounding indentation is part of the value.
        if (start + 2 < source.Length && source[start + 1] == '"' && source[start + 2] == '"')
        {
            var end = source.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            var stop = end < 0 ? source.Length : end + 3;

            for (var k = start; k < stop; k++) CodeContext.Blank(masked, k);
            return stop;
        }

        var value = new StringBuilder();
        var interpolated = false;
        var i = start + 1;

        CodeContext.Blank(masked, start);

        while (i < source.Length && source[i] != '"')
        {
            // An unterminated literal means our lexing has drifted; stop before it eats the file.
            if (source[i] == '\n') return i;

            if (source[i] == '\\' && i + 1 < source.Length)
            {
                if (source[i + 1] == '(')
                {
                    interpolated = true;
                    i = SkipInterpolation(source, masked, i);
                    continue;
                }

                value.Append(Unescape(source[i + 1]));
                CodeContext.Blank(masked, i);
                CodeContext.Blank(masked, i + 1);
                i += 2;
                continue;
            }

            value.Append(source[i]);
            CodeContext.Blank(masked, i);
            i++;
        }

        if (i >= source.Length) return i;

        CodeContext.Blank(masked, i);
        literals.Add(new Literal(start, i - start + 1, value.ToString(), !interpolated));
        return i + 1;
    }

    /// <summary>
    /// Skips a \(...) hole with parenthesis depth, so an expression containing a quote cannot end
    /// the literal early and desynchronise everything after it.
    /// </summary>
    private static int SkipInterpolation(string source, StringBuilder masked, int i)
    {
        CodeContext.Blank(masked, i);
        CodeContext.Blank(masked, i + 1);
        i += 2;

        var depth = 1;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')') depth--;

            CodeContext.Blank(masked, i);
            i++;
        }

        return i;
    }

    private static string Unescape(char escaped) => escaped switch
    {
        'n' => "\n",
        't' => "\t",
        'r' => "\r",
        '0' => "\0",
        _ => escaped.ToString()
    };

    private static List<(int Index, string Name)> Declarations(string masked) =>
        Declaration().Matches(masked)
            .Select(m => (m.Index, m.Groups["name"].Value))
            .ToList();

    [GeneratedRegex(@"\b(?:func|var|let|class|struct|enum|extension|protocol)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex Declaration();
}
