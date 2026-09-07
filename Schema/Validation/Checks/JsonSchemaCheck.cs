// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.Validation;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Validation.Checks;

/// <summary>
/// The final `--Validate` check: structural + custom-property governance over every package JSON
/// file, plus staleness detection for whichever <c>.json-schemas/{type}.{platform}.schema</c>
/// files happen to be committed. The domain model is the authority, not the committed artifact —
/// the committed files are a convenience for editor tooling and are optional here.
/// <para>
/// For each type, a fresh schema is regenerated in memory via
/// <see cref="SchemaGenerator.GenerateSchema(Type, Func{Type, Type})"/>. When a committed schema
/// exists and parses, it is compared against the fresh one (after re-merging the committed file's
/// hand-authored <c>Extensions</c> fragments back in via
/// <see cref="SchemaGenerator.MergeExtensionsDefinition"/>) with
/// <see cref="JToken.DeepEquals(JToken, JToken)"/>. A mismatch means the domain model moved on
/// since <c>--WriteSchemasOnly</c> was last run — <c>SS-STALE-001</c> — and structural validation
/// is skipped for that type: results against a schema that no longer matches the model would be
/// misleading. A committed schema that fails to parse is reported as <c>SS-STALE-002</c> and
/// treated the same as absent. When no committed schema is present (missing directory, missing
/// file, or unparseable file), there is nothing to compare — no staleness finding — and structural
/// validation runs directly against the freshly generated schema instead.
/// </para>
/// <para>
/// Structural validation: every package JSON file is validated against its resolved schema (the
/// committed one when present and current, the freshly generated one otherwise) via NJsonSchema.
/// <c>additionalProperties: false</c> catches misnamed/misplaced properties, <c>required</c>
/// catches missing ones, and any hand-authored governance on custom <c>Extensions</c> properties
/// (an authored <c>enum</c>/<c>required</c> fragment) is enforced automatically whenever the
/// committed file is what's used — a freshly generated schema has no such fragment, so it leaves
/// <c>Extensions</c> unconstrained (<see cref="Schema.Domain.DynamicBase.Extensions"/> maps to an
/// empty/permissive schema), never rejecting legitimate custom content.
/// </para>
/// The file/type/platform mapping uses the public
/// <see cref="Schema.Utility.RepositoryHelper.GetSchemaFileNames(Platform)"/> directly. The
/// domain-type resolution mirrors <c>RepositoryHelper</c>'s private <c>GetTypeForSchemaFile</c> /
/// <c>PlatformElementResolver</c> — duplicated here rather than exposed from <c>Schema/</c>,
/// which this check must not modify.
/// </summary>
public sealed class JsonSchemaCheck : ISchemaCheck
{
    private const string StaleCode = "SS-STALE-001";
    private const string MalformedCommittedCode = "SS-STALE-002";
    private const string StaleCategory = "Staleness";
    private const string JsonCode = "SS-JSON-001";
    private const string JsonCategory = "JsonSchema";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();
        var schemaDir = Path.Combine(ctx.PackagePath, ".json-schemas");

