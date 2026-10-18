// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Validation.Checks;

/// <summary>
/// Leans authors toward the canonical declared-object file naming convention
/// <c>&lt;schema&gt;.&lt;name&gt;[.&lt;VariantName&gt;].json</c> (schema segment omitted when the
/// content carries none — schema templates, MySQL/MariaDB, and events). Identity lives in the file
/// content, so a non-canonical name never breaks a deploy — it only earns a warning
/// (<c>SS-FILE-NAME-003</c>) so the filename stays an honest, sortable pointer to the object it holds.
/// <para>Covers <c>Tables/</c> and every modeled-object folder the loader reads on the package's engine.
/// The modeled folders matter beyond tidiness: tooling resolves an object by its derived file name, so a
/// file that drifted from its content let a second same-named object be written beside it.</para>
/// </summary>
public sealed class TableFileNameCheck : ISchemaCheck
{
    private const string Code = "SS-FILE-NAME-003";
    private const string Category = "FileName";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var findings = new List<Finding>();
        var dir = ProductDirectoryWrapper.GetFromFactory();
        if (!dir.Exists(ctx.PackagePath)) return findings;
        var schemaTemplateByFolder = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in dir.GetFiles(ctx.PackagePath, "*.json", SearchOption.AllDirectories))
        {
            var inSchemaTemplate = IsInSchemaTemplate(path, ctx.PackagePath, schemaTemplateByFolder);
            if (IsUnderModeledFolder(path, ctx.Platform))
            {
                findings.AddRange(CheckModeledObject(path, inSchemaTemplate));
                continue;
            }
            if (!IsUnderFolder(path, "Tables")) continue;

            Table table;
            try { table = JsonHelper.TableLoad(path, ctx.Platform); }
            catch { continue; } // malformed content is another check's concern
            if (table == null || string.IsNullOrWhiteSpace(table.Name)) continue;

            var schema = (table as IDeliverableTable)?.Schema ?? "";
            var canonical = TableFileName.Canonical(schema, table.Name, table.VariantName ?? "", schemaLess: string.IsNullOrEmpty(schema), inSchemaTemplate);
            var actual = Path.GetFileName(path);
            if (!string.Equals(actual, canonical, StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding(Severity.Warning, Code, Category, path,
                    $"Table file '{actual}' does not match its canonical name '{canonical}' (from Schema/Name/VariantName). Identity is content, so this is a naming lean, not an error — rename to keep the file a reliable pointer to its table."));
        }

        return findings;
    }

    // Folder -> the base engine whose loader reads *.json from it (Template.Load*). Keyed on the base
    // platform so MariaDB shares MySQL's Events folder, exactly as LoadEvents does.
    private static readonly (string Folder, Platform Platform)[] ModeledFolders =
    [
        ("Enum Types", Platform.PostgreSQL),
        ("Domain Types", Platform.PostgreSQL),
        ("Sequences", Platform.PostgreSQL),
        ("Materialized Views", Platform.PostgreSQL),
        ("Indexed Views", Platform.SqlServer),
        ("Events", Platform.MySQL),
    ];

    // Read from the template's own Template.json rather than the loaded templates: a template that failed to load is
    // missing from those, and its file names are exactly what the author is about to rename.
    private static bool IsInSchemaTemplate(string path, string packagePath, Dictionary<string, bool> byFolder)
    {
        var segments = Path.GetRelativePath(packagePath, path).Split('\\', '/');
        if (segments.Length < 3 || !segments[0].Equals("Templates", StringComparison.Ordinal)) return false;
        if (byFolder.TryGetValue(segments[1], out var known)) return known;

        var templateFile = Path.Join(packagePath, "Templates", segments[1], "Template.json");
        var file = ProductFileWrapper.GetFromFactory();
        var isSchemaTemplate = false;
        try
        {
            isSchemaTemplate = file.Exists(templateFile)
                && !string.IsNullOrWhiteSpace(JsonText.ParseObject(file.ReadAllText(templateFile)).Value<string>("SchemaIdentificationScript"));
        }
        catch (JsonReaderException)
        {
            // An unreadable Template.json is the load check's finding; name files as a regular template would.
        }
        return byFolder[segments[1]] = isSchemaTemplate;
    }

    private static IEnumerable<Finding> CheckModeledObject(string path, bool inSchemaTemplate)
    {
        JObject content;
        try { content = JsonText.ParseObject(ProductFileWrapper.GetFromFactory().ReadAllText(path)); }
        catch (JsonReaderException) { yield break; } // malformed content is another check's concern

        var name = content.Value<string>("Name");
        if (string.IsNullOrWhiteSpace(name)) yield break;

        var schema = content.Value<string>("Schema") ?? "";
        var canonical = TableFileName.Canonical(schema, name, content.Value<string>("VariantName") ?? "",
            schemaLess: string.IsNullOrEmpty(TableFileName.NormalizeIdentifier(schema)), inSchemaTemplate);
        var actual = Path.GetFileName(path);
        if (!string.Equals(actual, canonical, StringComparison.OrdinalIgnoreCase))
            yield return new Finding(Severity.Warning, Code, Category, path,
                $"Object file '{actual}' does not match its canonical name '{canonical}' (from Schema/Name/VariantName). Identity is content, so this is a naming lean, not an error — rename to keep the file a reliable pointer to its object.");
    }

    private static bool IsUnderModeledFolder(string path, Platform platform) =>
        ModeledFolders.Any(m => m.Platform == platform.GetBasePlatform() && IsUnderFolder(path, m.Folder))
        // MariaDB alone of its family reads Sequences/*.json (Template.LoadSequences), so it is keyed on the exact platform.
        || (platform == Platform.MariaDb && IsUnderFolder(path, "Sequences"));

    private static bool IsUnderFolder(string path, string folder) =>
        path.Split('\\', '/').Any(segment => segment.Equals(folder, StringComparison.Ordinal));
}
