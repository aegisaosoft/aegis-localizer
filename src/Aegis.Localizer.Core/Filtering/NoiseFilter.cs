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

using System.Text.RegularExpressions;

namespace Aegis.Localizer.Filtering;

/// <summary>
/// Cheap deterministic pre-filter. Its only job is to keep obvious machine strings out of the
/// model requests: every dropped string is a token we do not pay for. It is intentionally
/// conservative - anything ambiguous is kept and left for the model to judge.
/// </summary>
public static partial class NoiseFilter
{
    /// <summary>True when the string is clearly not user-visible copy.</summary>
    public static bool IsNoise(string text)
    {
        var s = text.Trim();

        if (s.Length < 2) return true;
        if (!s.Any(char.IsLetter)) return true;

        // Must contain at least one ASCII letter run of 2+; "%s1" and friends are not copy.
        if (!AsciiWord().IsMatch(s)) return true;

        if (Url().IsMatch(s)) return true;
        if (FilePath().IsMatch(s)) return true;
        if (Guid().IsMatch(s)) return true;
        if (HexColor().IsMatch(s)) return true;
        if (MimeType().IsMatch(s)) return true;
        if (Sql().IsMatch(s)) return true;
        if (DateFormat().IsMatch(s)) return true;
        if (ScreamingSnake().IsMatch(s)) return true;

        // Dotted / slashed / underscored identifiers with no spaces: keys, type names, routes.
        if (!s.Contains(' ') && IdentifierLike().IsMatch(s)) return true;

        // Single lowercase token of 3 chars or fewer: "px", "utf", "id" - never worth a resource.
        if (!s.Contains(' ') && s.Length <= 3 && s.All(c => char.IsLower(c) || char.IsDigit(c))) return true;

        return false;
    }

    /// <summary>
    /// Names of methods whose string arguments are developer-facing, not user-facing. Matched
    /// case-insensitively so a local `log(...)` delegate is treated like `Logger.Log(...)`.
    /// </summary>
    public static readonly HashSet<string> DiagnosticMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log",
        "WriteLine", "Write", "Assert", "Fail", "TraceInformation", "TraceError", "TraceWarning",
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
    };

    /// <summary>Attributes whose string arguments are wiring, never copy.</summary>
    public static readonly HashSet<string> NonUiAttributes = new(StringComparer.Ordinal)
    {
        "Route", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "FromQuery", "FromRoute",
        "FromBody", "FromHeader", "FromForm", "JsonPropertyName", "JsonProperty", "Table", "Column",
        "ForeignKey", "InverseProperty", "Index", "Key", "Authorize", "Obsolete", "DebuggerDisplay",
        "Category", "DefaultValue", "Bind", "Produces", "Consumes", "SwaggerOperation", "ApiExplorerSettings"
    };

    /// <summary>XAML/HTML attributes that carry user-visible copy.</summary>
    public static readonly HashSet<string> UiAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Title", "Placeholder", "Header", "Content", "Label", "ToolTip", "ToolTipText",
        "Description", "Hint", "Caption", "Message", "PlaceholderText", "EmptyViewText",
        "placeholder", "alt", "title", "aria-label", "value"
    };

    [GeneratedRegex(@"[A-Za-z]{2,}")] private static partial Regex AsciiWord();
    [GeneratedRegex(@"^\s*(https?|ftp|ws|wss|mailto|data):", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\|\./|\.\./|/)[^\s]*$")] private static partial Regex FilePath();
    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$")] private static partial Regex Guid();
    [GeneratedRegex(@"^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")] private static partial Regex HexColor();
    [GeneratedRegex(@"^[a-z]+/[a-z0-9.+-]+$", RegexOptions.IgnoreCase)] private static partial Regex MimeType();
    /// <summary>
    /// A leading SQL verb is not enough: "Delete your account", "Update payment method" and
    /// "Create a booking" are ordinary button labels, and dropping them silently is the worst kind
    /// of miss - the string never reaches the model, so nothing in the report explains its absence.
    /// A second clause is required before something counts as a statement.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|MERGE|EXEC|WITH)\b.*\b(FROM|INTO|SET|VALUES|TABLE|WHERE|JOIN|PROCEDURE|VIEW|INDEX|DATABASE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Sql();
    [GeneratedRegex(@"^[yMdHhmsfFtTzZ:/.,\s\\'-]+$")] private static partial Regex DateFormat();
    [GeneratedRegex(@"^[A-Z0-9]+(_[A-Z0-9]+)+$")] private static partial Regex ScreamingSnake();
    [GeneratedRegex(@"^[A-Za-z0-9]+([._/\\:-][A-Za-z0-9]+)+$")] private static partial Regex IdentifierLike();
}
