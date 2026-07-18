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

namespace Aegis.Localizer.Platforms.Flutter;

/// <summary>
/// Pulls user-visible copy out of Dart.
///
/// Two things make Dart awkward and both are handled in the lexer rather than the classifier:
/// literals come in single, double, triple and raw quotes, and adjacent literals concatenate with
/// nothing between them ('Hello ' 'world'), which has to be reported as one span or the rewrite
/// would leave half the sentence behind.
///
/// Recognition is accept-list only: a literal is kept when it is a Text widget's argument or sits
/// behind a known copy-carrying named parameter. Everything else - asset paths, route names, keys,
/// imports - is left alone.
/// </summary>
public static partial class DartExtractor
{
    /// <summary>Widgets whose positional string argument is displayed.</summary>
    private static readonly HashSet<string> UiWidgets = new(StringComparer.Ordinal)
    {
        "Text", "SelectableText"
    };

    /// <summary>Named parameters that carry copy regardless of the widget they belong to.</summary>
    private static readonly HashSet<string> UiLabels = new(StringComparer.Ordinal)
    {
        "hintText", "labelText", "helperText", "errorText", "counterText", "prefixText", "suffixText",
        "tooltip", "message", "title", "subtitle", "text", "semanticLabel", "confirmDismissText",
        "cancelText", "confirmText", "helpText", "fieldLabelText", "errorFormatText",
        "errorInvalidText", "placeholder"
    };

    /// <summary>Calls whose string argument is developer-facing; gated behind --include-diagnostics.</summary>
    private static readonly HashSet<string> DiagnosticCalls = new(StringComparer.Ordinal)
    {
        "print", "debugPrint", "log", "assert", "FlutterError", "throwError"
    };

    /// <summary>
    /// Widgets that install the localizations delegate themselves. Their own arguments are built
    /// before AppLocalizations exists in the tree, so a lookup there returns null and the generated
    /// `!` throws at runtime - copy here is reported, never rewritten.
    /// </summary>
    private static readonly HashSet<string> AppRootWidgets = new(StringComparer.Ordinal)
    {
        "MaterialApp", "CupertinoApp", "WidgetsApp"
    };

    /// <summary>Named parameters that name a key, an asset or a route rather than copy.</summary>
    private static readonly HashSet<string> OptOutLabels = new(StringComparer.Ordinal)
    {
        "key", "routeName", "initialRoute", "package", "fontFamily", "name", "id", "path", "asset",
        "value", "pattern", "locale", "restorationId", "debugLabel", "semanticsIdentifier"
    };

    private const string BuildMember = "build";

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var (literals, masked) = Scan(content);
        literals = MergeAdjacent(literals, masked);

        var buildBodies = BuildBodies(masked);
        var declarations = Declarations(masked);

