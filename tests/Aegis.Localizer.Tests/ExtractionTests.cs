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

using Aegis.Localizer.Model;
using Aegis.Localizer.Platforms.DotNet;
using Xunit;

namespace Aegis.Localizer.Tests;

public class CSharpExtractorTests
{
    private static List<StringCandidate> Extract(string code) =>
        CSharpExtractor.Extract("Test.cs", "Test.cs", code).ToList();

    [Fact]
    public void FindsPlainCopy()
    {
        var found = Extract("""class C { string T => "Save changes"; }""");

        Assert.Contains(found, c => c.Text == "Save changes");
    }

    /// <summary>Every candidate must carry a span the rewriter can verify byte for byte.</summary>
    [Fact]
    public void SpansPointAtTheOriginalText()
    {
        const string code = """class C { string T => "Save changes"; }""";

        var candidate = Assert.Single(Extract(code));

        Assert.Equal(code.Substring(candidate.SpanStart, candidate.SpanLength), candidate.RawSpanText);
        Assert.Equal("\"Save changes\"", candidate.RawSpanText);
    }

    [Theory]
    [InlineData("""class C { bool T(string s) => s == "admin"; }""")]
    [InlineData("""class C { bool T(string s) => s.StartsWith("api/"); }""")]
    [InlineData("""class C { string T() => nameof(C); }""")]
    [InlineData("""class C { [Route("api/cars")] void T() { } }""")]
    public void SkipsConstructsWhereRewritingWouldChangeBehaviour(string code) =>
        Assert.Empty(Extract(code));

    [Fact]
    public void SwitchArmResultsAreCopyButPatternsAreNot()
    {
        var found = Extract("""
                            class C
                            {
                                string T(string s) => s switch { "pending" => "Waiting for approval", _ => "Unknown" };
                            }
                            """);

        Assert.Contains(found, c => c.Text == "Waiting for approval");
        Assert.Contains(found, c => c.Text == "Unknown");
        Assert.DoesNotContain(found, c => c.Text == "pending");
    }

    [Fact]
    public void InterpolationBecomesAFormatStringWithItsArguments()
    {
        var found = Extract("""class C { string T(string n, decimal t) => $"Booking for {n}: {t}"; }""");

        var candidate = Assert.Single(found);
        Assert.Equal("Booking for {0}: {1}", candidate.Text);
        Assert.True(candidate.IsInterpolated);
        Assert.Equal(new[] { "n", "t" }, candidate.InterpolationArgs);
    }

    /// <summary>Alignment and format clauses cannot be reproduced by string.Format on a resource.</summary>
    [Fact]
    public void InterpolationWithAFormatClauseIsNotRewritable()
    {
        var candidate = Assert.Single(Extract("""class C { string T(decimal t) => $"Total: {t:C}"; }"""));

        Assert.True(candidate.IsInterpolated);
        Assert.Null(candidate.InterpolationArgs);
    }

    [Fact]
    public void LogAndExceptionTextIsMarkedDiagnostic()
    {
        var found = Extract("""
                            class C
                            {
                                void T(Microsoft.Extensions.Logging.ILogger l)
                                {
                                    l.LogError("Pipeline failed");
                                    throw new InvalidOperationException("Bad state");
                                }
                            }
                            """);

        Assert.All(found, c => Assert.Equal(CandidateKind.Diagnostic, c.Kind));
    }

    /// <summary>
    /// Idempotency: a second --apply run must not re-extract the resource keys the first run wrote.
    /// </summary>
    [Fact]
    public void IgnoresKeysThisToolAlreadyWrote()
    {
        var found = Extract("""class C { string T(string n) => L.Format("BookingConfirmed", n); }""");

        Assert.Empty(found);
    }
}

public class MarkupExtractorTests
{
    [Fact]
    public void ReadsRazorTextAndUiAttributes()
    {
        const string html = """
                            <h1>Complete your booking</h1>
                            <input placeholder="Enter your promo code" class="form-control" />
                            """;

        var found = MarkupExtractor.ExtractRazor("P.razor", "P.razor", html).ToList();

        Assert.Contains(found, c => c.Text == "Complete your booking");
        Assert.Contains(found, c => c.Text == "Enter your promo code");
        Assert.DoesNotContain(found, c => c.Text == "form-control");
    }

    [Fact]
    public void SkipsRazorCodeBlocksAndExpressions()
    {
        const string html = """
                            <span>@Total</span>
                            @code {
                                private string Total => "0.00";
                            }
                            """;

        Assert.Empty(MarkupExtractor.ExtractRazor("P.razor", "P.razor", html));
    }

    [Fact]
    public void ReadsXamlUiAttributesButNotBindingsOrColors()
    {
        const string xaml = """
                            <Label Text="Notifications" FontSize="20" />
                            <Label Text="{Binding UserName}" />
                            <Button Text="Save changes" BackgroundColor="#2266EE" />
                            """;

        var found = MarkupExtractor.ExtractXaml("P.xaml", "P.xaml", xaml).ToList();

        Assert.Contains(found, c => c.Text == "Notifications");
        Assert.Contains(found, c => c.Text == "Save changes");
        Assert.DoesNotContain(found, c => c.Text.Contains("Binding"));
        Assert.DoesNotContain(found, c => c.Text == "#2266EE");
    }
}
