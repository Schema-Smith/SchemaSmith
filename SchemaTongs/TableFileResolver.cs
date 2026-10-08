// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs;

/// <summary>
/// Where an extracted table should be written, and whether it is an ungated emit. <see cref="ExistingSchema"/> is the
/// <c>Schema</c> the matched file declares, as written, so a refresh can keep the file's form.
/// </summary>
public sealed record TableResolution(string WritePath, bool UngatedEmit, string ExistingSchema = "");

/// <summary>
/// Resolves the write target for an extracted table by CONTENT identity (Schema + Name) against the
/// existing files in a Tables/ folder, rather than by computed filename. When a logical table has a
/// variant SET on disk, the extracted shape is attributed to its active variant (gate evaluated
/// against the source) — refreshing that variant's file — or written to the bare canonical name as an
/// ungated emit when no single variant is active, so validation (SS-DUP-001) surfaces the drift.
/// The shared <see cref="ExtractionFileIndex"/> stays filename-keyed because it also indexes scripts.
/// </summary>
public sealed class TableFileResolver
{
    private readonly string _tablesDir;
    private readonly bool _isSchemaTemplate;
    private readonly Func<string, bool> _isVariantActive;
    private readonly Platform _platform;
    private readonly Dictionary<(string Schema, string Name), List<TableFileEntry>> _byIdentity = new(IdentityComparer.Instance);

    private sealed record TableFileEntry(string Path, string Gate, string VariantName, string DeclaredSchema);

    public TableFileResolver(string tablesDir, Platform platform, bool isSchemaTemplate, Func<string, bool> isVariantActive)
    {
        _tablesDir = tablesDir;
        _isSchemaTemplate = isSchemaTemplate;
        _isVariantActive = isVariantActive;
        _platform = platform;

        var directory = DirectoryWrapper.GetFromFactory();
        if (!directory.Exists(tablesDir)) return;

        foreach (var path in directory.GetFiles(tablesDir, "*.json", SearchOption.AllDirectories))
        {
            Table table;
            try { table = JsonHelper.TableLoad(path, platform); }
            catch { continue; } // unreadable/invalid JSON is the loader/validator's concern, not ours
            if (table == null || string.IsNullOrWhiteSpace(table.Name)) continue;

            // Schema-template files are schema-scrubbed on extraction, so identity is name-only there.
            // Identifiers load quoted ([dbo], `Widget`); normalize so they match the unquoted query.
            var declaredSchema = (table as IDeliverableTable)?.Schema ?? "";
            var key = (_isSchemaTemplate ? "" : IdentitySchema(declaredSchema), TableFileName.NormalizeIdentifier(table.Name));
            if (!_byIdentity.TryGetValue(key, out var entries))
                _byIdentity[key] = entries = new List<TableFileEntry>();
            entries.Add(new TableFileEntry(path, table.ShouldApplyExpression ?? "", table.VariantName ?? "", declaredSchema));
        }
    }

    public TableResolution Resolve(string schema, string name)
    {
        // A bare (schema-less) canonical name is produced whenever the content carries no schema —
        // schema-template packages (scrubbed) OR a table whose schema is the platform default and is
        // therefore omitted from content (PostgreSQL public, MySQL no-schema). This mirrors the
        // SS-FILE-NAME-003 check, which derives its canonical name with the same empty-schema test,
        // so extraction output and validation agree by construction.
        var schemaLess = _isSchemaTemplate || string.IsNullOrEmpty(schema);
        var key = (_isSchemaTemplate ? "" : IdentitySchema(schema), TableFileName.NormalizeIdentifier(name));
        var matches = _byIdentity.TryGetValue(key, out var entries) ? entries : new List<TableFileEntry>();

        if (matches.Count == 0)
            return new TableResolution(Path.Join(_tablesDir, TableFileName.Canonical(schema, name, "", schemaLess)), UngatedEmit: false);

        if (matches.Count == 1)
            return new TableResolution(matches[0].Path, UngatedEmit: false, matches[0].DeclaredSchema);

        // Variant set: attribute the extracted shape to the active variant, or emit ungated.
        var decision = VariantAttribution.Decide(matches, e => e.Gate, _isVariantActive);
        return decision.Action == VariantAction.RefreshActive
            ? new TableResolution(matches[decision.ActiveIndex].Path, UngatedEmit: false, matches[decision.ActiveIndex].DeclaredSchema)
            : new TableResolution(Path.Join(_tablesDir, TableFileName.Canonical(schema, name, "", schemaLess)), UngatedEmit: true);
    }

    // PostgreSQL extraction omits the default schema from content, while a hand-written or demo package often
    // declares it. Both name the same table; keyed apart, a re-extract wrote a duplicate beside the original.
    private string IdentitySchema(string schema)
    {
        var normalized = TableFileName.NormalizeIdentifier(schema);
        return _platform.GetBasePlatform() == Platform.PostgreSQL
               && string.Equals(normalized, _platform.GetDefaultSchema(), StringComparison.Ordinal)
            ? ""
            : normalized;
    }

    private sealed class IdentityComparer : IEqualityComparer<(string Schema, string Name)>
    {
        public static readonly IdentityComparer Instance = new();

        public bool Equals((string Schema, string Name) a, (string Schema, string Name) b) =>
            string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Name) k) =>
            HashCode.Combine(k.Schema.ToLowerInvariant(), k.Name.ToLowerInvariant());
    }
}
