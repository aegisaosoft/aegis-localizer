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
using Aegis.Localizer.Filtering;
using Aegis.Localizer.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aegis.Localizer.Platforms.DotNet;

/// <summary>
/// Pulls string literals out of C# using a real syntax tree, so verbatim, raw and interpolated
/// strings all decode correctly and every candidate carries an exact span for rewriting.
/// </summary>
public static class CSharpExtractor
{
    /// <summary>String methods whose literal argument is a comparison value, never displayed copy.</summary>
    private static readonly HashSet<string> ComparisonMethods = new(StringComparer.Ordinal)
    {
        "Equals", "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Split",
        "Replace", "TrimStart", "TrimEnd", "Trim", "GetString", "GetSection", "GetValue",
        "GetConnectionString", "GetEnvironmentVariable", "TryGetValue", "Parse", "ParseExact"
    };

    public static IEnumerable<StringCandidate> Extract(string filePath, string relativePath, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            StringCandidate? candidate = node switch
            {
                LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
                    => FromLiteral(lit, filePath, relativePath, content, tree),
                InterpolatedStringExpressionSyntax interp
                    => FromInterpolated(interp, filePath, relativePath, content, tree),
                _ => null
            };

            if (candidate is not null) yield return candidate;
        }
    }

    private static StringCandidate? FromLiteral(
        LiteralExpressionSyntax lit, string filePath, string relativePath, string content, SyntaxTree tree)
    {
        var value = lit.Token.ValueText;
        if (NoiseFilter.IsNoise(value)) return null;
        if (IsStructurallyExcluded(lit)) return null;

        var kind = ClassifyContext(lit);
        if (kind is null) return null;

        return Build(lit, value, kind.Value, filePath, relativePath, content, tree, null);
    }

    private static StringCandidate? FromInterpolated(
        InterpolatedStringExpressionSyntax interp, string filePath, string relativePath, string content, SyntaxTree tree)
    {
        // Nested interpolations inside a hole are handled by the outer string; skip the inner ones.
        if (interp.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Any()) return null;
        if (IsStructurallyExcluded(interp)) return null;

        var sb = new StringBuilder();
        var args = new List<string>();
        var rewritable = true;

        foreach (var part in interp.Contents)
        {
            switch (part)
            {
                case InterpolatedStringTextSyntax t:
                    // Braces already in the copy must survive as literal braces in a format string.
                    sb.Append(t.TextToken.ValueText.Replace("{", "{{").Replace("}", "}}"));
                    break;

                case InterpolationSyntax hole:
                    if (hole.AlignmentClause is not null || hole.FormatClause is not null) rewritable = false;
                    sb.Append('{').Append(args.Count).Append('}');
                    args.Add(hole.Expression.ToString());
                    break;
            }
        }

        var value = sb.ToString();
        if (args.Count == 0) return null;                 // no holes: the plain-literal path covers it
        if (NoiseFilter.IsNoise(value)) return null;

        var kind = ClassifyContext(interp);
        if (kind is null) return null;

        return Build(interp, value, kind.Value, filePath, relativePath, content, tree, rewritable ? args : null);
    }

    private static StringCandidate Build(
        SyntaxNode node, string value, CandidateKind kind,
        string filePath, string relativePath, string content, SyntaxTree tree,
        IReadOnlyList<string>? interpolationArgs)
    {
        var span = node.Span;
        var line = tree.GetLineSpan(span).StartLinePosition.Line + 1;

        return new StringCandidate
        {
            FilePath = filePath,
            RelativePath = relativePath,
            Line = line,
            SpanStart = span.Start,
            SpanLength = span.Length,
            Text = value,
            RawSpanText = content.Substring(span.Start, span.Length),
            Kind = kind,
            Context = Snippet(node),
            Member = EnclosingMember(node),
            IsInterpolated = node is InterpolatedStringExpressionSyntax,
            InterpolationArgs = interpolationArgs
        };
    }

    /// <summary>Contexts where replacing the literal would change behaviour rather than wording.</summary>
    private static bool IsStructurallyExcluded(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            // Comparisons and pattern matches test values; they must stay literal.
            if (ancestor is BinaryExpressionSyntax bin &&
                (bin.IsKind(SyntaxKind.EqualsExpression) || bin.IsKind(SyntaxKind.NotEqualsExpression)))
                return true;

            if (ancestor is CaseSwitchLabelSyntax or ConstantPatternSyntax or UsingDirectiveSyntax)
                return true;

            if (ancestor is SwitchExpressionArmSyntax arm && arm.Pattern.Contains(node))
                return true;

            if (ancestor is InvocationExpressionSyntax inv)
            {
                // Resource keys we ourselves wrote in an earlier --apply run: L.Format("Key", ...).
                if (inv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Expression.ToString() == DotNetAdapter.AccessorClassName)
                    return true;

                var name = MethodName(inv);
                // Only the innermost call decides; stop climbing either way.
                return name == "nameof" || ComparisonMethods.Contains(name);
            }

            if (ancestor is AttributeSyntax attr)
            {
                var attrName = attr.Name.ToString().Split('.').Last();
                if (attrName.EndsWith("Attribute", StringComparison.Ordinal))
                    attrName = attrName[..^"Attribute".Length];
                return NoiseFilter.NonUiAttributes.Contains(attrName);
            }
        }

        return false;
    }

    /// <summary>Decides the candidate kind, or null when the literal should be dropped.</summary>
    private static CandidateKind? ClassifyContext(SyntaxNode node)
    {
        if (node.Ancestors().OfType<AttributeSyntax>().Any())
            return CandidateKind.Attribute;

        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case InvocationExpressionSyntax inv when NoiseFilter.DiagnosticMethods.Contains(MethodName(inv)):
                    return CandidateKind.Diagnostic;

                case ObjectCreationExpressionSyntax oc when oc.Type.ToString().EndsWith("Exception", StringComparison.Ordinal):
                    return CandidateKind.Diagnostic;

                case ThrowStatementSyntax:
                case ThrowExpressionSyntax:
                    return CandidateKind.Diagnostic;

                case MethodDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case ClassDeclarationSyntax:
                    return CandidateKind.CSharpLiteral;
            }
        }

        return CandidateKind.CSharpLiteral;
    }

    private static string MethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => string.Empty
    };

    private static string? EnclosingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m: return m.Identifier.Text;
                case PropertyDeclarationSyntax p: return p.Identifier.Text;
                case ConstructorDeclarationSyntax c: return c.Identifier.Text + " (ctor)";
                case FieldDeclarationSyntax f: return f.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                case TypeDeclarationSyntax t: return t.Identifier.Text;
            }
        }

        return null;
    }

    /// <summary>Smallest enclosing statement, trimmed, as prompt context.</summary>
    private static string Snippet(SyntaxNode node)
    {
        SyntaxNode target = node.FirstAncestorOrSelf<StatementSyntax>()
                            ?? node.FirstAncestorOrSelf<AttributeSyntax>()
                            ?? (SyntaxNode?)node.FirstAncestorOrSelf<MemberDeclarationSyntax>()
                            ?? node;

        var s = target.ToString().Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Trim();
        return s.Length <= 240 ? s : s[..240] + "...";
    }
}
