// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;

namespace Schema.Utility;

public class SchemaFileResult
{
    public string FileName { get; set; }
    public bool WasCreated { get; set; }

    /// <summary>
    /// True when the committed schema could not be parsed, so it was regenerated from the model WITHOUT
    /// the hand-authored <c>Extensions</c> fragment it may have carried. The file is valid again; whatever
    /// custom-property governance was written into it is gone and has to be re-applied by hand.
    /// </summary>
    public bool AuthoredExtensionsLost { get; set; }

    /// <summary>
    /// True when an EXISTING file's content actually changed. This is the common outcome and the one the
    /// result could not previously express: the merged schema is written unconditionally, so a rewritten
    /// file used to return a result identical to nothing-happened, and every consumer's "up to date"
    /// message was unverifiable. A caller cannot recover this for itself without re-reading, re-generating
    /// and re-running the merge — reimplementing the method it just called — because this is the only place
    /// that holds both strings at once.
    /// <para>Distinct from <see cref="WasCreated"/>: created and updated are different things to report.</para>
    /// </summary>
    public bool WasUpdated { get; set; }

    /// <summary>Parser message for the unreadable file, so the warning can say WHY it could not be read.</summary>
    public string ParseError { get; set; }
}

/// <summary>
/// Helpers for initializing and updating schema package repositories.
/// Platform-aware: uses platform-appropriate validation scripts, template structures, and schema file names.
/// </summary>
public static class RepositoryHelper
{
    /// <summary>
    /// Initializes or updates a Product.json and adds missing schema files for the given platform.
    /// Schema files are only added if they don't already exist. Use WriteSchemaFiles for merge behavior.
    /// </summary>
    public static void UpdateOrInitRepository(
        string productPath, string productName, string templateName, string dbName, Platform platform,
        bool isSchemaTemplate = false)
    {
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();
        directory.CreateDirectory(Path.Combine(productPath, "Templates"));
        var productFile = Path.Combine(productPath, "Product.json");
        if (string.IsNullOrEmpty(productName)) productName = Path.GetFileName(productPath.TrimEnd(' ', '/', '\\'));
        if (string.IsNullOrEmpty(templateName)) templateName = dbName;

        var product = new Product
        {
            Name = productName,
            Platform = platform,
            ValidationScript = GetValidationScript(templateName, platform)
        };

        if (file.Exists(productFile)) product = JsonHelper.Load<Product>(productFile) ?? product;
        if (product.Platform == Platform.Unknown)
            product.Platform = platform;
        else if (product.Platform != platform)
            throw new Exception($"Platform mismatch: Product '{product.Name}' is configured for {product.Platform} but config specifies {platform}.");
        product.FilePath = productFile;
        if (!product.ScriptTokens.Any(t => t.Key.EqualsIgnoringCase($"{templateName}Db")))
            product.ScriptTokens.Add($"{templateName}Db", dbName);
        if (product.TemplateOrder.All(t => !t.EqualsIgnoringCase(templateName)))
            product.TemplateOrder.Add(templateName);

        // Schema-template entries must run AFTER any regular templates they depend on
        // (issue #258). Stable-partition the order: regular templates first, schema-
        // templates last, preserving relative order within each group. Detection reads
        // each Template.json's SchemaIdentificationScript (Template.IsSchemaTemplate);
        // the just-added entry's Template.json may not exist yet at this point on the
        // SchemaTongs call path, so the caller-supplied isSchemaTemplate wins for it.
        product.TemplateOrder = product.TemplateOrder
            .OrderBy(t => ClassifyTemplateEntry(productPath, t, templateName, isSchemaTemplate, file) ? 1 : 0)
            .ToList();

        JsonHelper.Write(productFile, product);
    }

