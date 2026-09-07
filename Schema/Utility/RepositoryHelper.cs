// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public static void WriteSchemaFiles(string productPath, Platform platform)
    {
        WriteSchemaFilesWithResults(productPath, platform);
    }

    /// <summary>
    /// Writes or merges schema files and returns detailed results for each file.
    /// </summary>
    public static List<SchemaFileResult> WriteSchemaFilesWithResults(string productPath, Platform platform)
    {
        var directory = DirectoryWrapper.GetFromFactory();
        var schemaPath = Path.Combine(productPath, ".json-schemas");
        directory.CreateDirectory(schemaPath);

        var schemaFileNames = GetSchemaFileNames(platform);
        var results = new List<SchemaFileResult>();
        foreach (var fileName in schemaFileNames)
            results.Add(WriteSchemaFileWithResult(schemaPath, fileName, platform));
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
        var existingObj = JObject.Parse(existing);
        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existingObj);
        file.WriteAllText(schemaFile, merged.ToString(Formatting.Indented));
        return new SchemaFileResult { FileName = fileName };
    }

    /// <summary>
    /// Maps the base collection element types (Column/Index/ForeignKey/CheckConstraint) to their
    /// platform subclass so generated table element schemas include platform-specific properties
    /// (e.g. CheckExpression, GenerationExpression). Reuses the canonical platform→subclass mapping
    /// in <see cref="PlatformDeserializer"/> rather than duplicating it.
    /// </summary>
    private static Func<Type, Type> PlatformElementResolver(Platform platform) => t =>
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