        foreach (var literal in literals)
        {
            if (literal.Interpolated) continue;                  // '$name' holes cannot be swapped verbatim
            if (NoiseFilter.IsNoise(literal.Value)) continue;

            var kind = Classify(masked, literal.Start);
            if (kind is null) continue;

            var scope = Scope(masked, literal.Start, buildBodies, declarations);

            yield return new StringCandidate
            {
                FilePath = filePath,
                RelativePath = relativePath,
                Line = LineOf(content, literal.Start),
                SpanStart = literal.Start,
                SpanLength = literal.Length,
                Text = literal.Value,
                RawSpanText = content.Substring(literal.Start, literal.Length),
                Kind = kind.Value,
                Context = Snippet(content, literal.Start, literal.Length),
                Member = scope.Member,
                RewriteBlockedReason = scope.BlockedReason
            };
        }
    }

    private static CandidateKind? Classify(string masked, int start)
    {
        var call = StripDot(EnclosingCall(masked, start));
        var label = ArgumentLabel(masked, start);

        if (label is not null && OptOutLabels.Contains(label)) return null;

        if (call is not null && DiagnosticCalls.Contains(call)) return CandidateKind.Diagnostic;
        if (call is not null && UiWidgets.Contains(call)) return CandidateKind.CodeLiteral;
        if (label is not null && UiLabels.Contains(label)) return CandidateKind.CodeLiteral;

        return null;
    }

    /// <summary>
    /// Where the literal sits, and whether it can be rewritten there. The generated lookup reads
    /// `AppLocalizations.of(context)!`, so it is only safe inside a verified
    /// `Widget build(BuildContext context)` body and outside any const expression. Every other
    /// position gets an explicit block reason rather than being quietly assumed unsafe.
    /// </summary>
    private static (string? Member, string? BlockedReason) Scope(
        string masked,
        int start,
        IReadOnlyList<(int Start, int End)> buildBodies,
        IReadOnlyList<(int Index, string Name)> declarations)
    {
        var declared = declarations.LastOrDefault(d => d.Index < start).Name;
        var member = string.IsNullOrEmpty(declared) ? null : declared;

        if (!buildBodies.Any(range => start > range.Start && start < range.End))
        {
            // Includes a build method we could not verify, e.g. a renamed BuildContext parameter:
            // the replacement hardcodes the name `context`.
            return (member, "no BuildContext in scope");
        }

        if (InConstExpression(masked, start))
            return (BuildMember, "const expression - a lookup is not const");

        // These arguments are evaluated before the localizations delegate exists, so a lookup here
        // compiles and then throws at run time.
        var call = StripDot(EnclosingCall(masked, start));
        if (call is not null && AppRootWidgets.Contains(call))
            return (BuildMember, $"{call} argument - evaluated before localizations are available");

        return (BuildMember, null);
    }

    /// <summary>
    /// True when a const keyword appears between the nearest statement boundary and the literal.
    /// Rewriting inside a const expression would not compile, and a localized lookup is never const,
    /// so this deliberately over-rejects: the cost is a report-only entry, not a broken build.
    /// </summary>
    private static bool InConstExpression(string masked, int index)
    {
        var stop = Math.Max(0, index - 400);
        var i = index - 1;
        while (i >= stop && masked[i] is not (';' or '{' or '}')) i--;

        return ConstKeyword().IsMatch(masked[(i + 1)..index]);
    }

    /// <summary>Bodies of build methods we can prove take a parameter literally named `context`.</summary>
    private static List<(int Start, int End)> BuildBodies(string masked)
    {
        var ranges = new List<(int Start, int End)>();

        foreach (Match match in BuildSignature().Matches(masked))
        {
            var i = match.Index + match.Length;
            while (i < masked.Length && char.IsWhiteSpace(masked[i])) i++;
            if (i >= masked.Length) continue;

            if (masked[i] == '{')
            {
                var start = i;
                var depth = 0;

                for (; i < masked.Length; i++)
                {
                    if (masked[i] == '{') depth++;
                    else if (masked[i] == '}' && --depth == 0)
                    {
                        i++;
                        break;
                    }
                }

                ranges.Add((start, i));
                continue;
            }

            // Expression-bodied build: everything up to the terminating semicolon.
            if (masked[i] == '=' && i + 1 < masked.Length && masked[i + 1] == '>')
            {
                var end = masked.IndexOf(';', i);
                ranges.Add((i, end < 0 ? masked.Length : end));
            }
        }

        return ranges;
    }

    /// <summary>A literal found by the lexer.</summary>
    private readonly record struct Literal(int Start, int Length, string Value, bool Interpolated);

    /// <summary>
    /// Single pass collecting literals with exact offsets and masking every comment and string body,
    /// so the classifier and the brace matching only ever see code.
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
                    Blank(masked, i);
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i = SkipBlockComment(source, masked, i);
                continue;
            }

            // r'...' and r"..." take no escapes and no interpolation.
            if ((c == 'r' || c == 'R') &&
                i + 1 < source.Length && (source[i + 1] == '\'' || source[i + 1] == '"') &&
                (i == 0 || !IsIdentifier(source[i - 1])))
            {
                i = ReadString(source, masked, i, i + 1, raw: true, literals);
                continue;
            }

            if (c is '\'' or '"')
            {
                i = ReadString(source, masked, i, i, raw: false, literals);
                continue;
            }

            i++;
        }

        return (literals, masked.ToString());
    }

    private static int SkipBlockComment(string source, StringBuilder masked, int i)
    {
        // Dart block comments nest, so the depth has to be counted rather than searched for.
        var depth = 0;

        while (i < source.Length)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                depth++;
                Blank(masked, i);
                Blank(masked, i + 1);
                i += 2;
                continue;
            }

            if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
            {
                depth--;
                Blank(masked, i);
                Blank(masked, i + 1);
                i += 2;
                if (depth == 0) return i;
                continue;
            }

            Blank(masked, i);
            i++;
        }

        return i;
    }

    /// <param name="start">Offset of the whole literal, including a raw prefix.</param>
    /// <param name="quote">Offset of the opening quote.</param>
    private static int ReadString(
        string source, StringBuilder masked, int start, int quote, bool raw, List<Literal> literals)
    {
        var delimiter = source[quote];
        var triple = quote + 2 < source.Length && source[quote + 1] == delimiter && source[quote + 2] == delimiter;

        if (triple)
        {
            // Multi-line literals are masked but never offered: their indentation is part of the
            // value and rewriting one would change the layout as well as the words.
            var terminator = new string(delimiter, 3);
            var end = source.IndexOf(terminator, quote + 3, StringComparison.Ordinal);
            var stop = end < 0 ? source.Length : end + 3;

            for (var k = start; k < stop; k++) Blank(masked, k);
            return stop;
        }

        var value = new StringBuilder();
        var interpolated = false;
        var i = quote + 1;

        for (var k = start; k <= quote; k++) Blank(masked, k);

        while (i < source.Length && source[i] != delimiter)
        {
            // An unterminated literal means our lexing has drifted; stop before it eats the file.
            if (source[i] == '\n') return i;

            if (!raw && source[i] == '\\' && i + 1 < source.Length)
            {
                value.Append(Unescape(source[i + 1]));
                Blank(masked, i);
                Blank(masked, i + 1);
                i += 2;
                continue;
            }

            if (!raw && source[i] == '$')
            {
                interpolated = true;
                i = SkipInterpolation(source, masked, i);
                continue;
            }

            value.Append(source[i]);
            Blank(masked, i);
            i++;
        }

        if (i >= source.Length) return i;

        Blank(masked, i);
        literals.Add(new Literal(start, i - start + 1, value.ToString(), interpolated));
        return i + 1;
    }

    /// <summary>Skips a $identifier or a ${expression} hole, counting braces for the latter.</summary>
    private static int SkipInterpolation(string source, StringBuilder masked, int i)
    {
        Blank(masked, i);
        i++;

        if (i < source.Length && source[i] == '{')
        {
            var depth = 0;

            while (i < source.Length)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}') depth--;

                Blank(masked, i);
                i++;
                if (depth == 0) break;
            }

            return i;
        }

        while (i < source.Length && (IsIdentifier(source[i]) || source[i] == '.'))
        {
            Blank(masked, i);
            i++;
        }

        return i;
    }

    /// <summary>
    /// Folds Dart's adjacent-literal concatenation into one candidate. The span has to cover every
    /// piece, otherwise a rewrite would replace the first fragment and leave the rest dangling.
    /// </summary>
    private static List<Literal> MergeAdjacent(List<Literal> literals, string masked)
    {
        var merged = new List<Literal>(literals.Count);

        foreach (var literal in literals)
        {
            if (merged.Count == 0)
            {
                merged.Add(literal);
                continue;
            }

            var previous = merged[^1];
            var gapStart = previous.Start + previous.Length;

            // Comments were blanked to spaces, so "only whitespace" also covers a comment between
            // the two fragments.
            var adjacent = literal.Start >= gapStart &&
                           masked.AsSpan(gapStart, literal.Start - gapStart).IsWhiteSpace();

            if (!adjacent)
            {
                merged.Add(literal);
                continue;
            }

            merged[^1] = new Literal(
                previous.Start,
                literal.Start + literal.Length - previous.Start,
                previous.Value + literal.Value,
                previous.Interpolated || literal.Interpolated);
        }

        return merged;
    }

    private static string? EnclosingCall(string masked, int index)
    {
        var depth = 0;
        var stop = Math.Max(0, index - 600);

        for (var i = index - 1; i >= stop; i--)
        {
            switch (masked[i])
            {
                case ')' or ']' or '}':
                    depth++;
                    break;

                case '[' or '{':
                    // Left a list literal or a block without meeting a call: nothing to report.
                    if (depth == 0) return null;
                    depth--;
                    break;

                case '(':
                    if (depth > 0)
                    {
                        depth--;
                        break;
                    }

                    return NameBefore(masked, i);

                case ';':
                    if (depth == 0) return null;
                    break;
            }
        }

        return null;
    }

    private static string? ArgumentLabel(string masked, int index)
    {
        var i = SkipSpaceBack(masked, index - 1);
        if (i < 0 || masked[i] != ':') return null;

        i--;
        var end = i + 1;
        while (i >= 0 && IsIdentifier(masked[i])) i--;

        return end > i + 1 ? masked[(i + 1)..end] : null;
    }

    private static string? NameBefore(string masked, int openParen)
    {
        var i = SkipSpaceBack(masked, openParen - 1);
        var end = i + 1;
        while (i >= 0 && IsIdentifier(masked[i])) i--;

        var start = i + 1;
        if (end <= start) return null;

        var name = masked[start..end];
        return i >= 0 && masked[i] == '.' ? "." + name : name;
    }

    private static string? StripDot(string? name) =>
        name is not null && name.StartsWith('.') ? name[1..] : name;

    private static int SkipSpaceBack(string masked, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(masked[index])) index--;
        return index;
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void Blank(StringBuilder masked, int index)
    {
        if (masked[index] is not ('\r' or '\n')) masked[index] = ' ';
    }

    private static string Unescape(char escaped) => escaped switch
    {
        'n' => "\n",
        't' => "\t",
        'r' => "\r",
        '0' => "\0",
        _ => escaped.ToString()
    };

    private static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static string Snippet(string content, int index, int length)
    {
        var from = Math.Max(0, index - 70);
        var to = Math.Min(content.Length, index + length + 70);
        var s = content[from..to].Replace('\r', ' ').Replace('\n', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }

    private static List<(int Index, string Name)> Declarations(string masked) =>
        Declaration().Matches(masked)
            .Select(m => (m.Index, m.Groups["name"].Value))
            .ToList();

    [GeneratedRegex(@"\bWidget\s+build\s*\(\s*BuildContext\s+context\s*[,)]")]
    private static partial Regex BuildSignature();

    [GeneratedRegex(@"\bconst\b")]
    private static partial Regex ConstKeyword();

    [GeneratedRegex(@"\b(?:class|mixin|extension)\s+(?<name>[A-Za-z_]\w*)|(?:^|\s)(?<name>[A-Za-z_]\w*)\s*\([^()\n]*\)\s*(?:async\s*)?\{",
        RegexOptions.Multiline)]
    private static partial Regex Declaration();
}