    private static bool ClassifyTemplateEntry(string productPath, string templateNameToCheck,
        string justAddedName, bool justAddedIsSchemaTemplate, IFile file)
    {
        if (templateNameToCheck.EqualsIgnoringCase(justAddedName))
            return justAddedIsSchemaTemplate;

        var templateFile = Path.Combine(productPath, "Templates", templateNameToCheck, "Template.json");
        if (!file.Exists(templateFile)) return false; // missing Template.json — treat as regular (leaves position alone)
        try
        {
            var template = JsonHelper.Load<Template>(templateFile);
            return template?.IsSchemaTemplate ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds schema files that don't already exist. Does not merge or overwrite existing files.
    /// </summary>
    public static void AddMissingSchemaFiles(string productPath, Platform platform)
    {
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();
        var schemaPath = Path.Combine(productPath, ".json-schemas");
        directory.CreateDirectory(schemaPath);

        foreach (var fileName in GetSchemaFileNames(platform))
        {
            var schemaFile = Path.Combine(schemaPath, fileName);
            if (file.Exists(schemaFile)) continue;

            var generated = SchemaGenerator.GenerateSchema(GetTypeForSchemaFile(fileName, platform), PlatformElementResolver(platform), platform);
            file.WriteAllText(schemaFile, generated.ToString(Formatting.Indented));
        }
    }

    /// <summary>
    /// Writes or merges schema files for the given platform into the .json-schemas folder.
    /// </summary>
    /// <param name="warn">
    /// Receives a line per committed schema that could not be parsed and was therefore regenerated without
    /// its authored <c>Extensions</c> fragment. Omitting it does NOT discard the warning — it routes to the
    /// engine's own logger instead. There is deliberately no silent path: the string being dropped is the
    /// notice that authored governance was destroyed, and a default that makes losing it the quiet option
    /// is the same fail-open shape this warning exists to close.
    /// </param>
    public static void WriteSchemaFiles(string productPath, Platform platform, Action<string> warn = null)
    {
        WriteSchemaFilesWithResults(productPath, platform, warn);
    }

    /// <summary>
    /// Writes or merges schema files and returns detailed results for each file.
    /// </summary>
    public static List<SchemaFileResult> WriteSchemaFilesWithResults(string productPath, Platform platform,
        Action<string> warn = null)
    {
        var directory = DirectoryWrapper.GetFromFactory();
        var schemaPath = Path.Combine(productPath, ".json-schemas");
        directory.CreateDirectory(schemaPath);

        // A caller that passes no sink gets the engine's logger, never silence. Making the parameter
        // REQUIRED was the other candidate and would force each new caller to decide -- but it breaks every
        // existing call site to buy a decision, when the property that actually matters is that the warning
        // always lands somewhere. Revisit if a host appears that needs the warning in front of a user rather
        // than in a log; the flag on the result already carries it for anyone who wants to render it.
        warn ??= message => LogFactory.GetLogger(nameof(RepositoryHelper)).Warn(message);

        var schemaFileNames = GetSchemaFileNames(platform);
        var results = new List<SchemaFileResult>();
        foreach (var fileName in schemaFileNames)
            results.Add(WriteSchemaFileWithResult(schemaPath, fileName, platform));

        // Losing an authored fragment is a real loss, so it is reported per file rather than summarised.
        // The user has to re-author it, and cannot do that without knowing which file it was.
        foreach (var lost in results.Where(r => r.AuthoredExtensionsLost))
            warn?.Invoke($"'{lost.FileName}' could not be parsed ({lost.ParseError}) and was regenerated from the "
                         + "current model. Any hand-authored \"Extensions\" governance it carried (required "
                         + "properties, enum rules) was NOT preserved and must be re-applied.");
        return results;
    }

    /// <summary>
    /// Initializes or updates a Template.json and creates platform-appropriate script folders.
    /// </summary>
    public static string UpdateOrInitTemplate(string productPath, string templateName, string dbName, Platform platform)
        => UpdateOrInitTemplate(productPath, templateName, dbName, platform, sourceSchema: null, userSchemaIdentificationScript: null);

    /// <summary>
    /// Schema-template-aware overload (design §7.5). When <paramref name="sourceSchema"/> is non-empty,
    /// the generated <c>Template.json</c> stub includes <c>SchemaIdentificationScript</c>
    /// (user-supplied or a placeholder returning <paramref name="sourceSchema"/> as a single row),
    /// <c>CreateSchemaIfMissing: false</c>, and the schema-template default folder set
    /// (database-scoped object types — Schemas, DDLTriggers, FullTextCatalogs, FullTextStopLists —
    /// are omitted). When the file already exists, its content is preserved as-is on the
    /// schema-template path too: a SchemaTongs re-cast does not clobber the user's edits.
    /// </summary>
    public static string UpdateOrInitTemplate(
        string productPath,
        string templateName,
        string dbName,
        Platform platform,
        string sourceSchema,
        string userSchemaIdentificationScript)
    {
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();
        if (string.IsNullOrEmpty(templateName)) templateName = dbName;
        var templatePath = Path.Combine(productPath, "Templates", templateName);
        directory.CreateDirectory(templatePath);
        var templateFile = Path.Combine(templatePath, "Template.json");

        var isSchemaTemplate = !string.IsNullOrWhiteSpace(sourceSchema);

        var template = new Template
        {
            Name = templateName,
            DatabaseIdentificationScript = GetDatabaseIdentificationScript(templateName, platform)
        };

        if (isSchemaTemplate)
        {
            template.SchemaIdentificationScript = !string.IsNullOrWhiteSpace(userSchemaIdentificationScript)
                ? userSchemaIdentificationScript
                : GetSchemaIdentificationStub(sourceSchema, platform);
            // Defensive: schema-template mode requires false here, so pin it locally rather than
            // relying on the model default — a future model-default flip should not silently
            // re-enable CREATE SCHEMA on extracted templates.
            template.CreateSchemaIfMissing = false;
            // AllowParallel and ContinueOnSchemaFailure default to true — JsonHelper omits default
            // values, so they don't appear in the serialized output. Template.Load reads them back
            // as default-true. Design §7.5 specifies the desired values: AllowParallel: true,
            // ContinueOnSchemaFailure: true.
        }

        if (!file.Exists(templateFile))
        {
            AddDefaultScriptFolders(template, platform, isSchemaTemplate);
            JsonHelper.Write(templateFile, template);
        }
        else
        {
            template = JsonHelper.Load<Template>(templateFile) ?? template;

            var needsUpgrade = template.ScriptFolders.Any(f => f.ObjectType == ScriptObjectType.None
                && ScriptFolderTypeInference.InferFromFolderName(f.FolderPath) != ScriptObjectType.None);
            if (needsUpgrade)
            {
                foreach (var folder in template.ScriptFolders.Where(sf => sf.ObjectType == ScriptObjectType.None))
                    folder.ObjectType = ScriptFolderTypeInference.InferFromFolderName(folder.FolderPath);
                JsonHelper.Write(templateFile, template);
            }
        }

        foreach (var folder in template.ScriptFolders)
            directory.CreateDirectory(Path.Combine(templatePath, folder.FolderPath));
        return templatePath;
    }

    /// <summary>
    /// Builds the placeholder <c>SchemaIdentificationScript</c> for a freshly extracted
    /// schema template (design §7.5). The stub returns the source schema as a single row
    /// so the package quench-tests immediately without user editing.
    /// </summary>
    internal static string GetSchemaIdentificationStub(string sourceSchema, Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer =>
            "-- TODO: replace with a query returning the active iteration schemas.\n" +
            "-- Placeholder uses the seed schema as a single-row example.\n" +
            $"SELECT '{sourceSchema}' AS SchemaName",
        Platform.PostgreSQL =>
            "-- TODO: replace with a query returning the active iteration schemas.\n" +
            "-- Placeholder uses the seed schema as a single-row example.\n" +
            $"SELECT '{sourceSchema}' AS \"SchemaName\"",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform,
            $"Schema-template extraction is not supported on {platform}.")
    };

    private static SchemaFileResult WriteSchemaFileWithResult(string schemaPath, string fileName, Platform platform)
    {
        var file = FileWrapper.GetFromFactory();
        var schemaFile = Path.Combine(schemaPath, fileName);
        var generated = SchemaGenerator.GenerateSchema(GetTypeForSchemaFile(fileName, platform), PlatformElementResolver(platform), platform);

        if (!file.Exists(schemaFile))
        {
            file.WriteAllText(schemaFile, generated.ToString(Formatting.Indented));
            return new SchemaFileResult { FileName = fileName, WasCreated = true };
        }

        var existing = file.ReadAllText(schemaFile);

        // The existing file is read to carry its hand-authored "Extensions" fragment forward, which is worth
        // doing -- but an unreadable file used to take the whole command down with a raw JsonReaderException
        // at exit 3. That made the advice circular: --Validate's SS-STALE-002 finding tells the user to
        // regenerate via --WriteSchemasOnly, and regenerating is exactly what a malformed file prevented.
        // The only way out was deleting the file, which nothing told them to do.
        //
        // Regenerate instead, and say what was lost. A valid schema with no authored fragment is a state the
        // user can see and fix; a stack trace is not. NOT silent: dropping governance quietly would be worse
        // than the crash, because the package would look healthy while enforcing less than it used to.
        JObject existingObj;
        try
        {
            existingObj = JObject.Parse(existing);
        }
        catch (JsonException ex)
        {
            file.WriteAllText(schemaFile, generated.ToString(Formatting.Indented));
            return new SchemaFileResult
            {
                FileName = fileName, WasUpdated = true, AuthoredExtensionsLost = true, ParseError = ex.Message
            };
        }

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existingObj);
        var mergedText = merged.ToString(Formatting.Indented);

        // Compared BEFORE the write, which is the only moment both strings are in hand. The write itself is
        // deliberately left unconditional: skipping it when the content matches would stop --WriteSchemasOnly
        // touching mtimes on every run, which is appealing but is a behaviour change consumers may read, and
        // it is not needed to report the outcome honestly. Worth deciding on its own merits, not as a side
        // effect of a reporting fix.
        file.WriteAllText(schemaFile, mergedText);
        return new SchemaFileResult { FileName = fileName, WasUpdated = mergedText != existing };
    }