        var findings = new List<Finding>();
        var staleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaByType = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in RepositoryHelper.GetSchemaFileNames(ctx.Platform))
        {
            var typeName = fileName.Split('.')[0];
            var domainType = GetTypeForSchemaFile(fileName, ctx.Platform);
            var freshGenerated = SchemaGenerator.GenerateSchema(domainType, PlatformElementResolver(ctx.Platform), ctx.Platform);

            var schemaPath = Path.Combine(schemaDir, fileName);
            var committed = ReadCommittedSchema(file, schemaPath, findings);

            if (committed == null)
            {
                // Nothing usable is committed for this type — the domain model IS the authority,
                // so validate directly against the freshly generated schema. Nothing to compare,
                // so no staleness finding either.
                schemaByType[typeName] = LoadNJsonSchema(freshGenerated);
                continue;
            }

            var merged = SchemaGenerator.MergeExtensionsDefinition(freshGenerated, committed);

            if (!JToken.DeepEquals(merged, committed))
            {
                findings.Add(new Finding(Severity.Error, StaleCode, StaleCategory, schemaPath,
                    $"Committed .json-schemas are stale — regenerate via --WriteSchemasOnly."));
                staleTypes.Add(typeName); // short-circuit: structural validation skips this type entirely
                continue;
            }

            schemaByType[typeName] = LoadNJsonSchema(committed);
        }

        if (schemaByType.Count == 0) return findings;

        var jsonFiles = dir.GetFiles(ctx.PackagePath, "*.json", SearchOption.AllDirectories)
            .Where(f => !IsUnderJsonSchemasDir(f))
            .ToList();

        foreach (var jsonFile in jsonFiles)
        {
            var typeName = MapFileToType(jsonFile);
            if (typeName == null || staleTypes.Contains(typeName)) continue;
            if (!schemaByType.TryGetValue(typeName, out var schema)) continue;

            var text = SafeReadText(file, jsonFile);
            if (text == null) continue;

            foreach (var error in FlattenErrors(SafeValidate(schema, text)))
                findings.Add(new Finding(Severity.Error, JsonCode, JsonCategory, jsonFile,
                    $"{RenderMessage(error)}"));
        }

        return findings;
    }

    // Returns null (nothing usable committed) for both a missing file and a file that fails to
    // parse — the malformed case additionally records a finding, since an unparseable committed
    // schema is a broken artifact worth surfacing, not a silent skip.
    private static JObject ReadCommittedSchema(IFile file, string schemaPath, List<Finding> findings)
    {
        if (!file.Exists(schemaPath)) return null;

        try
        {
            return JObject.Parse(file.ReadAllText(schemaPath));
        }
        catch
        {
            findings.Add(new Finding(Severity.Error, MalformedCommittedCode, StaleCategory, schemaPath,
                $"Committed .json-schemas file is malformed — validated against a freshly generated schema instead. Regenerate via --WriteSchemasOnly."));
            return null;
        }
    }

    private static JsonSchema LoadNJsonSchema(JObject committed) =>
        JsonSchema.FromJsonAsync(committed.ToString()).GetAwaiter().GetResult();

    private static ICollection<ValidationError> SafeValidate(JsonSchema schema, string jsonText)
    {
        try
        {
            return schema.Validate(jsonText);
        }
        catch
        {
            // Malformed package JSON is TokenCheck/package-load's concern, not this one — skip
            // rather than crash the whole --Validate run over one bad file.
            return Array.Empty<ValidationError>();
        }
    }

    private static string SafeReadText(IFile file, string path)
    {
        try
        {
            return file.Exists(path) ? file.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    // NJsonSchema wraps every nested object/array violation in a ChildSchemaValidationError whose
    // Errors dictionary holds the real per-property leaf errors — Kind/Property/Path only carry
    // useful information at the leaf, so descend until one is reached.
    private static IEnumerable<ValidationError> FlattenErrors(IEnumerable<ValidationError> errors)
    {
        foreach (var error in errors)
        {
            if (error is ChildSchemaValidationError child)
            {
                foreach (var leaf in FlattenErrors(child.Errors.Values.SelectMany(v => v)))
                    yield return leaf;
            }
            else
            {
                yield return error;
            }
        }
    }

    private static string RenderMessage(ValidationError error) => error.Kind switch
    {
        ValidationErrorKind.NoAdditionalPropertiesAllowed => $"unexpected property '{error.Property}' at {error.Path}",
        ValidationErrorKind.PropertyRequired => $"missing required property '{error.Property}' at {error.Path}",
        ValidationErrorKind.NotInEnumeration => $"value for '{error.Property}' not in allowed enumeration at {error.Path}",
        _ => $"{error.Kind} on property '{error.Property}' at {error.Path}"
    };

    private static bool IsUnderJsonSchemasDir(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".json-schemas", StringComparison.OrdinalIgnoreCase));

    // Package JSON files map to a schema type by file name / containing folder, mirroring the
    // on-disk layout Template.Load itself reads (Schema/Domain/Template.cs: LoadTables reads
    // "Tables", LoadIndexedViews reads "Indexed Views", LoadMaterializedViews reads
    // "Materialized Views" — note the spaces, unlike the compact .schema file name segments).
    // Directory context takes precedence over file name: a file under a component folder is
    // classified by that folder even if it happens to be named Product.json/Template.json (e.g.
    // MySQL's schema-less <table>.json layout can produce Tables/Product.json for a table
    // literally named "Product") — otherwise it would be misread as the manifest.
    private static string MapFileToType(string jsonFilePath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(jsonFilePath) ?? "");
        if (parent.Equals("Tables", StringComparison.OrdinalIgnoreCase)) return "tables";
        if (parent.Equals("Indexed Views", StringComparison.OrdinalIgnoreCase)) return "indexedviews";
        if (parent.Equals("Materialized Views", StringComparison.OrdinalIgnoreCase)) return "materializedviews";
        if (parent.Equals("Events", StringComparison.OrdinalIgnoreCase)) return "events";
        if (parent.Equals("Domain Types", StringComparison.OrdinalIgnoreCase)) return "domaintypes";
        if (parent.Equals("Enum Types", StringComparison.OrdinalIgnoreCase)) return "enumtypes";
        if (parent.Equals("Sequences", StringComparison.OrdinalIgnoreCase)) return "sequences";

        var fileName = Path.GetFileName(jsonFilePath);
        if (fileName.Equals("Product.json", StringComparison.OrdinalIgnoreCase)) return "products";
        if (fileName.Equals("Template.json", StringComparison.OrdinalIgnoreCase)) return "templates";
        return null;
    }

    // ---- Everything below mirrors Schema/Utility/RepositoryHelper.cs's private mapping.
    // Duplicated rather than exposed: SchemaQuench must not reference Schema's private members,
    // and this check must not modify Schema/. Keep these in lockstep with RepositoryHelper if the
    // domain model's platform-subclass or schema-file-naming scheme ever changes. ----

    // internal, not private: SchemaFileMappingParityTests pins this in lockstep with RepositoryHelper's
    // twin, because the two drifting silently is exactly how events/enumtypes/sequences shipped a broken
    // --Validate on this branch -- the full gate was the only thing that caught it.
    internal static Type GetTypeForSchemaFile(string fileName, Platform platform)
    {
        var objectPart = fileName.Split('.')[0];

        // MariaDB before the base-platform fold, exactly as RepositoryHelper does it: GetBasePlatform()
        // maps MariaDb to MySQL, which is right for every shared shape but would hand MariaDB the MySQL
        // table schema and lose the MariaDB-only properties MariaDbTable exists to carry. Without this the
        // freshly generated schema omits IsSystemVersioned/Periods/Encrypted/PageCompressed while the
        // committed file (generated by RepositoryHelper from MariaDbTable) has them, so DeepEquals never
        // matches and every MariaDB package reports a false SS-STALE-001 that no regeneration can clear.
        if (objectPart == "tables" && platform == Platform.MariaDb) return typeof(MariaDbTable);

        return (objectPart, platform.GetBasePlatform()) switch
        {
            ("products", _) => typeof(Product),
            ("templates", Platform.SqlServer) => typeof(SqlServerTemplate),
            ("templates", Platform.PostgreSQL) => typeof(PostgreSqlTemplate),
            ("templates", Platform.MySQL) => typeof(MySqlTemplate),
            ("tables", Platform.SqlServer) => typeof(SqlServerTable),
            ("tables", Platform.PostgreSQL) => typeof(PostgreSqlTable),
            ("tables", Platform.MySQL) => typeof(MySqlTable),
            ("indexedviews", Platform.SqlServer) => typeof(SqlServerIndexedView),
            ("materializedviews", Platform.PostgreSQL) => typeof(PostgreSqlMaterializedView),
            ("events", Platform.MySQL) => typeof(MySqlEvent),
            ("domaintypes", Platform.PostgreSQL) => typeof(PostgreSqlDomainType),
            ("enumtypes", Platform.PostgreSQL) => typeof(PostgreSqlEnumType),
            ("sequences", Platform.PostgreSQL) => typeof(PostgreSqlSequence),
            _ => throw new ArgumentException($"Unknown schema file mapping: {fileName} for platform {platform}")
        };
    }

    private static Func<Type, Type> PlatformElementResolver(Platform platform) => t =>
        t == typeof(Column) ? PlatformDeserializer.GetColumnType(platform)
        : t == typeof(Schema.Domain.Index) ? PlatformDeserializer.GetIndexType(platform)
        : t == typeof(ForeignKey) ? PlatformDeserializer.GetForeignKeyType(platform)
        : t == typeof(CheckConstraint) ? PlatformDeserializer.GetCheckConstraintType(platform)
        : t;
}
