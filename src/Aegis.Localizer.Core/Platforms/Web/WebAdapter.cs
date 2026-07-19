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
using Aegis.Localizer.Model;
using Aegis.Localizer.Resources;
using Aegis.Localizer.Scanning;

namespace Aegis.Localizer.Platforms.Web;

/// <summary>Which JavaScript UI framework the tree is built on; decides the emitted bootstrap.</summary>
internal enum WebFramework
{
    Unknown,
    React,
    Vue,
    Angular,
    Svelte
}

/// <summary>
/// The web stack: React, Vue, Angular and plain HTML, all writing i18next-shaped JSON bundles.
///
/// Only the constructs whose rewrite is provably correct are rewritten; everything else is reported
/// so a human can decide. That asymmetry is deliberate - the tool edits repositories it did not
/// write, and a report costs a reader a minute where a bad edit costs a build.
/// </summary>
public sealed class WebAdapter : ISourceAdapter
{
    /// <summary>
    /// i18next re-exports the default instance's `t`, so this one line is enough for `t("Key")` to
    /// compile and resolve. The react-i18next hook is not used: it would additionally require a
    /// `const { t } = useTranslation()` inside every component body, which cannot be placed by an
    /// import-only rewrite.
    /// </summary>
    private const string TranslateImport = "import { t } from \"i18next\";";

    public string Name => "web";

    public string DisplayName => "Web (React, Vue, Angular, plain HTML)";

    public IReadOnlyCollection<string> Extensions { get; } =
        [".tsx", ".jsx", ".ts", ".js", ".vue", ".svelte", ".html", ".htm"];

    public ResourceFormat DefaultFormat => ResourceFormat.I18NextJson;

    public int DetectionScore(string projectRoot)
    {
        var manifest = FindPackageJson(projectRoot);
        if (manifest is not null)
            return ReadFramework(manifest) == WebFramework.Unknown ? 70 : 95;

        // No manifest: fall back to what is actually on disk. Component files are a stronger signal
        // than loose scripts, which a project of any stack may carry.
        if (HasFile(projectRoot, "*.tsx") || HasFile(projectRoot, "*.jsx") ||
            HasFile(projectRoot, "*.vue") || HasFile(projectRoot, "*.svelte")) return 65;

        return HasFile(projectRoot, "*.js") || HasFile(projectRoot, "*.ts") ? 40 : 0;
    }

    public string DefaultResourceDirectory(string projectRoot)
    {
        var publicDir = Path.Combine(projectRoot, "public");
        return Directory.Exists(publicDir)
            ? Path.Combine(publicDir, "locales")
            : Path.Combine(projectRoot, "locales");
    }

