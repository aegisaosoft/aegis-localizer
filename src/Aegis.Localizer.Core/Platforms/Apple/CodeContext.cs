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

namespace Aegis.Localizer.Platforms.Apple;

/// <summary>
/// Lexical helpers shared by the Swift and Objective-C extractors.
///
/// There is no Roslyn for Swift, so instead of parsing we lex once into a *masked* copy of the file
/// - every comment body and every string body replaced by spaces, offsets untouched - and then ask
/// simple questions of that copy. Masking first is what makes the questions safe: a colon, brace or
/// parenthesis inside a string can no longer be mistaken for code.
/// </summary>
internal static class CodeContext
{
    /// <summary>How far back a lookup scans. A call site is always a few tokens away from its argument.</summary>
    private const int LookBack = 600;

    /// <summary>Overwrites one character with a space, keeping line breaks so line numbers still work.</summary>
    public static void Blank(StringBuilder masked, int index)
    {
        if (masked[index] is not ('\r' or '\n')) masked[index] = ' ';
    }

    /// <summary>
    /// Name of the call whose argument list the offset sits in, with its leading dot kept so a
    /// member call (".navigationTitle") can be told apart from a free function ("Text").
    /// Null when the offset is not directly inside an argument list.
    /// </summary>
    public static string? EnclosingCall(string masked, int index)
    {
        var depth = 0;
        var stop = Math.Max(0, index - LookBack);

        for (var i = index - 1; i >= stop; i--)
        {
            switch (masked[i])
            {
                case ')' or ']' or '}':
                    depth++;
                    break;

                case '[' or '{':
                    // Left an array literal or a closure without meeting a call: nothing to report.
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

    /// <summary>
    /// The argument label immediately in front of the offset ("placeholder:" -&gt; "placeholder"), or
    /// null when the argument is positional.
    /// </summary>
    public static string? ArgumentLabel(string masked, int index)
    {
        var i = SkipSpaceBack(masked, index - 1);
        if (i < 0 || masked[i] != ':') return null;

        i--;
        var end = i + 1;
        while (i >= 0 && IsIdentifier(masked[i])) i--;

        return end > i + 1 ? masked[(i + 1)..end] : null;
    }

    /// <summary>
    /// The property path being assigned to when the offset is the right-hand side of an assignment
    /// ("cell.titleLabel?.text = " -&gt; "cell.titleLabel?.text"), otherwise null.
    /// </summary>
    public static string? AssignmentTarget(string masked, int index)
    {
        var i = SkipSpaceBack(masked, index - 1);
        if (i < 0 || masked[i] != '=') return null;

        // "==", "!=", "+=" and friends compare or combine; they are not plain assignments.
        if (i > 0 && masked[i - 1] is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/') return null;
        if (i + 1 < masked.Length && masked[i + 1] == '=') return null;

        i = SkipSpaceBack(masked, i - 1);
        var end = i + 1;
        while (i >= 0 && (IsIdentifier(masked[i]) || masked[i] is '.' or '?' or '!')) i--;

        return end > i + 1 ? masked[(i + 1)..end] : null;
    }

    /// <summary>Last component of a property path, with Swift's optional markers removed.</summary>
    public static string LastComponent(string path)
    {
        var dot = path.LastIndexOf('.');
        var tail = dot < 0 ? path : path[(dot + 1)..];
        return tail.Trim('?', '!');
    }

    /// <summary>Drops the leading dot of a member call so one accept-list can cover both spellings.</summary>
    public static string? StripDot(string? name) =>
        name is not null && name.StartsWith('.') ? name[1..] : name;

    public static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    /// <summary>One-line snippet around the span, given to the model as context.</summary>
    public static string Snippet(string content, int index, int length)
    {
        var from = Math.Max(0, index - 70);
        var to = Math.Min(content.Length, index + length + 70);
        var s = content[from..to].Replace('\r', ' ').Replace('\n', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }

    /// <summary>Nearest declaration name in front of the offset, used to fill Member in reports.</summary>
    public static string? DeclarationBefore(IReadOnlyList<(int Index, string Name)> declarations, int index)
    {
        string? name = null;
        foreach (var (at, candidate) in declarations)
        {
            if (at >= index) break;
            name = candidate;
        }

        return name;
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

    private static int SkipSpaceBack(string masked, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(masked[index])) index--;
        return index;
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';
}
