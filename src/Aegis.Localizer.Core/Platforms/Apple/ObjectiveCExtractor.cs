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
/// Pulls user-visible copy out of Objective-C.
///
/// Only NSString literals (@"...") are candidates. Plain C literals ("...") are masked so they
/// cannot confuse the scan, but never offered: in Objective-C they are format specifiers, selector
/// names and file paths, essentially never something a user reads.
///
/// Copy is recognised by the selector keyword in front of the literal, which in message-send syntax
/// is exactly where the intent lives: [button setTitle:@"Save" forState:...].
/// </summary>
public static partial class ObjectiveCExtractor
{
    /// <summary>Selector keywords whose argument is shown to the user.</summary>
    private static readonly HashSet<string> UiKeywords = new(StringComparer.Ordinal)
    {
        "setTitle", "setText", "setPlaceholder", "setPlaceholderString", "setStringValue",
        "setPrompt", "setToolTip", "setMessage", "setLabel", "setHeaderTitle", "setFooterTitle",
        "title", "text", "placeholder", "message", "prompt", "label", "subtitle",
        "initWithTitle", "actionWithTitle", "alertControllerWithTitle", "alertWithTitle",
        "buttonWithTitle", "itemWithTitle", "addButtonWithTitle"
    };

    /// <summary>Properties whose assigned value ends up on screen.</summary>
    private static readonly HashSet<string> UiProperties = new(StringComparer.Ordinal)
    {
        "text", "title", "placeholder", "stringValue", "prompt", "message", "toolTip"
    };

    /// <summary>Calls whose string argument is developer-facing; gated behind --include-diagnostics.</summary>
    private static readonly HashSet<string> DiagnosticCalls = new(StringComparer.Ordinal)
    {
        "NSLog", "os_log", "printf", "fprintf", "NSAssert", "NSCAssert"
    };

    /// <summary>
    /// Already-localized lookups. Skipping them is what makes a second --apply run a no-op instead
    /// of wrapping the key in another NSLocalizedString.
    /// </summary>
    private static readonly HashSet<string> AlreadyLocalized = new(StringComparer.Ordinal)
    {
        "NSLocalizedString", "NSLocalizedStringFromTable", "NSLocalizedStringFromTableInBundle",
        "NSLocalizedStringWithDefaultValue"
    };

    /// <summary>Keywords that name a key, an asset or a selector rather than copy.</summary>
    private static readonly HashSet<string> OptOutKeywords = new(StringComparer.Ordinal)
    {
        "forKey", "setValue", "setObject", "objectForKey", "valueForKey", "imageNamed",
        "systemImageNamed", "NSSelectorFromString", "NSClassFromString", "stringWithFormat",
        "initWithFormat", "URLWithString", "fileURLWithPath", "contentsOfFile", "ofType",
        "identifier", "reuseIdentifier", "withIdentifier", "dequeueReusableCellWithIdentifier",
        "fontWithName", "colorNamed", "pathForResource", "nibWithNibName"
    };

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var (literals, masked) = Scan(content);
        var declarations = Declarations(masked);

        foreach (var literal in literals)
        {
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
                // No Objective-C kind exists; CSharpLiteral is the enum's "plain code literal".
                Kind = kind.Value,
                Context = CodeContext.Snippet(content, literal.Start, literal.Length),
                Member = CodeContext.DeclarationBefore(declarations, literal.Start)
            };
        }
    }

    private static CandidateKind? Classify(string masked, int start)
    {
        var call = CodeContext.StripDot(CodeContext.EnclosingCall(masked, start));
        var keyword = CodeContext.ArgumentLabel(masked, start);

        if (call is not null && AlreadyLocalized.Contains(call)) return null;
        if (keyword is not null && (OptOutKeywords.Contains(keyword) || AlreadyLocalized.Contains(keyword)))
            return null;

        if (call is not null && DiagnosticCalls.Contains(call)) return CandidateKind.Diagnostic;
        if (keyword is not null && DiagnosticCalls.Contains(keyword)) return CandidateKind.Diagnostic;
        if (keyword is not null && UiKeywords.Contains(keyword)) return CandidateKind.CodeLiteral;

        var target = CodeContext.AssignmentTarget(masked, start);
        if (target is not null && UiProperties.Contains(CodeContext.LastComponent(target)))
            return CandidateKind.CodeLiteral;

        return null;
    }

    private readonly record struct Literal(int Start, int Length, string Value);

    /// <summary>
    /// Single pass collecting @"..." literals with exact offsets and masking every comment, C string
    /// and character literal so the classifier only ever looks at code.
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
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + 2;
                for (var k = i; k < stop; k++) CodeContext.Blank(masked, k);
                i = stop;
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(source, masked, i);
                continue;
            }

            // '@' immediately before the quote is what makes it an NSString rather than a C string.
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i = ReadQuoted(source, masked, i, i + 1, literals);
                continue;
            }

            if (c == '"')
            {
                i = ReadQuoted(source, masked, i, i, null);
                continue;
            }

            i++;
        }

        return (literals, masked.ToString());
    }

    /// <summary>Reads one quoted run; <paramref name="literals"/> is null for C strings we only mask.</summary>
    private static int ReadQuoted(
        string source, StringBuilder masked, int start, int quote, List<Literal>? literals)
    {
        var value = new StringBuilder();
        var i = quote + 1;

        for (var k = start; k <= quote; k++) CodeContext.Blank(masked, k);

        while (i < source.Length && source[i] != '"')
        {
            // An unterminated literal means our lexing has drifted; stop before it eats the file.
            if (source[i] == '\n') return i;

            if (source[i] == '\\' && i + 1 < source.Length)
            {
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
        literals?.Add(new Literal(start, i - start + 1, value.ToString()));
        return i + 1;
    }

    private static int SkipCharLiteral(string source, StringBuilder masked, int i)
    {
        CodeContext.Blank(masked, i);
        i++;

        while (i < source.Length && source[i] != '\'' && source[i] != '\n')
        {
            var step = source[i] == '\\' ? 2 : 1;
            for (var k = i; k < Math.Min(source.Length, i + step); k++) CodeContext.Blank(masked, k);
            i += step;
        }

        if (i < source.Length && source[i] == '\'')
        {
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

    /// <summary>Interface / implementation blocks and method definitions, for the Member column.</summary>
    [GeneratedRegex(@"@(?:interface|implementation)\s+(?<name>[A-Za-z_]\w*)|^\s*[-+]\s*\([^)]*\)\s*(?<name>[A-Za-z_]\w*)",
        RegexOptions.Multiline)]
    private static partial Regex Declaration();
}