    public IEnumerable<StringCandidate> Extract(
        string filePath, string relativePath, string content, LocalizationRequest request)
    {
        if (IsTestFile(relativePath)) return [];

        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".tsx" or ".jsx" => JsxTextExtractor.Extract(filePath, relativePath, content, markup: true),
            ".ts" or ".js" => JsxTextExtractor.Extract(filePath, relativePath, content, markup: false),
            ".vue" => VueTemplateExtractor.Extract(filePath, relativePath, content),
            ".svelte" => HtmlTextExtractor.Extract(filePath, relativePath, content, "Svelte"),
            ".html" or ".htm" => HtmlTextExtractor.Extract(filePath, relativePath, content, "Html"),
            _ => []
        };
    }

    public RewritePlan? PlanRewrite(StringCandidate candidate, string key, LocalizationRequest request) =>
        Path.GetExtension(candidate.FilePath).ToLowerInvariant() switch
        {
            ".tsx" or ".jsx" => candidate.Kind switch
            {
                // Both the text node and the quoted attribute value are replaced whole, and a JSX
                // expression container is legal in either position.
                CandidateKind.MarkupText or CandidateKind.MarkupAttribute =>
                    new RewritePlan($"{{t(\"{key}\")}}", TranslateImport),

                CandidateKind.CodeLiteral => new RewritePlan($"t(\"{key}\")", TranslateImport),

                _ => null
            },

            ".ts" or ".js" => candidate.Kind == CandidateKind.CodeLiteral
                ? new RewritePlan($"t(\"{key}\")", TranslateImport)
                : null,

            ".vue" => candidate.Kind switch
            {
                // $t is injected globally by the emitted bootstrap, so the template needs no import.
                CandidateKind.MarkupText => new RewritePlan($"{{{{ $t(\"{key}\") }}}}"),

                CandidateKind.MarkupAttribute => VueTemplateExtractor.AttributeName(candidate.RawSpanText) is
                    { } name
                    ? new RewritePlan($":{name}='$t(\"{key}\")'")
                    : null,

                // The script block would need useI18n() in setup(), which an import cannot supply.
                _ => null
            },

            // Plain HTML has no module to import into and Svelte's dialect is not modelled here.
            _ => null
        };

    public LocalizationSetup InspectSetup(LocalizationRequest request, string resourceDir) =>
        WebSetup.Inspect(request, resourceDir);

    public IReadOnlyList<SetupStep> ApplySetup(LocalizationRequest request, string resourceDir, IRunLog log) =>
        WebSetup.Apply(request, resourceDir, log);

    public void EmitRuntime(
        IReadOnlyList<string> keys, LocalizationRequest request, string resourceDir, IRunLog log)
    {
        var path = Path.Combine(resourceDir, "i18n.ts");

        // Never clobber a bootstrap someone has since configured by hand.
        if (File.Exists(path))
        {
            log.Info($"  {path}  left as it is, already present");
            return;
        }

        var cultures = new List<string> { request.SourceLanguage };
        foreach (var language in request.Languages)
            if (!cultures.Contains(language, StringComparer.OrdinalIgnoreCase))
                cultures.Add(language);

        var manifest = FindPackageJson(request.ProjectPath);
        var framework = manifest is null ? WebFramework.Unknown : ReadFramework(manifest);

        Directory.CreateDirectory(resourceDir);
        File.WriteAllText(
            path, Bootstrap(framework, cultures, request.ResourceName), new UTF8Encoding(false));

        log.Info($"  {path}  {framework.ToString().ToLowerInvariant()} i18n bootstrap, {cultures.Count} cultures");
    }

    /// <summary>
    /// The bootstrap is written per framework because the wrong one does not merely misbehave, it
    /// fails to resolve: a Vue app has no react-i18next to import.
    /// </summary>
    private static string Bootstrap(WebFramework framework, List<string> cultures, string ns)
    {
        var sb = new StringBuilder();
        var source = cultures[0];

        sb.AppendLine("// Generated by aegis-localizer. Safe to edit: it is never overwritten once it exists.");
        sb.AppendLine("// Requires \"resolveJsonModule\": true in tsconfig.json.");
        sb.AppendLine();

        if (framework == WebFramework.Vue)
        {
            sb.AppendLine("import { createI18n } from \"vue-i18n\";");
            sb.AppendLine();
            foreach (var culture in cultures)
                sb.AppendLine($"import {Identifier(culture)} from \"./{culture}/{ns}.json\";");
            sb.AppendLine();
            sb.AppendLine("const i18n = createI18n({");
            sb.AppendLine("  legacy: false,");
            // Without this, $t is undefined in templates and every rewritten line renders blank.
            sb.AppendLine("  globalInjection: true,");
            sb.AppendLine($"  locale: \"{source}\",");
            sb.AppendLine($"  fallbackLocale: \"{source}\",");
            sb.AppendLine("  messages: {");
            foreach (var culture in cultures)
                sb.AppendLine($"    \"{culture}\": {Identifier(culture)},");
            sb.AppendLine("  }");
            sb.AppendLine("});");
            sb.AppendLine();
            sb.AppendLine("export default i18n;");
            return sb.ToString();
        }

        sb.AppendLine("import i18n from \"i18next\";");
        if (framework == WebFramework.React) sb.AppendLine("import { initReactI18next } from \"react-i18next\";");
        sb.AppendLine();
        foreach (var culture in cultures)
            sb.AppendLine($"import {Identifier(culture)} from \"./{culture}/{ns}.json\";");
        sb.AppendLine();
        sb.AppendLine("const resources = {");
        foreach (var culture in cultures)
            sb.AppendLine($"  \"{culture}\": {{ \"{ns}\": {Identifier(culture)} }},");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine(framework == WebFramework.React ? "i18n.use(initReactI18next).init({" : "i18n.init({");
        sb.AppendLine("  resources,");
        sb.AppendLine($"  lng: \"{source}\",");
        sb.AppendLine($"  fallbackLng: \"{source}\",");
        sb.AppendLine($"  ns: [\"{ns}\"],");
        sb.AppendLine($"  defaultNS: \"{ns}\",");
        // Keys are flat PascalCase, so i18next must not read '.' or ':' as structure.
        sb.AppendLine("  keySeparator: false,");
        sb.AppendLine("  nsSeparator: false,");
        sb.AppendLine("  interpolation: { escapeValue: false }");
        sb.AppendLine("});");
        sb.AppendLine();
        sb.AppendLine("export default i18n;");
        return sb.ToString();
    }

    /// <summary>Culture tag as a legal JS identifier: "pt-BR" becomes ptBR.</summary>
    private static string Identifier(string culture)
    {
        var cleaned = new string(culture.Where(char.IsLetterOrDigit).ToArray());
        if (cleaned.Length == 0) return "bundle";
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    private static bool IsTestFile(string relativePath)
    {
        var comparable = relativePath.Replace('\\', '/');
        return comparable.Contains(".test.", StringComparison.OrdinalIgnoreCase)
               || comparable.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
               || comparable.Contains(".stories.", StringComparison.OrdinalIgnoreCase)
               || comparable.Contains("__tests__/", StringComparison.OrdinalIgnoreCase)
               || comparable.Contains("__mocks__/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Root manifest, or the first one a directory down, which is where a monorepo keeps its apps.
    /// Deeper than that is not worth a full tree walk on a stack whose node_modules dwarfs the source.
    /// </summary>
    private static string? FindPackageJson(string projectRoot)
    {
        var root = Path.Combine(projectRoot, "package.json");
        if (File.Exists(root)) return root;

        try
        {
            return Directory
                .EnumerateDirectories(projectRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.') &&
                            !Path.GetFileName(d).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "package.json"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static WebFramework ReadFramework(string packageJsonPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(packageJsonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WebFramework.Unknown;
        }

        // Matching on the quoted key skips the many packages whose names merely contain "react".
        if (text.Contains("\"react\":", StringComparison.Ordinal)) return WebFramework.React;
        if (text.Contains("\"vue\":", StringComparison.Ordinal)) return WebFramework.Vue;
        if (text.Contains("\"@angular/core\":", StringComparison.Ordinal)) return WebFramework.Angular;
        if (text.Contains("\"svelte\":", StringComparison.Ordinal)) return WebFramework.Svelte;
        return WebFramework.Unknown;
    }

    private static bool HasFile(string projectRoot, string pattern)
    {
        try
        {
            return DirectoryWalk.Files(projectRoot, pattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