    /// <summary>
    /// Maps the base collection element types (Column/Index/ForeignKey/CheckConstraint) to their
    /// platform subclass so generated table element schemas include platform-specific properties
    /// (e.g. CheckExpression, GenerationExpression). Reuses the canonical platform→subclass mapping
    /// in <see cref="PlatformDeserializer"/> rather than duplicating it.
    /// </summary>
    // internal, like GetTypeForSchemaFile: the generated-schema tests have to drive the real resolver.
    // A stub one returns null element types and NullReferences inside the generator, which reads as a
    // product bug rather than a wrong test.
    internal static Func<Type, Type> PlatformElementResolver(Platform platform) => t =>
        t == typeof(Column) ? PlatformDeserializer.GetColumnType(platform)
        : t == typeof(Schema.Domain.Index) ? PlatformDeserializer.GetIndexType(platform)
        : t == typeof(ForeignKey) ? PlatformDeserializer.GetForeignKeyType(platform)
        : t == typeof(CheckConstraint) ? PlatformDeserializer.GetCheckConstraintType(platform)
        : t;

    // internal, not private: SchemaFileMappingParityTests compares this against JsonSchemaCheck's
    // duplicate TYPE-FOR-TYPE. Asserting only that the duplicate resolves *something* let the MariaDB
    // tables row drift silently -- it returned MySqlTable, threw nothing, and every MariaDB package
    // reported a false SS-STALE-001 because the committed schema was generated from MariaDbTable.
    internal static Type GetTypeForSchemaFile(string fileName, Platform platform)
    {
        var objectPart = fileName.Split('.')[0]; // "products", "templates", "tables", "indexedviews", "materializedviews"

        // MariaDB before the base-platform fold: GetBasePlatform() maps it to MySQL, which is right for
        // every shared shape but would hand MariaDB the MySQL table schema and lose the MariaDB-only
        // properties MariaDbTable exists to carry.
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

    public static string[] GetSchemaFileNames(Platform platform)
    {
        var platformName = platform.ToCanonicalString().ToLower();
        var files = new List<string>
        {
            $"products.{platformName}.schema",
            $"templates.{platformName}.schema",
            $"tables.{platformName}.schema"
        };
        if (platform == Platform.PostgreSQL)
            files.Add($"materializedviews.{platformName}.schema");
        if (platform == Platform.SqlServer)
            files.Add($"indexedviews.{platformName}.schema");
        // Scheduled events, MySQL and MariaDB. MySqlEvent carries no MariaDB-only properties, so unlike
        // tables this needs no pre-fold special case -- both engines get the same shape.
        if (platform.GetBasePlatform() == Platform.MySQL)
            files.Add($"events.{platformName}.schema");
        if (platform == Platform.PostgreSQL)
            files.Add($"domaintypes.{platformName}.schema");
        if (platform == Platform.PostgreSQL)
            files.Add($"enumtypes.{platformName}.schema");
        if (platform == Platform.PostgreSQL)
            files.Add($"sequences.{platformName}.schema");
        return files.ToArray();
    }

    /// <summary>
    /// Maps a package object-type folder to the schema kind that validates what is inside it.
    /// Returns null for a folder no generated schema covers, which is not an error: script folders
    /// and anything a user adds alongside them simply get no <c>$schema</c>.
    /// </summary>
    private static string SchemaKindForFolder(string folderName) => folderName switch
    {
        "Tables" => "tables",
        "Indexed Views" => "indexedviews",
        "Materialized Views" => "materializedviews",
        "Events" => "events",
        "Domain Types" => "domaintypes",
        "Enum Types" => "enumtypes",
        "Sequences" => "sequences",
        _ => null
    };

    /// <summary>
    /// The <c>$schema</c> value for one package JSON file: the relative path from that file to the
    /// package's generated schema for its kind.
    /// </summary>
    /// <remarks>
    /// Always forward slashes. <see cref="Path.GetRelativePath"/> emits the platform separator, and a
    /// Windows-authored package carrying <c>..\.json-schemas\tables.sqlserver.schema</c> resolves in
    /// no editor on any OS — including on Windows, where VS Code wants a URI-style path. Getting this
    /// wrong is silent: the key is present, looks right in a diff, and simply never validates.
    /// </remarks>
    public static string BuildSchemaRef(string productPath, string jsonFilePath, string schemaKind, Platform platform)
    {
        var schemaFile = Path.Combine(productPath, ".json-schemas",
            $"{schemaKind}.{platform.ToCanonicalString().ToLower()}.schema");
        var fromDirectory = Path.GetDirectoryName(Path.GetFullPath(jsonFilePath)) ?? productPath;
        return Path.GetRelativePath(fromDirectory, Path.GetFullPath(schemaFile)).Replace('\\', '/');
    }

    /// <summary>
    /// Writes a <c>$schema</c> reference into every JSON file in the package that a generated schema
    /// covers, so editors and third-party validators pick the schema up with no per-user setup.
    /// Idempotent: a file already carrying the right reference is left untouched.
    /// </summary>
    /// <remarks>
    /// No-ops when the package has no <c>.json-schemas</c> folder — a reference to a file that was
    /// never generated would validate nothing and show the user a broken path.
    /// <para>
    /// Edits the file as TEXT rather than deserializing and re-serializing. A typed round-trip would
    /// rewrite all 3,000-plus shipped package files through the current serializer, and every setting
    /// that has shifted since a file was authored (DefaultValueHandling dropping a now-default value,
    /// property order, an <c>Extensions</c> fragment's inner formatting) would land as an unreviewable
    /// diff wrapped around the one line being added.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Adds the <c>$schema</c> reference to ONE package file. Returns true if the file changed.
    /// </summary>
    /// <remarks>
    /// For the two package-level files, <c>Product.json</c> and <c>Template.json</c>, which are
    /// written during init -- before <c>.json-schemas</c> exists -- and are not rewritten by a later
    /// extraction. Neither carries variants, so stamping them does not touch the byte-for-byte
    /// promise that applies to object files.
    /// </remarks>
    public static bool StampSchemaRef(string productPath, string jsonFilePath, string schemaKind,
        Platform platform, Action<string> warn = null)
    {
        var file = FileWrapper.GetFromFactory();
        var schemaFile = Path.Combine(productPath, ".json-schemas",
            $"{schemaKind}.{platform.ToCanonicalString().ToLower()}.schema");
        if (!file.Exists(jsonFilePath) || !file.Exists(schemaFile)) return false;

        try
        {
            var original = file.ReadAllText(jsonFilePath);
            var updated = WithSchemaRef(original, BuildSchemaRef(productPath, jsonFilePath, schemaKind, platform));
            if (updated == null || updated == original) return false;
            file.WriteAllText(jsonFilePath, updated);
            return true;
        }
        catch (Exception ex)
        {
            (warn ?? (m => LogFactory.GetLogger(nameof(RepositoryHelper)).Warn(m)))(
                $"'{jsonFilePath}' could not be stamped with a $schema reference ({ex.Message}). "
                + "The file is unchanged; deploy behavior is unaffected.");
            return false;
        }
    }

    public static int StampSchemaRefs(string productPath, Platform platform, Action<string> warn = null)
    {
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();
        if (!directory.Exists(Path.Combine(productPath, ".json-schemas"))) return 0;
        warn ??= message => LogFactory.GetLogger(nameof(RepositoryHelper)).Warn(message);

        var stamped = 0;
        foreach (var (jsonFile, kind) in EnumerateSchemaCoveredFiles(productPath, directory))
        {
            // Per FILE, not per folder. A package can hold some of its schemas and not others, and a
            // reference to the one that is missing is worse than no reference: editors report it as a
            // broken schema rather than silently skipping, and nothing in the deploy path would ever
            // surface it.
            if (!file.Exists(Path.Combine(productPath, ".json-schemas",
                    $"{kind}.{platform.ToCanonicalString().ToLower()}.schema")))
                continue;

            var reference = BuildSchemaRef(productPath, jsonFile, kind, platform);
            try
            {
                var original = file.ReadAllText(jsonFile);
                var updated = WithSchemaRef(original, reference);
                if (updated == null || updated == original) continue;
                file.WriteAllText(jsonFile, updated);
                stamped++;
            }
            catch (Exception ex)
            {
                // One unparseable file must not abort the package: the rest are still correct, and the
                // name is what the user needs in order to go look at it.
                warn($"'{jsonFile}' could not be stamped with a $schema reference ({ex.Message}). "
                     + "The file is unchanged; deploy behavior is unaffected.");
            }
        }
        return stamped;
    }

    private static IEnumerable<(string Path, string Kind)> EnumerateSchemaCoveredFiles(string productPath, IDirectory directory)
    {
        var productFile = Path.Combine(productPath, "Product.json");
        if (FileWrapper.GetFromFactory().Exists(productFile))
            yield return (productFile, "products");

        var templatesRoot = Path.Combine(productPath, "Templates");
        if (!directory.Exists(templatesRoot)) yield break;

        foreach (var templateDirectory in directory.GetDirectories(templatesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var templateFile = Path.Combine(templateDirectory, "Template.json");
            if (FileWrapper.GetFromFactory().Exists(templateFile))
                yield return (templateFile, "templates");

            foreach (var objectDirectory in directory.GetDirectories(templateDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var kind = SchemaKindForFolder(Path.GetFileName(objectDirectory));
                if (kind == null) continue;

                // Recursive: tables are commonly foldered (Tables/Core, Tables/Reference) and the
                // schema still covers them at any depth.
                foreach (var jsonFile in directory.GetFiles(objectDirectory, "*.json", SearchOption.AllDirectories))
                    yield return (jsonFile, kind);
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="json"/> with <c>$schema</c> present as its first property, or null if
    /// the text is not a JSON object this can safely edit.
    /// </summary>
    private static string WithSchemaRef(string json, string reference)
    {
        // Parse purely as a guard. The result is thrown away and the edit is textual, but a file that
        // does not parse is a file this must not rewrite.
        if (JToken.Parse(json) is not JObject parsed) return null;

        var encoded = JsonConvert.ToString(reference);
        if (parsed["$schema"] is not null)
        {
            var current = parsed["$schema"].Type == JTokenType.String ? parsed["$schema"].Value<string>() : null;
            if (current == reference) return json;
            var existing = new Regex("\"\\$schema\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|null)");
            return existing.IsMatch(json) ? existing.Replace(json, $"\"$schema\": {encoded}", 1) : null;
        }

        // Insert as the first property, matching the indentation the next line already uses so the
        // result is byte-identical to the original but for the added line.
        var opening = new Regex("^(\\s*\\{)(\\r?\\n)([ \\t]*)");
        var match = opening.Match(json);
        if (!match.Success) return null;
        var indent = match.Groups[3].Value;
        var newline = match.Groups[2].Value;
        return opening.Replace(json,
            $"{match.Groups[1].Value}{newline}{indent}\"$schema\": {encoded},{newline}{indent}", 1);
    }

    internal static string GetValidationScript(string templateName, Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"SELECT CAST(CASE WHEN EXISTS(SELECT * FROM master.sys.databases WHERE [Name] = '{{{{{templateName}Db}}}}') THEN 1 ELSE 0 END AS BIT)",
        Platform.PostgreSQL => $"SELECT EXISTS(SELECT * FROM pg_database WHERE datname = '{{{{{templateName}Db}}}}')",
        Platform.MySQL => $"SELECT EXISTS(SELECT * FROM information_schema.schemata WHERE SCHEMA_NAME = '{{{{{templateName}Db}}}}')",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
    };

    internal static string GetDatabaseIdentificationScript(string templateName, Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"SELECT [Name] FROM master.sys.databases WHERE [Name] = '{{{{{templateName}Db}}}}'",
        Platform.PostgreSQL => $"SELECT datname FROM pg_database WHERE datname = '{{{{{templateName}Db}}}}'",
        Platform.MySQL => $"SELECT SCHEMA_NAME FROM information_schema.schemata WHERE SCHEMA_NAME = '{{{{{templateName}Db}}}}'",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, $"Unsupported platform: {platform}")
    };

    internal static Template CreateDefaultTemplate(string templateName, Platform platform)
    {
        var template = new Template
        {
            Name = templateName,
            DatabaseIdentificationScript = GetDatabaseIdentificationScript(templateName, platform)
        };
        AddDefaultScriptFolders(template, platform);
        return template;
    }

    private static void AddDefaultScriptFolders(Template template, Platform platform)
        => AddDefaultScriptFolders(template, platform, isSchemaTemplate: false);

    /// <summary>
    /// Adds the platform-appropriate default <see cref="TemplateFolder"/> set — delegates to
    /// <see cref="Template.GetDefaultTemplateFolders(Platform, bool)"/>, the deploy path's own
    /// source of truth for this set, so the scaffolder can never drift from what Template.Load
    /// falls back to. That method already handles the <paramref name="isSchemaTemplate"/>
    /// database-scoped-object exclusion (design §3.3 / §7.2), so there's nothing left to do here
    /// beyond appending the result.
    /// </summary>
    private static void AddDefaultScriptFolders(Template template, Platform platform, bool isSchemaTemplate)
        => template.ScriptFolders.AddRange(Template.GetDefaultTemplateFolders(platform, isSchemaTemplate));
}
