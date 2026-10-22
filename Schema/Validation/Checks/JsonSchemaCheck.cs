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
/// since <c>--WriteSchemasOnly</c> was last run — <c>SS-STALE-001</c> — but structural validation
/// still RUNS for that type, against the merged schema. A stale file is out of date, not
/// meaningless: the governance authored into it is still what the author asked for, so skipping the
/// type would quietly stop enforcing it while reporting only the staleness.
/// <para>
/// A committed schema that cannot be PARSED is different, and is reported as <c>SS-STALE-002</c>:
/// there is no fragment to recover from an unreadable file, so that type falls back to the freshly
/// generated schema and whatever governance the file carried is not enforced on that run — which is
/// why the finding says so outright rather than only naming the file.
/// </para>
/// <para>
/// When no committed schema is present at all (missing directory or missing file), there is nothing
/// to compare — no staleness finding — and structural validation runs directly against the freshly
/// generated schema.
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
    private const string NewerCommittedCode = "SS-STALE-003";
    private const string StaleCategory = "Staleness";
    private const string JsonCode = "SS-JSON-001";
    private const string JsonCategory = "JsonSchema";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        // The schemas are per platform, so without one there is nothing to compare against.
        if (ctx.Platform == Platform.Unknown) return [];
        var dir = ProductDirectoryWrapper.GetFromFactory();
        var file = ProductFileWrapper.GetFromFactory();
        var schemaDir = Path.Combine(ctx.PackagePath, ".json-schemas");

        var findings = new List<Finding>();
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
                var unknown = new List<string>();
                if (Describes(committed, merged, "", unknown) && unknown.Count > 0)
                    findings.Add(new Finding(Severity.Warning, NewerCommittedCode, StaleCategory, schemaPath,
                        $"Committed .json-schemas were written by a newer SchemaSmith: they describe {string.Join(", ", unknown)}, " +
                        "which this version does not know. Do not regenerate with this version -- that would remove them; " +
                        "upgrade to match. Structural validation used this version's model, so a file that uses them is " +
                        "reported, as this version's deploy would refuse it."));
                else
                    findings.Add(new Finding(Severity.Error, StaleCode, StaleCategory, schemaPath,
                        $"Committed .json-schemas are stale — regenerate via --WriteSchemasOnly. " +
                        $"Structural validation used the current model merged with this file's authored " +
                        $"custom-property governance."));

                // A STALE FILE IS STILL A VALID GOVERNANCE SOURCE, even though it is not a valid
                // structural one. `merged` is the CURRENT model carrying the Extensions fragment
                // recovered from the committed file, so validating against it enforces the user's own
                // required/enum rules while judging structure by the model that actually ships.
                //
                // This type used to be skipped entirely, to avoid misleading findings from a stale
                // schema. That reasoning does not apply to `merged`: the structure here is the current
                // model, not the stale file. The cost is that a package authored against the OLD model
                // can now surface findings that were previously suppressed -- which is the honest
                // report, since those files really are invalid against what will be deployed.
                schemaByType[typeName] = LoadNJsonSchema(merged);
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
            var typeName = MapFileToType(ctx.PackagePath, jsonFile);
            if (typeName == null) continue;
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
    //
    // THE MESSAGE NAMES THE GOVERNANCE CONSEQUENCE, not just the malformed file. A freshly
    // generated schema carries no hand-authored Extensions fragment, so falling back to one
    // silently stops enforcing any required/enum rule the user wrote onto a custom property —
    // and the failure is DIRECTIONAL: validation still passes, so the user's own governance rule
    // reads as satisfied when it was never evaluated. "Your schema is malformed" is not the
    // consequence a reader would act on; "your governance did not run" is. Stated
    // unconditionally because an unparseable file cannot be inspected for whether it carried a
    // fragment — the sentence is true either way, and a false reassurance would be worse than a
    // redundant warning.
    private static JObject ReadCommittedSchema(IFile file, string schemaPath, List<Finding> findings)
    {
        if (!file.Exists(schemaPath)) return null;

        try
        {
            return JsonText.ParseObject(file.ReadAllText(schemaPath));
        }
        catch
        {
            findings.Add(new Finding(Severity.Error, MalformedCommittedCode, StaleCategory, schemaPath,
                $"Committed .json-schemas file is malformed — validated against a freshly generated schema instead, " +
                $"so any custom-property governance authored in this file (Extensions required/enum rules) was NOT applied this run. " +
                $"Regenerate via --WriteSchemasOnly."));
            return null;
        }
    }

    private static JsonSchema LoadNJsonSchema(JObject committed) =>
        JsonSchema.FromJsonAsync(WithEnforceableRequiredUnless(committed).ToString()).GetAwaiter().GetResult();

    private const string RequiredUnlessMarker = "x-requiredUnless";

    // The generator states "P is required unless U is true" as allOf/if/else, which editors understand and NJsonSchema
    // does not evaluate -- so the rule was never applied. Each such entry is replaced, in this in-memory copy only, by
    // the same rule as anyOf (P present, or U true), marked so a failure is reported as the one missing property.
    private static JObject WithEnforceableRequiredUnless(JObject schema)
    {
        var copy = (JObject)schema.DeepClone();
        foreach (var allOf in copy.Descendants().OfType<JProperty>().Where(p => p.Name == "allOf").Select(p => p.Value).OfType<JArray>().ToList())
            for (var i = 0; i < allOf.Count; i++)
            {
                if (allOf[i] is not JObject entry || entry["else"]?["required"] is not JArray { Count: 1 } elseRequired
                    || entry["if"]?["required"] is not JArray { Count: 1 } ifRequired) continue;
                var property = (string)elseRequired[0];
                var unless = (string)ifRequired[0];
                if (entry["if"]?["properties"]?[unless]?["const"] is not JValue { Type: JTokenType.Boolean } flag || !(bool)flag) continue;
                allOf[i] = new JObject
                {
                    [RequiredUnlessMarker] = new JArray(property, unless),
                    ["anyOf"] = new JArray(
                        new JObject { ["required"] = new JArray(property) },
                        new JObject { ["properties"] = new JObject { [unless] = new JObject { ["enum"] = new JArray(true) } },
                                      ["required"] = new JArray(unless) })
                };
            }
        return copy;
    }

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
            if (error.Kind == ValidationErrorKind.NotAnyOf && RequiredUnless(error) != null)
            {
                yield return error;
            }
            else if (error is ChildSchemaValidationError child)
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

    private static string[] RequiredUnless(ValidationError error) =>
        error.Schema?.ExtensionData != null && error.Schema.ExtensionData.TryGetValue(RequiredUnlessMarker, out var names)
            ? (names as IEnumerable<object>)?.Select(n => n?.ToString()).ToArray()
            : null;

    private static string RenderMessage(ValidationError error)
    {
        if (RequiredUnless(error) is [var property, var unless])
            return $"missing required property '{property}' at {error.Path} (required unless {unless} is true)";

        return error.Kind switch
        {
            ValidationErrorKind.NoAdditionalPropertiesAllowed => $"unexpected property '{error.Property}' at {error.Path}",
            ValidationErrorKind.PropertyRequired => $"missing required property '{error.Property}' at {error.Path}",
            ValidationErrorKind.NotInEnumeration => $"value for '{error.Property}' not in allowed enumeration at {error.Path}",
            _ => $"{error.Kind} on property '{error.Property}' at {error.Path}"
        };
    }

    // Whether `committed` holds everything `current` does, collecting what it holds beyond that (#560): a newer
    // SchemaSmith's file, rather than an out-of-date one. A newer release adds properties, adds values to an enum-like
    // pattern, adds members to a oneOf, and rewords descriptions; none of that is a difference. A required list moves
    // both ways between releases (2.8 made the MySQL partition Expression optional), so it decides nothing.
    private static bool Describes(JToken committed, JToken current, string path, List<string> unknown)
    {
        switch (current)
        {
            case JObject currentObject when committed is JObject committedObject:
                foreach (var property in currentObject.Properties())
                {
                    if (property.Name is "description" or "required") continue;
                    if (committedObject[property.Name] is not { } inCommitted) return false;
                    if (property.Name == "pattern" && property.Value.Type == JTokenType.String && inCommitted.Type == JTokenType.String)
                    {
                        if (!PatternCovers((string)inCommitted, (string)property.Value, path, unknown)) return false;
                        continue;
                    }
                    if (!Describes(inCommitted, property.Value, Join(path, property.Name), unknown)) return false;
                }
                foreach (var property in committedObject.Properties().Where(p => p.Name is not ("description" or "required") && currentObject[p.Name] == null))
                    unknown.Add(Join(path, property.Name));
                return true;
            case JArray currentArray when committed is JArray committedArray:
                // Each element this version generates must be held by a distinct element of the committed array.
                var matched = new HashSet<int>();
                foreach (var element in currentArray)
                {
                    var match = Enumerable.Range(0, committedArray.Count)
                        .FirstOrDefault(k => !matched.Contains(k) && Describes(committedArray[k], element, path, []), -1);
                    if (match < 0) return false;
                    matched.Add(match);
                    Describes(committedArray[match], element, path, unknown);
                }
                unknown.AddRange(Enumerable.Range(0, committedArray.Count).Where(k => !matched.Contains(k))
                    .Select(k => $"{path} {committedArray[k].ToString(Newtonsoft.Json.Formatting.None)}"));
                return true;
            default:
                return JToken.DeepEquals(committed, current);
        }
    }

    // An enum-like value list is generated as ^(?:A|B|C)$, case-insensitive ones with [Aa] classes. The committed pattern
    // covers this version's when it accepts every alternative this one does.
    private static bool PatternCovers(string committed, string current, string path, List<string> unknown)
    {
        if (committed == current) return true;
        var committedAlternatives = Alternatives(committed);
        var currentAlternatives = Alternatives(current);
        if (committedAlternatives == null || currentAlternatives == null || !currentAlternatives.IsSubsetOf(committedAlternatives)) return false;
        unknown.AddRange(committedAlternatives.Except(currentAlternatives).Select(a => $"{path} '{a}'"));
        return true;
    }

    private static HashSet<string> Alternatives(string pattern)
    {
        if (!pattern.StartsWith("^(?:") || !pattern.EndsWith(")$")) return null;
        var body = pattern[4..^2];
        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        var start = 0;
        for (var k = 0; k <= body.Length; k++)
        {
            if (k < body.Length && body[k] == '(') depth++;
            else if (k < body.Length && body[k] == ')') depth--;
            else if (k == body.Length || (body[k] == '|' && depth == 0))
            {
                alternatives.Add(CaseFoldedAlternative.Replace(body[start..k], m =>
                    char.ToLowerInvariant(m.Groups[1].Value[0]) == char.ToLowerInvariant(m.Groups[2].Value[0]) ? m.Groups[1].Value : m.Value).ToLowerInvariant());
                start = k + 1;
            }
        }
        return alternatives;
    }

    private static readonly System.Text.RegularExpressions.Regex CaseFoldedAlternative = new(@"\[([A-Za-z])([A-Za-z])\]");

    // The schema keywords between property names carry no meaning for a reader: Columns.items.AnsiPadding, not
    // properties.Columns.items.properties.AnsiPadding.
    private static string Join(string path, string name) =>
        name == "properties" ? path : string.IsNullOrEmpty(path) ? name : $"{path}.{name}";

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
    // The NEAREST component folder between the package root and the file decides, so a subfolder that organises tables
    // (Tables/Core/product.json) is still read as tables (#563). Folders above the package root never count.
    private static readonly (string Folder, string Type)[] ComponentFolders =
    [
        ("Tables", "tables"), ("Indexed Views", "indexedviews"), ("Materialized Views", "materializedviews"),
        ("Events", "events"), ("Domain Types", "domaintypes"), ("Enum Types", "enumtypes"), ("Sequences", "sequences"),
    ];

    internal static string MapFileToType(string packagePath, string jsonFilePath)
    {
        var folders = Path.GetRelativePath(packagePath, Path.GetDirectoryName(jsonFilePath) ?? "")
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var folder in folders.Reverse())
        {
            var match = ComponentFolders.FirstOrDefault(c => c.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (match.Type != null) return match.Type;
        }

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
        if (objectPart == "sequences" && platform == Platform.MariaDb) return typeof(MariaDbSequence);

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
