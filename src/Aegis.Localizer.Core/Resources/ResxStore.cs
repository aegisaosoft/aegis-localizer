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
using System.Xml;
using System.Xml.Linq;

namespace Aegis.Localizer.Resources;

/// <summary>.NET resource files: AppResources.resx for the source language, AppResources.ru.resx for the rest.</summary>
public sealed class ResxStore : IResourceStore
{
    public ResourceFormat Format => ResourceFormat.Resx;

    public string ResolvePath(ResourceLocation location) => Path.Combine(
        location.Directory,
        location.Culture is null
            ? $"{location.BaseName}.resx"
            : $"{location.BaseName}.{location.Culture}.resx");

    public IReadOnlyDictionary<string, string> Read(ResourceLocation location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = ResolvePath(location);
        if (!File.Exists(path)) return result;

        try
        {
            var root = XDocument.Load(path).Root;
            if (root is null) return result;

            foreach (var data in root.Elements("data"))
            {
                var name = data.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                result[name] = data.Element("value")?.Value ?? string.Empty;
            }
        }
        catch (XmlException)
        {
            // A broken resource file is rebuilt rather than aborting the run.
        }

        return result;
    }

    public ResourceWriteResult Write(
        ResourceLocation location,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? comments = null)
    {
        var path = ResolvePath(location);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A file that exists but will not parse is refused, not replaced. It is usually a merge
        // conflict or a truncated earlier write, and quietly rebuilding it from this run's values
        // would delete every translation in it - including both sides of the conflict - with no
        // backup and no warning.
        var doc = File.Exists(path)
            ? Load(path)
            : XDocument.Parse(Skeleton);

        var root = doc.Root ?? XDocument.Parse(Skeleton).Root!;
        var existing = root.Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(e => e.Attribute("name")!.Value, e => e, StringComparer.Ordinal);

        var added = 0;
        var updated = 0;

        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string? comment = null;
            comments?.TryGetValue(key, out comment);

            if (existing.TryGetValue(key, out var node))
            {
                var valueNode = node.Element("value");
                if (valueNode is null)
                {
                    node.Add(new XElement("value", value));
                    updated++;
                }
                else if (valueNode.Value != value)
                {
                    valueNode.Value = value;
                    updated++;
                }
            }
            else
            {
                var data = new XElement("data",
                    new XAttribute("name", key),
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    new XElement("value", value));

                if (!string.IsNullOrWhiteSpace(comment))
                    data.Add(new XElement("comment", comment));

                root.Add(data);
                added++;
            }
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            NewLineChars = "\r\n"
        };

        using (var writer = XmlWriter.Create(path, settings))
            doc.Save(writer);

        return new ResourceWriteResult(path, added, updated);
    }

    private static XDocument Load(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                $"{path} is not valid XML ({ex.Message}). Fix or remove it and run again - " +
                "overwriting it would discard the translations it holds.", ex);
        }
    }

    private const string Skeleton =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <resheader name="version">
            <value>2.0</value>
          </resheader>
          <resheader name="reader">
            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
          <resheader name="writer">
            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
        </root>
        """;
}
