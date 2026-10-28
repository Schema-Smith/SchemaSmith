// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using log4net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using MySqlConnector;
using Npgsql;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Utility;
using Schema.Configuration;

namespace SchemaTongs;

public class SchemaTongs
{
    private readonly ILog _progressLog = LogFactory.GetLogger("ProgressLog");
    private readonly Platform _platform;
    private readonly StringComparison _schemaNameComparison;
    private readonly Stopwatch _stopwatch = new();
    private string _productPath = "";
    private string _templatePath = "";
    private string[] _objectsToCast = [];

    // Package ScriptTokens (template + product, template-precedence) used to substitute variant-gate
    // expressions before evaluating them against the source DB during re-extraction. Deploy resolves
    // ScriptTokens across the package before any gate evaluates; extraction must match so a token
    // gate (e.g. '{{Edition}}'='Modern') folds to the active variant instead of being written as a
    // spurious ungated 3rd entry (SS-DUP-001). Populated by LoadPackageTokens.
    private List<KeyValuePair<string, string>> _packageTokens = [];

    private FolderMappingConfig _folderMappingConfig;
    internal Dictionary<ScriptObjectType, string> ResolvedFolders { get; } = new();

    // Schema-template extraction mode (design §7). Activated by Source.Schema non-empty.
    private bool _isSchemaTemplate;
    private string _sourceSchema;
    private string _schemaIdentificationScript;

    // Aggregated unqualified-identifier audit (design §7.4). Populated as each .sql body is
    // rewritten in schema-template mode; emitted once as a Warn at the end of CastTemplate.
    private readonly List<(string RelativePath, IReadOnlyList<int> Lines)> _unqualifiedReferenceAudit = new();

    // Audit entries for unqualified refs detected inside JSON property bodies (table-level
    // expression strings like CheckConstraint.Expression, Column.ComputedExpression, etc.).
    // Line numbers within these property bodies are not meaningful to a reviewer (they
    // don't align to the JSON file's lines), so we attribute by property context instead:
    // "Tables/Customers.json: CheckConstraint 'CK_Email_Format'".
    private readonly List<(string RelativePath, string PropertyContext)> _unqualifiedPropertyAudit = new();

    // Platform-specific ShouldCast flags
    // Common
    private bool _includeTables;
    private bool _includeViews;

    // SQL Server specific
    private bool _includeSchemas;
    private bool _includeUserDefinedTypes;
    private bool _includeUserDefinedFunctions;
    private bool _includeStoredProcedures;
    private bool _includeTableTriggers;
    private bool _includeFullTextCatalogs;
    private bool _includeFullTextStopLists;
    private bool _includeDDLTriggers;
    private bool _includeXmlSchemaCollections;
    // TRANSITIONAL (ScriptDynamicDependencyRemovalForFunctions, deprecated in 2.8): the setting is read only to warn
    // that it does nothing. Delete the field, the warning and the SettingsKeys entry two releases past 2.8 GA.
    private bool _scriptDynamicDependencyRemovalForFunctions;
    private bool _includeIndexedViews;
    private bool _includeSynonyms;
    private bool _includeSecurityPolicies;

    // PostgreSQL specific
    private bool _includeDomainTypes;
    private bool _includeEnumTypes;
    private bool _includeCompositeTypes;
    private bool _includeFunctions; // PostgreSQL: functions, trigger functions, and window functions
    private bool _includeAggregates;
    private bool _includeProcedures;
    private bool _includeSequences; // PostgreSQL always; SQL Server (2012+) and MariaDb-only on MySQL's base platform
    private bool _includeRules;
    private bool _includeTriggers;
    private bool _includeMaterializedViews;
    private bool _includeCollations;
    private bool _includePublications;
    private bool _includeRangeTypes;
    private bool _includeTextSearchConfigurations;
    private bool _includeOperators;
    private bool _includeCasts;
    private bool _includeEventTriggers;

    // MySQL specific
    private bool _includeEvents;
    private bool _includePackages;

    // Orphan handling
    private OrphanHandlingMode _orphanHandlingMode;

    // Script validation
    internal bool _validateScripts;
    internal bool _saveInvalidScripts = true;
    private readonly ScriptValidator _scriptValidator = new();
    internal readonly List<(string FileName, string ErrorMessage, ScriptObjectType ObjectType)> _invalidScripts = new();

    internal CheckConstraintStyle CheckConstraintStyle => _checkConstraintStyle;
    private CheckConstraintStyle _checkConstraintStyle;
    private ObjectOrder _objectOrder = ObjectOrder.Physical;
    private bool _preserveExistingOrder = true;

    // The wire encoding used to KINDLE and to READ the SQL Server schema model back out of the source.
    // Below the OPENJSON/FOR JSON cliff (pre-2016 binary), or when Source:CompatEncoding=legacy, the
    // model is generated as XML (GenerateTableXml / GenerateIndexedViewXml) and converted back to the
    // JSON model via ModelXmlSerializer.FromIngestXml. Resolved per source in CastSqlServerObjects from
    // best-effort version detection (defaults to Json when the version is unknown).
    private IngestEncoding _ingestEncoding = IngestEncoding.Json;
    private ModuleSyntax _moduleSyntax = ModuleSyntax.CreateOrAlter;

    public SchemaTongs(Platform platform)
    {
        _platform = platform;
        _schemaNameComparison = platform == Platform.SqlServer
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    internal void SetTemplatePath(string path) => _templatePath = path;

    internal void ResolveFolderMappings()
    {
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        _folderMappingConfig = new FolderMappingConfig(config, _platform);
        _folderMappingConfig.Validate();

        var templateFile = Path.Join(_templatePath, "Template.json");
        if (!FileWrapper.GetFromFactory().Exists(templateFile)) return;

        var template = JsonHelper.Load<Template>(templateFile);

        // In schema-template mode (design §7.2), the four database-scoped types are
        // intentionally omitted from the folder set — using the schema-template overload
        // ensures ResolveFolderMappings doesn't silently re-add them via FolderMapping
        // configuration defaults.
        var defaultSlots = Template.GetDefaultTemplateFolders(_platform, _isSchemaTemplate)
            .Where(f => f.ObjectType != ScriptObjectType.None)
            .ToDictionary(f => f.ObjectType, f => f.QuenchSlot);

        var modified = false;

        foreach (ScriptObjectType type in Enum.GetValues<ScriptObjectType>())
        {
            if (type == ScriptObjectType.None) continue;
            var configFolder = _folderMappingConfig.GetFolderName(type);
            if (configFolder == null) continue;

            var templateFolder = template.ScriptFolders.FirstOrDefault(f => f.ObjectType == type);

            if (templateFolder != null)
            {
                ResolvedFolders[type] = templateFolder.FolderPath;
                if (!string.Equals(templateFolder.FolderPath, configFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _progressLog.Warn($"SchemaTongs config maps '{type}' to '{configFolder}' but template uses '{templateFolder.FolderPath}'. " +
                                      $"Extracting to '{templateFolder.FolderPath}' per template definition.");
                }
            }
            else if (!RepositoryHelper.FoldersWrittenOnUse.Contains(type))
            {
                if (defaultSlots.TryGetValue(type, out var slot))
                {
                    template.ScriptFolders.Add(new TemplateFolder
                    {
                        FolderPath = configFolder,
                        QuenchSlot = slot,
                        ObjectType = type
                    });
                    ResolvedFolders[type] = configFolder;
                    modified = true;

                    DirectoryWrapper.GetFromFactory().CreateDirectory(Path.Combine(_templatePath, configFolder));
                }
            }
        }

        if (modified)
            WritePackageObject(templateFile, template, "templates");
    }


    internal string GetCastPath(ScriptObjectType type, string defaultFolderName)
    {
        if (ResolvedFolders.TryGetValue(type, out var folder))
            return Path.Combine(_templatePath, folder);
        return Path.Combine(_templatePath, defaultFolderName);
    }

    internal string ResolveFolderName(string defaultFolderName, ScriptObjectType type)
    {
        if (ResolvedFolders.TryGetValue(type, out var folder))
            return folder;
        return defaultFolderName;
    }

    private IDbConnection GetConnection(string targetDb)
    {
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        if (!string.IsNullOrEmpty(connectionStringOverride))
        {
            if (_platform.GetBasePlatform() == Platform.MySQL &&
                !connectionStringOverride.Contains("AllowUserVariables", StringComparison.OrdinalIgnoreCase))
            {
                _progressLog.Warn("Connection string override for MySQL does not contain AllowUserVariables=true. " +
                                  "This is required for SchemaSmith stored procedures that use PREPARE/EXECUTE.");
            }
            var overrideFactory = GetConnectionFactory();
            var overrideConnection = overrideFactory.GetDbConnection(connectionStringOverride);
            overrideConnection.Open();
            return Prepared(overrideConnection);
        }

        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var server = config[SettingsKeys.Source.Server] ?? config[SettingsKeys.Target.Server];
        var user = config[SettingsKeys.Source.User] ?? config[SettingsKeys.Target.User];
        var password = config[SettingsKeys.Source.Password] ?? config[SettingsKeys.Target.Password];
        var port = config[SettingsKeys.Source.Port] ?? config[SettingsKeys.Target.Port];
        var connectionProperties = ConnectionString.ReadProperties(config, SettingsKeys.Source.ConnectionProperties);
        if (connectionProperties.Count == 0)
            connectionProperties = ConnectionString.ReadProperties(config, SettingsKeys.Target.ConnectionProperties);
        CommandLineParser.ApplyTransportSecuritySwitch(_platform, connectionProperties);
        var integratedSecurity = string.Equals(config[SettingsKeys.Source.IntegratedSecurity] ?? config[SettingsKeys.Target.IntegratedSecurity], "true", StringComparison.OrdinalIgnoreCase);

        var connectionString = ConnectionString.Build(_platform, server, targetDb, user, password, port, connectionProperties, integratedSecurity: integratedSecurity);
        var connectionFactory = GetConnectionFactory();
        var connection = connectionFactory.GetDbConnection(connectionString);
        connection.Open();
        return Prepared(connection);
    }

    private IDbConnection Prepared(IDbConnection connection)
    {
        // The extraction routines say what they leave out through a warning message (a temporal foreign key on
        // PostgreSQL, a bound rule on SQL Server); without a listener the message dies in the driver and the package
        // is silently short of it.
        if (connection is NpgsqlConnection npgsql)
            npgsql.Notice += (_, e) =>
            {
                if (string.Equals(e.Notice.Severity, "WARNING", StringComparison.OrdinalIgnoreCase))
                    _progressLog.Warn($"    WARNING: {e.Notice.MessageText}");
            };
        if (connection is SqlConnection sqlServer)
            sqlServer.InfoMessage += (_, e) =>
            {
                foreach (SqlError error in e.Errors)
                    if (error.Class <= 10 && error.Message.StartsWith("WARNING: ", StringComparison.Ordinal))
                        _progressLog.Warn($"    {error.Message}");
            };
        // A catalog query that joins pg_proc to itself several times is estimated past the JIT thresholds, and
        // compiling it costs seconds for a query that runs in milliseconds.
        if (_platform.GetBasePlatform() == Platform.PostgreSQL)
        {
            using var jitOff = connection.CreateCommand();
            jitOff.CommandText = "SET jit = off";
            jitOff.ExecuteNonQuery();
        }
        return connection;
    }

    private IDbConnectionFactory GetConnectionFactory()
    {
        return _platform.GetBasePlatform() switch
        {
            Platform.SqlServer => SqlServerConnectionFactory.GetFromFactory(),
            Platform.PostgreSQL => PostgreSqlConnectionFactory.GetFromFactory(),
            Platform.MySQL => MySqlConnectionFactory.GetFromFactory(),
            _ => throw new Exception($"Unsupported platform: {_platform}")
        };
    }

    /// <summary>
    /// Source version pre-flight — detects the extraction source's engine version (and, for SQL Server,
    /// the target database's compatibility level), logs it, and enforces SchemaSmith's intrinsic version
    /// floor before any extraction. Run from the CLI entry before <see cref="CastTemplate"/> so an
    /// unsupported source (e.g. SQL Server 2012, where the kindling scripts' STRING_AGG would otherwise
    /// die with a raw "not a recognized built-in function" error) fails fast with a clear message.
    /// </summary>
    public void PreFlightSourceVersion()
    {
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var targetDb = config[SettingsKeys.Source.Database] ?? config[SettingsKeys.Source.Schema];
        var server = config[SettingsKeys.Source.Server] ?? config[SettingsKeys.Target.Server] ?? "source";
        using var connection = GetConnection(targetDb);
        using var command = connection.CreateCommand();
        var info = TargetVersionDetector.Detect(command, _platform,
            _platform.GetBasePlatform() == Platform.SqlServer ? targetDb : null);
        _progressLog.Info($"Source {info.Platform} version {VersionHelper.DisplayVersion(info)}" +
                          (info.CompatibilityLevel is { } lvl ? $" (compatibility level {lvl})" : ""));
        PreFlightVersionGuard.CheckOrThrow(info, server, targetDb);
    }

    // Detected SOURCE server major, kept so extraction queries can gate a filter on a catalog column
    // that does not exist on older servers. 0 until version detection has run.
    private int _sourceMajor;
    private bool _sourceReportsXmlCompression;

    /// <summary>
    /// Whether the SOURCE server can report XML_COMPRESSION. It DEPLOYS from SQL Server 2022 but only
    /// READS from 2025 -- sys.partitions.xml_compression does not exist before then -- and the legacy XML
    /// ingest path cannot read it at any version. Where this is false the package being refreshed is the
    /// only record of the setting, so ImportTableHelper carries it forward instead of letting an
    /// extraction that structurally cannot see it strip a property the server is honouring.
    /// </summary>
    private bool SourceCanReportXmlCompression =>
        _platform == Platform.SqlServer && _sourceReportsXmlCompression && _ingestEncoding != IngestEncoding.Xml;

    /// <summary>
    /// Excludes a LIVE ledger view (<c>&lt;table&gt;_Ledger</c>), which a ledger table generates and
    /// which carries no engine-reserved name prefix — only <c>sys.tables.ledger_view_id</c> identifies
    /// it. That column is 2022+, so the clause is added only when the source is new enough to have it;
    /// referencing it statically would fail to bind on an older server.
    /// </summary>
    private string LedgerViewFilter() =>
        _sourceMajor >= 16
            ? Environment.NewLine + "   AND NOT EXISTS (SELECT 1 FROM sys.tables lt WHERE lt.ledger_view_id = o.object_id)"
            : "";

    public void CastTemplate()
    {
        _stopwatch.Start();
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var targetDb = config[SettingsKeys.Source.Database] ?? config[SettingsKeys.Source.Schema];
        if (string.IsNullOrEmpty(targetDb)) throw new RunFailedException("Source database is required. Set 'Source:Database' in SchemaTongs.settings.json.");
        _productPath = ConfigHelper.PathSetting(config, SettingsKeys.ProductKeys.Path);

        LoadShouldCastSettings(config);

        _objectsToCast = (config[SettingsKeys.ShouldCast.ObjectList]?.ToLower() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        var configStyle = Enum.TryParse<CheckConstraintStyle>(config[SettingsKeys.ProductKeys.CheckConstraintStyle], true, out var style)
            ? style : (CheckConstraintStyle?)null;

        // Extraction ordering. Both are extraction preferences -- nothing on the deploy path reads them.
        // PreserveExistingOrder defaults ON: it only has any effect when a file already exists, so a first
        // extract is unaffected and nobody is opted into a surprise.
        _objectOrder = Enum.TryParse<ObjectOrder>(config[SettingsKeys.ProductKeys.ObjectOrder], true, out var order)
            ? order : ObjectOrder.Physical;
        _preserveExistingOrder = !bool.TryParse(config[SettingsKeys.ProductKeys.PreserveExistingOrder], out var preserve) || preserve;

        var productFile = Path.Combine(_productPath, "Product.json");
        var productIsNew = !FileWrapper.GetFromFactory().Exists(productFile);

        RepositoryHelper.UpdateOrInitRepository(_productPath, config[SettingsKeys.ProductKeys.Name], config[SettingsKeys.TemplateKeys.Name], targetDb, _platform,
            isSchemaTemplate: _isSchemaTemplate);

        ApplyCheckConstraintStyle(productFile, productIsNew, configStyle);

        _templatePath = RepositoryHelper.UpdateOrInitTemplate(
            _productPath, config[SettingsKeys.TemplateKeys.Name], targetDb, _platform,
            sourceSchema: _isSchemaTemplate ? _sourceSchema : null,
            userSchemaIdentificationScript: _isSchemaTemplate
                ? (string.IsNullOrWhiteSpace(_schemaIdentificationScript) ? null : _schemaIdentificationScript)
                : null);

        ResolveFolderMappings();

        BuildFileIndexes();

        LoadPackageTokens();

        RepositoryHelper.WriteSchemaFiles(_productPath, _platform, _progressLog.Warn);

        // Product.json and Template.json are written during init, before the schemas above exist, and
        // a later extraction does not rewrite them -- so they cannot pick up a $schema reference the
        // way the object files do (WritePackageObject). Stamped here, once the schemas are on disk.
        // Neither file carries variants, so this is outside the byte-for-byte promise that governs
        // object files.
        RepositoryHelper.StampSchemaRef(_productPath, Path.Join(_productPath, "Product.json"),
            "products", _platform, _progressLog.Warn);
        RepositoryHelper.StampSchemaRef(_productPath, Path.Join(_templatePath, "Template.json"),
            "templates", _platform, _progressLog.Warn);

        CastDatabaseObjects(targetDb);
        CleanupResolvedSqulerrorFiles();
        ProcessOrphanedFiles();

        GenerateInvalidObjectCleanupScript();
        EmitUnqualifiedReferenceAudit();
        _stopwatch.Stop();
        LogSummary();
    }

    /// <summary>
    /// Emits the aggregated unqualified-identifier audit warning at the end of a
    /// schema-template extraction (design §7.4). One warning lists every <c>.sql</c>
    /// file containing unqualified references along with their 1-based line numbers.
    /// No-op outside schema-template mode and when no unqualified refs were observed.
    /// </summary>
    private void EmitUnqualifiedReferenceAudit()
    {
        if (!_isSchemaTemplate
            || (_unqualifiedReferenceAudit.Count == 0 && _unqualifiedPropertyAudit.Count == 0))
            return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The following extracted files contain unqualified object references. " +
                      "Review them and add `{{SchemaName}}.` qualification where the reference " +
                      "targets the iteration schema. Unqualified references that target a shared " +
                      "schema (built-in functions, shared lookup tables) can stay as-is — make that " +
                      "decision deliberately, because the deployed engine does not set a default " +
                      "schema per iteration.");
        foreach (var (relPath, lines) in _unqualifiedReferenceAudit)
            sb.AppendLine($"  - {relPath}: lines {string.Join(", ", lines)}");
        foreach (var (relPath, context) in _unqualifiedPropertyAudit)
            sb.AppendLine($"  - {relPath}: {context}");
        _progressLog.Warn(sb.ToString());
    }

    /// <summary>
    /// Helper invoked at every <c>.sql</c>-body write site in schema-template mode. Rewrites
    /// source-schema-qualified identifiers to <c>{{SchemaName}}</c>, captures unqualified-ref
    /// line numbers for the audit warning, and returns the body to write. No-op (returns the
    /// input verbatim) outside schema-template mode.
    /// </summary>
    private string RewriteSqlBodyForSchemaTemplate(string body, string fileName)
    {
        if (!_isSchemaTemplate) return body;
        var result = SqlBodyRewriter.Rewrite(body, _sourceSchema);
        if (result.UnqualifiedReferenceLines.Count > 0)
        {
            var relativePath = fileName.StartsWith(_templatePath, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(_templatePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Path.GetFileName(fileName);
            _unqualifiedReferenceAudit.Add((relativePath, result.UnqualifiedReferenceLines));
        }
        return result.Body;
    }

    /// <summary>
    /// In schema-template mode, scrub schema-bearing properties on a deserialized table so the
    /// downstream <c>JsonHelper.Write</c> emits an unqualified package:
    /// <list type="bullet">
    /// <item>The platform-specific <c>Schema</c> property is nulled (engine fills with
    /// <c>{{SchemaName}}</c> at template load via <see cref="SchemaDefaultResolver"/>).</item>
    /// <item>FK <c>RelatedTableSchema</c> values matching the source schema are nulled
    /// (same-iteration FK); cross-schema literals are preserved as-is per design §7.2.</item>
    /// <item>SQL expression strings carried as JSON properties (table-level check constraints,
    /// column defaults / computed / check / generation expressions, filtered-index expressions,
    /// exclude-constraint filter expressions) are rewritten through <see cref="SqlBodyRewriter"/>
    /// so source-schema-qualified references collapse to <c>{{SchemaName}}</c> (design §7.3).
    /// Unqualified references inside those bodies are captured for the §7.4 audit, attributed
    /// by property context (e.g. <c>CheckConstraint 'CK_Email_Format'</c>) rather than line
    /// number — line numbers within a JSON property body don't align to the JSON file's lines.</item>
    /// </list>
    /// No-op outside schema-template mode.
    /// </summary>
    private void ScrubSchemaForTemplate(Table tableObj, string fileName = null)
    {
        if (!_isSchemaTemplate || tableObj == null) return;
        var relPath = ResolveAuditRelativePath(fileName);

        // Common expression rewrites (Table-level CheckConstraints, Column.Default,
        // base Index — none of those properties are platform-specific).
        foreach (var check in tableObj.CheckConstraints ?? Enumerable.Empty<CheckConstraint>())
            check.Expression = RewriteJsonExpression(check.Expression, relPath, $"CheckConstraint '{check.Name}'");
        foreach (var col in tableObj.Columns ?? Enumerable.Empty<Column>())
            col.Default = RewriteJsonExpression(col.Default, relPath, $"Column.Default '{col.Name}'");

        switch (tableObj)
        {
            case SqlServerTable ss:
                ss.Schema = null;
                foreach (var fk in ss.ForeignKeys.OfType<SqlServerForeignKey>())
                {
                    if (string.Equals(
                            Identifier.Unwrap(fk.RelatedTableSchema, _platform),
                            Identifier.Unwrap(_sourceSchema, _platform),
                            _schemaNameComparison))
                        fk.RelatedTableSchema = null;
                }
                foreach (var col in ss.Columns.OfType<SqlServerColumn>())
                {
                    col.CheckExpression = RewriteJsonExpression(col.CheckExpression, relPath, $"Column.CheckExpression '{col.Name}'");
                    col.ComputedExpression = RewriteJsonExpression(col.ComputedExpression, relPath, $"Column.ComputedExpression '{col.Name}'");
                }
                foreach (var idx in ss.Indexes.OfType<SqlServerIndex>())
                    idx.FilterExpression = RewriteJsonExpression(idx.FilterExpression, relPath, $"Index.FilterExpression '{idx.Name}'");
                foreach (var stat in ss.Statistics ?? Enumerable.Empty<Schema.Domain.SqlServer.Statistic>())
                    stat.FilterExpression = RewriteJsonExpression(stat.FilterExpression, relPath, $"Statistic.FilterExpression '{stat.Name}'");
                break;
            case PostgreSqlTable pg:
                pg.Schema = null;
                foreach (var fk in pg.ForeignKeys.OfType<PostgreSqlForeignKey>())
                {
                    if (string.Equals(
                            Identifier.Unwrap(fk.RelatedTableSchema, _platform),
                            Identifier.Unwrap(_sourceSchema, _platform),
                            _schemaNameComparison))
                        fk.RelatedTableSchema = null;
                }
                foreach (var col in pg.Columns.OfType<PostgreSqlColumn>())
                    col.GenerationExpression = RewriteJsonExpression(col.GenerationExpression, relPath, $"Column.GenerationExpression '{col.Name}'");
                foreach (var idx in pg.Indexes.OfType<PostgreSqlIndex>())
                    idx.FilterExpression = RewriteJsonExpression(idx.FilterExpression, relPath, $"Index.FilterExpression '{idx.Name}'");
                foreach (var ex in pg.ExcludeConstraints ?? Enumerable.Empty<ExcludeConstraint>())
                    ex.FilterExpression = RewriteJsonExpression(ex.FilterExpression, relPath, $"ExcludeConstraint.FilterExpression '{ex.Name}'");
                break;
        }
    }

    /// <summary>
    /// Rewrites one expression-bearing JSON property value in schema-template mode. Returns the
    /// rewritten body and, when the property contained an unqualified table-position identifier,
    /// records a §7.4 audit entry attributed by <paramref name="propertyContext"/>. Returns the
    /// input verbatim for null/empty values or outside schema-template mode.
    /// </summary>
    private string RewriteJsonExpression(string value, string relPath, string propertyContext)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = SqlBodyRewriter.Rewrite(value, _sourceSchema);
        if (result.UnqualifiedReferenceLines.Count > 0 && !string.IsNullOrEmpty(relPath))
            _unqualifiedPropertyAudit.Add((relPath, propertyContext));
        return result.Body;
    }

    /// <summary>
    /// Computes the audit-friendly relative path for a JSON file inside the template root.
    /// Returns <c>null</c> when <paramref name="fileName"/> is null/empty so callers can skip
    /// audit attribution cleanly.
    /// </summary>
    private string ResolveAuditRelativePath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        return fileName.StartsWith(_templatePath, StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(_templatePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(fileName);
    }

    /// <summary>
    /// True when the (schema, name) tuple should be extracted in the current run. In
    /// schema-template mode, only objects whose schema matches <see cref="_sourceSchema"/>
    /// are emitted; cross-schema objects are skipped (they belong on a regular template
    /// earlier in <c>TemplateOrder</c>). Outside schema-template mode this is always true.
    /// </summary>
    private bool ShouldExtractFromSchema(string schema)
        => !_isSchemaTemplate
           || string.Equals(
                  Identifier.Unwrap(schema, _platform),
                  Identifier.Unwrap(_sourceSchema, _platform),
                  _schemaNameComparison);

    /// <summary>
    /// Returns the appropriate filename for a per-schema object. In schema-template mode the
    /// schema prefix is omitted per design §7.2.
    /// </summary>
    private string EncodeObjectFileName(string schema, string name, string extension)
        => _isSchemaTemplate
            ? EncodeFileName(name, extension)
            : EncodeFileName(schema, name, extension);

    private void LoadShouldCastSettings(IConfigurationRoot config)
    {
        _orphanHandlingMode = Enum.TryParse<OrphanHandlingMode>(config[SettingsKeys.OrphanHandling.Mode], true, out var mode)
            ? mode : OrphanHandlingMode.Detect;

        _validateScripts = config[SettingsKeys.ShouldCast.ValidateScripts]?.ToLower() == "true";
        _saveInvalidScripts = config[SettingsKeys.ShouldCast.SaveInvalidScripts]?.ToLower() != "false";

        _includeTables = config[SettingsKeys.ShouldCast.Tables]?.ToLower() != "false";
        _includeViews = config[SettingsKeys.ShouldCast.Views]?.ToLower() != "false";

        // Schema-template extraction mode (design §7.1). MySQL excluded — no schema-inside-database concept (design §2).
        _sourceSchema = _platform.GetBasePlatform() != Platform.MySQL ? (config[SettingsKeys.Source.Schema] ?? "") : "";
        var rawSourceSchema = config[SettingsKeys.Source.Schema];
        if (_platform.GetBasePlatform() == Platform.MySQL && !string.IsNullOrWhiteSpace(rawSourceSchema))
            _progressLog.Warn($"MySQL: Source.Schema='{rawSourceSchema}' is set but ignored as schema-template activator — running in regular mode.");
        _schemaIdentificationScript = config[SettingsKeys.TemplateKeys.SchemaIdentificationScript] ?? "";
        _isSchemaTemplate = !string.IsNullOrWhiteSpace(_sourceSchema);

        switch (_platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                _includeSchemas = config[SettingsKeys.ShouldCast.Schemas]?.ToLower() != "false";
                _includeUserDefinedTypes = config[SettingsKeys.ShouldCast.UserDefinedTypes]?.ToLower() != "false";
                _includeUserDefinedFunctions = config[SettingsKeys.ShouldCast.Functions]?.ToLower() != "false";
                _includeStoredProcedures = config[SettingsKeys.ShouldCast.Procedures]?.ToLower() != "false";
                _includeTableTriggers = config[SettingsKeys.ShouldCast.TableTriggers]?.ToLower() != "false";
                _includeFullTextCatalogs = config[SettingsKeys.ShouldCast.Catalogs]?.ToLower() != "false";
                _includeFullTextStopLists = config[SettingsKeys.ShouldCast.StopLists]?.ToLower() != "false";
                _includeDDLTriggers = config[SettingsKeys.ShouldCast.DdlTriggers]?.ToLower() != "false";
                _includeXmlSchemaCollections = config[SettingsKeys.ShouldCast.XmlSchemaCollections]?.ToLower() != "false";
                _scriptDynamicDependencyRemovalForFunctions = config[SettingsKeys.ShouldCast.ScriptDynamicDependencyRemovalForFunctions]?.ToLower() == "true";
                if (_scriptDynamicDependencyRemovalForFunctions)
                    _progressLog.Warn("ShouldCast:ScriptDynamicDependencyRemovalForFunctions is deprecated and does nothing: SchemaQuench drops the schema-bound " +
                                      "dependents a changed function blocks on and later steps put them back. Remove the setting.");
                _includeIndexedViews = config[SettingsKeys.ShouldCast.IndexedViews]?.ToLower() != "false";
                _includeSequences = config[SettingsKeys.ShouldCast.Sequences]?.ToLower() != "false";
                _includeSynonyms = config[SettingsKeys.ShouldCast.Synonyms]?.ToLower() != "false";
                _includeSecurityPolicies = config[SettingsKeys.ShouldCast.SecurityPolicies]?.ToLower() != "false";
                _moduleSyntax = ReadModuleSyntax(config[SettingsKeys.ShouldCast.ModuleSyntax]);
                break;

            case Platform.PostgreSQL:
                _includeSchemas = config[SettingsKeys.ShouldCast.Schemas]?.ToLower() != "false";
                _includeDomainTypes = config[SettingsKeys.ShouldCast.DomainTypes]?.ToLower() != "false";
                _includeEnumTypes = config[SettingsKeys.ShouldCast.EnumTypes]?.ToLower() != "false";
                _includeCompositeTypes = config[SettingsKeys.ShouldCast.CompositeTypes]?.ToLower() != "false";
                _includeFunctions = config[SettingsKeys.ShouldCast.Functions]?.ToLower() != "false";
                _includeAggregates = config[SettingsKeys.ShouldCast.Aggregates]?.ToLower() != "false";
                _includeProcedures = config[SettingsKeys.ShouldCast.Procedures]?.ToLower() != "false";
                _includeSequences = config[SettingsKeys.ShouldCast.Sequences]?.ToLower() != "false";
                _includeRules = config[SettingsKeys.ShouldCast.Rules]?.ToLower() != "false";
                _includeTriggers = config[SettingsKeys.ShouldCast.TableTriggers]?.ToLower() != "false";
                _includeMaterializedViews = config[SettingsKeys.ShouldCast.MaterializedViews]?.ToLower() != "false";
                _includeCollations = config[SettingsKeys.ShouldCast.Collations]?.ToLower() != "false";
                _includePublications = config[SettingsKeys.ShouldCast.Publications]?.ToLower() != "false";
                _includeRangeTypes = config[SettingsKeys.ShouldCast.RangeTypes]?.ToLower() != "false";
                _includeTextSearchConfigurations = config[SettingsKeys.ShouldCast.TextSearchConfigurations]?.ToLower() != "false";
                _includeOperators = config[SettingsKeys.ShouldCast.Operators]?.ToLower() != "false";
                _includeCasts = config[SettingsKeys.ShouldCast.Casts]?.ToLower() != "false";
                _includeEventTriggers = config[SettingsKeys.ShouldCast.EventTriggers]?.ToLower() != "false";
                break;

            case Platform.MySQL:
                _includeUserDefinedFunctions = config[SettingsKeys.ShouldCast.Functions]?.ToLower() != "false";
                _includeStoredProcedures = config[SettingsKeys.ShouldCast.Procedures]?.ToLower() != "false";
                _includeTableTriggers = config[SettingsKeys.ShouldCast.TableTriggers]?.ToLower() != "false";
                _includeEvents = config[SettingsKeys.ShouldCast.Events]?.ToLower() != "false";
                // MariaDb-only: MySQL has no native SEQUENCE object, so the flag stays false on plain MySQL
                // regardless of config — there is nothing for ShouldCast:Sequences to mean there.
                _includeSequences = _platform == Platform.MariaDb && config[SettingsKeys.ShouldCast.Sequences]?.ToLower() != "false";
                _includePackages = _platform == Platform.MariaDb && config[SettingsKeys.ShouldCast.Packages]?.ToLower() != "false";
                break;
        }

        if (_isSchemaTemplate) ForceDatabaseScopedShouldCastFalse();
    }

    /// <summary>
    /// Schema-template extraction (design §7.2) does not emit content for the four
    /// database-scoped object types — they don't fan out per schema iteration and must live in
    /// a regular template that runs earlier in <c>TemplateOrder</c>. If the user left the
    /// corresponding <c>ShouldCast</c> flags at <c>true</c>, force them off and warn so the
    /// user knows the setting was ignored. The warning is emitted only when the user set the
    /// flag (or relied on a default-true that produces extraction); a quiet skip would be too
    /// surprising.
    /// </summary>
    private void ForceDatabaseScopedShouldCastFalse()
    {
        if (_platform == Platform.SqlServer)
        {
            if (_includeSchemas)
            {
                _progressLog.Warn("ShouldCast.Schemas is ignored in schema-template extraction mode " +
                                  "(schema objects ARE the iteration unit). Move shared schema scripts " +
                                  "to a regular template earlier in TemplateOrder.");
                _includeSchemas = false;
            }
            if (_includeDDLTriggers)
            {
                _progressLog.Warn("ShouldCast.DDLTriggers is ignored in schema-template extraction mode " +
                                  "(DDL triggers are database-scoped). Move them to a regular template " +
                                  "earlier in TemplateOrder.");
                _includeDDLTriggers = false;
            }
            if (_includeFullTextCatalogs)
            {
                _progressLog.Warn("ShouldCast.Catalogs (full-text catalogs) is ignored in schema-template " +
                                  "extraction mode (full-text catalogs are database-scoped). Move them to " +
                                  "a regular template earlier in TemplateOrder.");
                _includeFullTextCatalogs = false;
            }
            if (_includeFullTextStopLists)
            {
                _progressLog.Warn("ShouldCast.StopLists (full-text stop lists) is ignored in schema-template " +
                                  "extraction mode (full-text stop lists are database-scoped). Move them to " +
                                  "a regular template earlier in TemplateOrder.");
                _includeFullTextStopLists = false;
            }
        }
        else if (_platform == Platform.PostgreSQL)
        {
            if (_includeSchemas)
            {
                _progressLog.Warn("ShouldCast.Schemas is ignored in schema-template extraction mode " +
                                  "(schema objects ARE the iteration unit). Move shared schema scripts " +
                                  "to a regular template earlier in TemplateOrder.");
                _includeSchemas = false;
            }
            if (_includePublications)
            {
                _progressLog.Warn("ShouldCast.Publications is ignored in schema-template extraction mode " +
                                  "(publications are database-scoped — CREATE PUBLICATION has no schema " +
                                  "qualifier). Move them to a regular template earlier in TemplateOrder.");
                _includePublications = false;
            }
            if (_includeCasts || _includeEventTriggers)
            {
                _progressLog.Warn("ShouldCast.Casts and ShouldCast.EventTriggers are ignored in schema-template extraction mode " +
                                  "(casts and event triggers are database-scoped and have no schema). Move them to a regular " +
                                  "template earlier in TemplateOrder.");
                _includeCasts = false;
                _includeEventTriggers = false;
            }
        }
    }

    private void ApplyCheckConstraintStyle(string productFile, bool productIsNew, CheckConstraintStyle? configStyle)
    {
        var productJson = FileWrapper.GetFromFactory().ReadAllText(productFile);
        var product = productJson != null
            ? (JsonText.Deserialize<Product>(productJson) ?? new Product())
            : new Product();
        var productStyle = product.CheckConstraintStyle;

        if (productIsNew && configStyle.HasValue)
        {
            product.CheckConstraintStyle = configStyle.Value;
            Product.Save(productFile, product);
            _checkConstraintStyle = configStyle.Value;
        }
        else if (!productIsNew && configStyle.HasValue && configStyle.Value != productStyle)
        {
            _progressLog.Warn($"SchemaTongs config specifies CheckConstraintStyle '{configStyle.Value}' but Product.json is set to '{productStyle}'. " +
                              $"Extracting as '{productStyle}' per the product definition. Update Product.json to change this.");
            _checkConstraintStyle = productStyle;
        }
        else
        {
            _checkConstraintStyle = productStyle;
        }
    }

    /// <summary>
    /// Loads the package's configured ScriptTokens (template + product) so variant-gate expressions
    /// can be token-substituted before evaluation during re-extraction — mirroring deploy, which
    /// resolves ScriptTokens across the package before any gate evaluates. Precedence matches the
    /// authoritative deploy gate-resolution path (<c>Template.Load</c>: template tokens override
    /// product tokens on key conflict). <see cref="SqlScript.TokenReplace"/> builds a first-added-wins
    /// lookup, so template entries are added first to win. Unresolved tokens are left as-is (same as
    /// deploy — <see cref="SqlScript.TokenReplace"/> leaves a placeholder it can't resolve untouched).
    /// </summary>
    private void LoadPackageTokens()
    {
        var tokens = new List<KeyValuePair<string, string>>();

        var templateFile = Path.Join(_templatePath, "Template.json");
        if (FileWrapper.GetFromFactory().Exists(templateFile))
        {
            var template = JsonHelper.Load<Template>(templateFile);
            if (template?.ScriptTokens != null) tokens.AddRange(template.ScriptTokens);
        }

        var productFile = Path.Join(_productPath, "Product.json");
        if (FileWrapper.GetFromFactory().Exists(productFile))
        {
            var productJson = FileWrapper.GetFromFactory().ReadAllText(productFile);
            var product = productJson != null ? JsonText.Deserialize<Product>(productJson) : null;
            if (product?.ScriptTokens != null) tokens.AddRange(product.ScriptTokens);
        }

        _packageTokens = tokens;
    }

    private readonly ExtractionStats _stats = new();

    // Mirrors ProductQuench.Failed: one or more tables were skipped (per-table catch below) rather
    // than aborting the run. The package on disk is only the tables that DID extract, so a caller
    // must not treat a zero exception / exit code as "the package is complete" -- Program.cs maps
    // this to the same partial-failure exit code SchemaQuench uses.
    public bool Failed => _stats.TableErrors > 0;

    /// <summary>2 when a table failed; 1 when an object was skipped or its script was invalid, so the package is
    /// missing it; otherwise 0.</summary>
    public int ExitCode => Failed ? 2 : _stats.SkippedObjects > 0 || _invalidScripts.Count > 0 ? 1 : 0;

    private readonly Dictionary<string, ExtractionFileIndex> _folderIndexes = new();
    private readonly List<string> _pendingSqulerrorCleanup = new();

    private void BuildFileIndexes()
    {
        var templateFile = Path.Join(_templatePath, "Template.json");
        if (!FileWrapper.GetFromFactory().Exists(templateFile)) return;

        var template = JsonHelper.Load<Template>(templateFile);
        if (template == null) return;

        foreach (var folder in template.ScriptFolders)
        {
            var fullPath = Path.Combine(_templatePath, folder.FolderPath);
            var index = new ExtractionFileIndex();
            index.BuildIndex(fullPath);
            _folderIndexes[folder.FolderPath] = index;
        }

        foreach (var jsonFolder in new[] { "Tables", "Indexed Views", "Materialized Views" })
        {
            var fullPath = Path.Combine(_templatePath, jsonFolder);
            var index = new ExtractionFileIndex();
            index.BuildIndex(fullPath);
            _folderIndexes[jsonFolder] = index;
        }
    }

    private string ResolveOutputPath(string baseFolderPath, string fileName)
    {
        var folderName = GetRelativeFolderName(baseFolderPath);
        if (_folderIndexes.TryGetValue(folderName, out var index))
        {
            var existingPath = index.FindExistingPath(fileName);
            if (existingPath != null)
            {
                // If the existing file is .sqlerror but we're writing .sql, use the .sql path
                // and clean up the old .sqlerror file
                if (existingPath.EndsWith(".sqlerror", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                {
                    var sqlPath = Path.ChangeExtension(existingPath, ".sql");
                    index.MarkWritten(existingPath);
                    index.MarkWritten(sqlPath);
                    _pendingSqulerrorCleanup.Add(existingPath);
                    return sqlPath;
                }

                index.MarkWritten(existingPath);
                return existingPath;
            }
        }

        var defaultPath = Path.Combine(baseFolderPath, fileName);
        _folderIndexes.GetValueOrDefault(folderName)?.MarkWritten(defaultPath);
        return defaultPath;
    }

    // Marks a content-resolved table path as written so orphan detection spares it (the resolver
    // picks the path by identity, bypassing the filename-keyed ResolveOutputPath).
    private void MarkPathWritten(string baseFolderPath, string path)
    {
        var folderName = GetRelativeFolderName(baseFolderPath);
        _folderIndexes.GetValueOrDefault(folderName)?.MarkWritten(path);
    }

    private static string EncodeFileName(string schema, string name, string extension)
    {
        return $"{FileNameEncoder.Encode(schema)}.{FileNameEncoder.Encode(name)}{extension}";
    }

    private static string EncodeFileName(string name, string extension)
    {
        return $"{FileNameEncoder.Encode(name)}{extension}";
    }

    private static string EncodeFullName(string fullName, string extension)
    {
        var parts = fullName.Split('.');
        return string.Join(".", parts.Select(FileNameEncoder.Encode)) + extension;
    }

    private string GetRelativeFolderName(string fullPath)
    {
        if (fullPath.StartsWith(_templatePath))
        {
            var relative = fullPath.Substring(_templatePath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative;
        }
        return fullPath;
    }

    /// <summary>
    /// Checks if a script should be skipped because it has a known .sqlerror file and validation is off.
    /// When validation is disabled, existing .sqlerror files are preserved as known-bad markers.
    /// Call this BEFORE writing the .sql file — returns true if the write should be skipped.
    /// </summary>
    internal bool ShouldSkipKnownBadScript(string filePath)
    {
        if (_validateScripts) return false; // When validating, always extract (validation handles the rest)

        var errorPath = Path.ChangeExtension(filePath, ".sqlerror");
        var file = FileWrapper.GetFromFactory();
        if (!file.Exists(errorPath)) return false;

        _progressLog.Info($"  Skipping export for known invalid script: {Path.GetFileName(filePath)} (.sqlerror exists, validation off)");
        _stats.SkippedObjects++;
        return true;
    }

    internal void ValidateAndHandleScript(IDbConnection connection, string filePath, string script,
        ScriptObjectType objectType)
    {
        if (!_validateScripts) return;

        // Skip validation for unchanged known-bad scripts — if an existing .sqlerror file
        // has identical content, the script hasn't changed on the server and will fail the same way
        var existingErrorPath = Path.ChangeExtension(filePath, ".sqlerror");
        var file = FileWrapper.GetFromFactory();
        if (file.Exists(existingErrorPath))
        {
            var existingContent = file.ReadAllText(existingErrorPath);
            if (string.Equals(existingContent, script, StringComparison.Ordinal))
            {
                _progressLog.Info($"  Skipping validation for unchanged invalid script: {Path.GetFileName(filePath)}");
                _invalidScripts.Add((Path.GetFileName(filePath), "Previously invalid — unchanged", objectType));
                // Remove the .sql file since we know it's still bad
                if (!filePath.Equals(existingErrorPath, StringComparison.OrdinalIgnoreCase))
                    file.Delete(filePath);
                return;
            }
            // Script changed on server — re-validate the new version
            _progressLog.Info($"  Re-validating changed script: {Path.GetFileName(filePath)}");
        }

        var result = _scriptValidator.ValidateScript(connection, script, objectType, _platform);
        if (result.IsValid) return;

        _progressLog.Warn($"  Invalid script detected: {Path.GetFileName(filePath)} — {result.ErrorMessage}");
        _invalidScripts.Add((Path.GetFileName(filePath), result.ErrorMessage, objectType));

        if (_saveInvalidScripts)
        {
            var errorPath = Path.ChangeExtension(filePath, ".sqlerror");
            file.WriteAllText(errorPath, script);
            if (!filePath.Equals(errorPath, StringComparison.OrdinalIgnoreCase))
                file.Delete(filePath);
        }
        else
        {
            file.Delete(filePath);
        }
    }

    private void CleanupResolvedSqulerrorFiles()
    {
        if (_pendingSqulerrorCleanup.Count == 0) return;

        var file = FileWrapper.GetFromFactory();
        foreach (var sqlerrorPath in _pendingSqulerrorCleanup)
        {
            if (file.Exists(sqlerrorPath))
            {
                file.Delete(sqlerrorPath);
                _progressLog.Info($"  Removed previously invalid script: {Path.GetFileName(sqlerrorPath)}");
            }
        }
    }

    internal void GenerateInvalidObjectCleanupScript()
    {
        if (_invalidScripts.Count == 0) return;

        var logsDir = Path.Combine(_templatePath, "Logs");
        var file = FileWrapper.GetFromFactory();
        var directory = DirectoryWrapper.GetFromFactory();

        // Archive existing cleanup scripts
        if (directory.Exists(logsDir))
        {
            var existingScripts = directory.GetFiles(logsDir, "_InvalidObjectCleanup.sql", SearchOption.TopDirectoryOnly);
            if (existingScripts.Length > 0)
            {
                var archiveDir = Path.Combine(logsDir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
                directory.CreateDirectory(archiveDir);
                foreach (var script in existingScripts)
                {
                    var destPath = Path.Combine(archiveDir, Path.GetFileName(script));
                    file.Move(script, destPath);
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- {_invalidScripts.Count} invalid objects detected during extraction on {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine();

        foreach (var (fileName, _, objectType) in _invalidScripts)
        {
            var drop = CleanupScriptGenerator.GenerateDropStatement(fileName, objectType, _platform);
            if (drop != null)
                sb.AppendLine(drop);
        }

        directory.CreateDirectory(logsDir);
        var scriptPath = Path.Combine(logsDir, "_InvalidObjectCleanup.sql");
        file.WriteAllText(scriptPath, sb.ToString());

        _progressLog.Warn($"{_invalidScripts.Count} invalid script(s) detected. Cleanup script written to {scriptPath}");
    }

    /// <summary>
    /// Writes one package object file, setting its <c>$schema</c> reference first.
    /// </summary>
    /// <remarks>
    /// The reference is set on the OBJECT being serialized rather than stamped onto the folder
    /// afterwards, and that distinction is the whole point: only a file this run actually writes can
    /// gain one. Re-extraction folds the variant active against the source and must leave every other
    /// file byte-for-byte identical -- an inactive variant describes a different target. A package-wide
    /// pass cannot honour that, because nothing on disk distinguishes a file extraction declined to
    /// rewrite from one it never saw; mtime cannot either, since a file written moments before the run
    /// looks exactly like one written during it.
    /// <para>
    /// No reference when the schema for that kind is absent -- a dangling <c>$schema</c> reads as a
    /// broken schema in an editor and is worse than none.
    /// </para>
    /// </remarks>
    private void WritePackageObject<T>(string filePath, T obj, string schemaKind) where T : class
    {
        var schemaFile = Path.Join(_productPath, ".json-schemas",
            $"{schemaKind}.{_platform.ToCanonicalString().ToLower()}.schema");
        if (obj != null && FileWrapper.GetFromFactory().Exists(schemaFile))
        {
            var property = obj.GetType().GetProperty("SchemaRef");
            property?.SetValue(obj,
                RepositoryHelper.BuildSchemaRef(_productPath, filePath, schemaKind, _platform));
        }
        JsonHelper.Write(filePath, obj);
    }

    private void ProcessOrphanedFiles()
    {
        var fullyExtracted = GetFullyExtractedFolders();
        if (fullyExtracted.Count == 0) return;

        var folderObjectTypes = GetFolderObjectTypes();
        var logsDir = Path.Combine(_templatePath, "Logs");

        var handler = new OrphanHandler();
        handler.ProcessOrphans(_folderIndexes, _platform, _orphanHandlingMode, fullyExtracted, logsDir, folderObjectTypes);
    }

    private HashSet<string> GetFullyExtractedFolders()
    {
        if (_objectsToCast.Length > 0) return [];

        var folders = new HashSet<string>();

        // Common
        if (_includeTables) folders.Add("Tables");
        if (_includeViews) folders.Add(ResolveFolderName("Views", ScriptObjectType.Views));

        switch (_platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                // #323: only SQL Server has SCHEMABINDING, so these two folders are declared here
                // rather than alongside the cross-platform Views entry above.
                if (_includeViews) folders.Add(ResolveFolderName("SchemaBound Views", ScriptObjectType.SchemaBoundViews));
                if (_includeUserDefinedFunctions) folders.Add(ResolveFolderName("SchemaBound Functions", ScriptObjectType.SchemaBoundFunctions));
                if (_includeSchemas) folders.Add(ResolveFolderName("Schemas", ScriptObjectType.Schemas));
                if (_includeUserDefinedTypes) folders.Add(ResolveFolderName("DataTypes", ScriptObjectType.DataTypes));
                if (_includeUserDefinedFunctions) folders.Add(ResolveFolderName("Functions", ScriptObjectType.Functions));
                if (_includeStoredProcedures) folders.Add(ResolveFolderName("Procedures", ScriptObjectType.Procedures));
                if (_includeTableTriggers) folders.Add(ResolveFolderName("Triggers", ScriptObjectType.Triggers));
                if (_includeFullTextCatalogs) folders.Add(ResolveFolderName("FullTextCatalogs", ScriptObjectType.FullTextCatalogs));
                if (_includeFullTextStopLists) folders.Add(ResolveFolderName("FullTextStopLists", ScriptObjectType.FullTextStopLists));
                if (_includeDDLTriggers) folders.Add(ResolveFolderName("DDLTriggers", ScriptObjectType.DDLTriggers));
                if (_includeXmlSchemaCollections) folders.Add(ResolveFolderName("XMLSchemaCollections", ScriptObjectType.XMLSchemaCollections));
                if (_includeIndexedViews) folders.Add("Indexed Views");
                if (_includeSequences) folders.Add(ResolveFolderName("Sequences", ScriptObjectType.Sequences));
                if (_includeSynonyms) folders.Add(ResolveFolderName("Synonyms", ScriptObjectType.Synonyms));
                if (_includeSecurityPolicies) folders.Add(ResolveFolderName("SecurityPolicies", ScriptObjectType.SecurityPolicies));
                break;

            case Platform.PostgreSQL:
                if (_includeSchemas) folders.Add(ResolveFolderName("Schemas", ScriptObjectType.Schemas));
                if (_includeDomainTypes) folders.Add(ResolveFolderName("Domain Types", ScriptObjectType.DomainTypes));
                if (_includeEnumTypes) folders.Add(ResolveFolderName("Enum Types", ScriptObjectType.EnumTypes));
                if (_includeCompositeTypes) folders.Add(ResolveFolderName("Composite Types", ScriptObjectType.CompositeTypes));
                if (_includeFunctions)
                {
                    folders.Add(ResolveFolderName("Functions", ScriptObjectType.Functions));
                    folders.Add(ResolveFolderName("Trigger Functions", ScriptObjectType.TriggerFunctions));
                    folders.Add(ResolveFolderName("Window Functions", ScriptObjectType.WindowFunctions));
                }
                if (_includeAggregates) folders.Add(ResolveFolderName("Aggregates", ScriptObjectType.Aggregates));
                if (_includeProcedures) folders.Add(ResolveFolderName("Procedures", ScriptObjectType.Procedures));
                if (_includeSequences) folders.Add(ResolveFolderName("Sequences", ScriptObjectType.Sequences));
                if (_includeRules) folders.Add(ResolveFolderName("Rules", ScriptObjectType.Rules));
                if (_includeTriggers) folders.Add(ResolveFolderName("Triggers", ScriptObjectType.Triggers));
                if (_includeMaterializedViews) folders.Add("Materialized Views");
                if (_includeCollations) folders.Add(ResolveFolderName("Collations", ScriptObjectType.Collations));
                if (_includePublications) folders.Add(ResolveFolderName("Publications", ScriptObjectType.Publications));
                if (_includeRangeTypes) folders.Add(ResolveFolderName("Range Types", ScriptObjectType.RangeTypes));
                if (_includeTextSearchConfigurations) folders.Add(ResolveFolderName("Text Search Configurations", ScriptObjectType.TextSearchConfigurations));
                if (_includeOperators) folders.Add(ResolveFolderName("Operators", ScriptObjectType.Operators));
                if (_includeCasts) folders.Add(ResolveFolderName("Casts", ScriptObjectType.Casts));
                if (_includeEventTriggers) folders.Add(ResolveFolderName("Event Triggers", ScriptObjectType.EventTriggers));
                break;

            case Platform.MySQL:
                if (_includeUserDefinedFunctions) folders.Add(ResolveFolderName("Functions", ScriptObjectType.Functions));
                if (_includeStoredProcedures) folders.Add(ResolveFolderName("Procedures", ScriptObjectType.Procedures));
                if (_includeTableTriggers) folders.Add(ResolveFolderName("Triggers", ScriptObjectType.Triggers));
                if (_includeEvents) folders.Add(ResolveFolderName("Events", ScriptObjectType.Events));
                if (_includeSequences) folders.Add(ResolveFolderName("Sequences", ScriptObjectType.Sequences)); // MariaDb-only; _includeSequences is gated in LoadShouldCastSettings
                if (_includePackages) folders.Add(ResolveFolderName("Packages", ScriptObjectType.Packages)); // MariaDb-only, likewise
                break;
        }

        return folders;
    }

    private Dictionary<string, ScriptObjectType> GetFolderObjectTypes()
    {
        var map = new Dictionary<string, ScriptObjectType>();
        foreach (var (folderName, _) in _folderIndexes)
        {
            var objectType = folderName switch
            {
                "Tables" => ScriptObjectType.None,
                "Indexed Views" => ScriptObjectType.None,
                "Materialized Views" => ScriptObjectType.None,
                _ => ScriptFolderTypeInference.InferFromFolderName(folderName)
            };
            map[folderName] = objectType;
        }
        return map;
    }

    private void CastDatabaseObjects(string targetDb)
    {
        switch (_platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                CastSqlServerObjects(targetDb);
                break;
            case Platform.PostgreSQL:
                CastPostgreSqlObjects(targetDb);
                break;
            case Platform.MySQL:
                CastMySqlObjects(targetDb);
                break;
            default:
                throw new Exception($"Unsupported platform: {_platform}");
        }
    }

    #region SQL Server Extraction

    private void CastSqlServerObjects(string targetDb)
    {
        using var connection = GetConnection(targetDb);
        try
        {
            using var command = connection.CreateCommand();

            // Resolve the model-extraction encoding from the detected source version, best-effort: below the
            // OPENJSON cliff (a pre-2016 binary / compat < 130), or when Source:CompatEncoding=legacy, extract
            // as XML; a genuinely undetectable version defaults to JSON (the modern path). Kept self-contained
            // here (not carried from PreFlightSourceVersion) so extraction picks the right encoding regardless
            // of call order; the strict "never work blind" version guard stays PreFlightSourceVersion's job.
            // The XML twins carry twin proc names (GenerateTableXml / GenerateIndexedViewXml), so kindling with
            // the encoding installs the right generators; the extraction sites below read _ingestEncoding.
            var sourceMajor = 0;
            using (var versionCmd = connection.CreateCommand())
            {
                var info = TargetVersionDetector.TryDetect(versionCmd, _platform, targetDb);
                _ingestEncoding = info == null
                    ? IngestEncoding.Json
                    : CompatEncoding.Select(
                        FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.SourceCompatEncoding],
                        info.CompatibilityLevel, info.ServerComparable);
                sourceMajor = info?.ServerComparable ?? 0;
                _sourceMajor = sourceMajor;
                _sourceReportsXmlCompression = _platform == Platform.SqlServer && ForgeKindler.ReportsXmlCompression(versionCmd);
            }

            _progressLog.Info("Kindling The Forge");
            // Bake the detected SOURCE major version into SchemaSmith.fn_ServerMajorVersion so the version-gated
            // compare/extraction helpers resolve the source version on a genuine pre-2016 binary (where the
            // former SESSION_CONTEXT transport is unavailable); 0 falls back to the server property on modern.
            ForgeKindler.KindleTheForge(command, _platform, encoding: _ingestEncoding, serverMajorVersion: sourceMajor,
                                        allowReadOnlyTarget: true);

            if (_includeTables) ExtractTableDefinitions(command, targetDb);
            // Always Encrypted arrived in 2016 (major 13); below it there are no keys to read.
            if (_includeTables && !_isSchemaTemplate && sourceMajor is 0 or >= 13) ScriptSqlServerAlwaysEncryptedKeys(command, targetDb);
            if (_includeTables && !_isSchemaTemplate) ScriptSqlServerPartitionSchemes(command, targetDb);
            if (_includeSchemas) ScriptSqlServerSchemas(command);
            if (_includeUserDefinedTypes) ScriptSqlServerUserDefinedTypes(command);
            if (_includeUserDefinedFunctions) ScriptSqlServerFunctions(command);
            if (_includeViews) ScriptSqlServerViews(command);
            if (_includeStoredProcedures) ScriptSqlServerProcedures(command);
            if (_includeTableTriggers) ScriptSqlServerTableTriggers(command);
            if (_includeFullTextCatalogs) ScriptSqlServerFullTextCatalogs(command);
            if (_includeFullTextStopLists) ScriptSqlServerFullTextStopLists(command);
            if (_includeDDLTriggers) ScriptSqlServerDDLTriggers(command);
            if (_includeXmlSchemaCollections) ScriptSqlServerXmlSchemaCollections(command);
            if (_includeIndexedViews) CastSqlServerIndexedViews(command);
            // Sequences arrived in SQL Server 2012 (major 11); 2008 R2 has no sys.sequences, so there is nothing to cast.
            if (_includeSequences && sourceMajor is 0 or >= 11) ScriptSqlServerSequences(command);
            if (_includeSynonyms) ScriptSqlServerSynonyms(command);
            // Row-level security arrived in SQL Server 2016 (major 13).
            if (_includeSecurityPolicies && sourceMajor is 0 or >= 13) ScriptSqlServerSecurityPolicies(command);
        }
        finally
        {
            connection.Close();
        }
    }

    private void ScriptSqlServerSchemas(IDbCommand command)
    {
        _progressLog.Info("Casting Schema Scripts");
        var castPath = GetCastPath(ScriptObjectType.Schemas, "Schemas");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name, s.schema_id
  FROM sys.schemas s
 WHERE s.schema_id > 4
   AND s.name NOT LIKE 'db[_]%'
   AND s.name NOT LIKE '%\%'
   AND s.name <> 'SchemaSmith'
   AND s.principal_id IS NOT NULL
 ORDER BY s.name";

        var schemas = new List<(string Name, int SchemaId)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var schemaId = reader.GetInt32(1);
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
                schemas.Add((name, schemaId));
            }
        }

        foreach (var (name, _) in schemas)
        {
            var escapedName = EscapeSql(name);
            var script = $"IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'{escapedName}')\r\n" +
                         $"EXEC sys.sp_executesql N'CREATE SCHEMA [{EscapeSql(Identifier.EscapeDelimited(name, Platform.SqlServer))}]'\r\n";

            var extProps = GetExtendedProperties(command, "SCHEMA", name);
            if (extProps.Length > 0)
                script += "\r\n" + extProps;

            var fileName = ResolveOutputPath(castPath, EncodeFileName(name, ".sql"));
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.Schemas++;
        }
    }

    internal string GetExtendedProperties(IDbCommand command, string level0Type, string level0Name,
        string level1Type = null, string level1Name = null,
        string level2Type = null, string level2Name = null)
    {
        var fnLevel1Type = level1Type != null ? $"N'{level1Type}'" : "NULL";
        var fnLevel1Name = level1Type != null ? $"N'{EscapeSql(level1Name)}'" : "NULL";
        var fnLevel2Type = level2Type != null ? $"N'{level2Type}'" : "NULL";
        var fnLevel2Name = level2Type != null ? $"N'{EscapeSql(level2Name)}'" : "NULL";

        command.CommandText = $@"
SELECT name, CAST(value AS NVARCHAR(MAX)) AS value
  FROM sys.fn_listextendedproperty(NULL, N'{level0Type}', N'{EscapeSql(level0Name)}', {fnLevel1Type}, {fnLevel1Name}, {fnLevel2Type}, {fnLevel2Name})
 ORDER BY name";

        var properties = new List<(string Name, string Value)>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!InternalExtendedProperties.IsInternal(name))
                    properties.Add((name, reader.GetString(1)));
            }
        }
        catch (Exception)
        {
            // fn_listextendedproperty raises an error when the referenced object doesn't exist
        }

        if (properties.Count == 0) return "";

        var spLevel0 = $" , @level0type=N'{level0Type}',@level0name=N'{EscapeSql(level0Name)}'";
        var spLevel1 = level1Type != null ? $" , @level1type=N'{level1Type}',@level1name=N'{EscapeSql(level1Name)}'" : "";
        var spLevel2 = level2Type != null ? $" , @level2type=N'{level2Type}',@level2name=N'{EscapeSql(level2Name)}'" : "";

        var lines = new List<string>();
        foreach (var (propName, propValue) in properties)
        {
            var escapedValue = propValue.Replace("'", "''");
            var escapedPropName = EscapeSql(propName);
            var fnArgs = $"N'{escapedPropName}' , N'{level0Type}',N'{EscapeSql(level0Name)}', {fnLevel1Type},{fnLevel1Name}, {fnLevel2Type},{fnLevel2Name}";
            var addArgs = $"@name=N'{escapedPropName}', @value=N'{escapedValue}'{spLevel0}{spLevel1}{spLevel2}";
            var updateArgs = $"@name=N'{escapedPropName}', @value=N'{escapedValue}'{spLevel0}{spLevel1}{spLevel2}";

            lines.Add(
                $"IF NOT EXISTS (SELECT * FROM sys.fn_listextendedproperty({fnArgs}))\r\n" +
                $"\tEXEC sys.sp_addextendedproperty {addArgs}\r\n" +
                $"ELSE\r\n" +
                $"BEGIN\r\n" +
                $"\tEXEC sys.sp_updateextendedproperty {updateArgs}\r\n" +
                $"END\r\n");
        }

        return string.Join("", lines);
    }

    private void ScriptSqlServerUserDefinedTypes(IDbCommand command)
    {
        _progressLog.Info("Casting User Defined Types");
        var castPath = GetCastPath(ScriptObjectType.DataTypes, "DataTypes");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
        ScriptSqlServerAliasTypes(command, castPath);
        ScriptSqlServerTableTypes(command, castPath);
    }

    private void ScriptSqlServerAliasTypes(IDbCommand command, string castPath)
    {
        command.CommandText = @"
SELECT s.name AS SchemaName, t.name AS TypeName,
       TYPE_NAME(t.system_type_id) AS BaseTypeName,
       t.max_length, t.precision, t.scale, t.is_nullable
  FROM sys.types t
  JOIN sys.schemas s ON t.schema_id = s.schema_id
 WHERE t.is_user_defined = 1
   AND t.is_table_type = 0
   AND t.is_assembly_type = 0
 ORDER BY s.name, t.name";

        var types = new List<(string Schema, string Name, string BaseType, short MaxLength, byte Precision, byte Scale, bool IsNullable)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                types.Add((schema, name, reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6)));
            }
        }

        foreach (var (schema, name, baseType, maxLength, precision, scale, isNullable) in types)
        {
            var typeSpec = FormatBaseType(baseType, maxLength, precision, scale);
            var nullSpec = isNullable ? "NULL" : "NOT NULL";
            var script = $"IF NOT EXISTS (SELECT * FROM sys.types st JOIN sys.schemas ss ON st.schema_id = ss.schema_id WHERE st.name = N'{EscapeSql(name)}' AND ss.name = N'{EscapeSql(schema)}')\r\n" +
                         $"CREATE TYPE [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}] FROM {typeSpec} {nullSpec}";

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.DataTypes++;
        }
    }

    private void ScriptSqlServerTableTypes(IDbCommand command, string castPath)
    {
        // Every clause the type can carry: identity, computed, DEFAULT, COLLATE, named indexes (2014+) with HASH,
        // INCLUDE and a filter, IGNORE_DUP_KEY and MEMORY_OPTIMIZED. The memory-optimized column and sys.hash_indexes
        // do not exist before 2014, so they are read only there.
        var memoryOptimized = _sourceMajor >= 12;
        command.CommandText = $@"
SELECT s.name AS SchemaName, tt.name AS TypeName, tt.type_table_object_id,
       {(memoryOptimized ? "tt.is_memory_optimized" : "CONVERT(BIT, 0)")} AS IsMemoryOptimized,
       CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS DatabaseCollation
  FROM sys.table_types tt
  JOIN sys.schemas s ON tt.schema_id = s.schema_id
 WHERE tt.is_user_defined = 1
 ORDER BY s.name, tt.name";

        var tableTypes = new List<(string Schema, string Name, int ObjectId, bool IsMemoryOptimized, string DatabaseCollation)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                tableTypes.Add((schema, name, reader.GetInt32(2), reader.GetBoolean(3), reader.IsDBNull(4) ? "" : reader.GetString(4)));
            }
        }

        foreach (var (schema, name, objectId, isMemoryOptimized, databaseCollation) in tableTypes)
        {
            command.CommandText = $@"
SELECT c.name, TYPE_NAME(c.user_type_id) AS TypeName,
       c.max_length, c.precision, c.scale, c.is_nullable,
       c.is_identity, c.is_computed,
       ts.name AS UserTypeName, tss.name AS UserTypeSchema,
       c.collation_name,
       CONVERT(VARCHAR(50), ic.seed_value) AS Seed, CONVERT(VARCHAR(50), ic.increment_value) AS Increment,
       cc.definition AS ComputedDefinition, cc.is_persisted,
       dc.definition AS DefaultDefinition
  FROM sys.columns c
  LEFT JOIN sys.types ts ON c.user_type_id = ts.user_type_id AND ts.is_user_defined = 1
  LEFT JOIN sys.schemas tss ON ts.schema_id = tss.schema_id
  LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
  LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
  LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
 WHERE c.object_id = {objectId}
 ORDER BY c.column_id";

            var columnLines = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = $"[{Identifier.EscapeDelimited(reader.GetString(0), Platform.SqlServer)}]";
                    if (reader.GetBoolean(7))
                    {
                        columnLines.Add($"\t{columnName} AS {reader.GetString(13)}{(reader.GetBoolean(14) ? " PERSISTED" : "")}");
                        continue;
                    }
                    var typeSpec = reader.IsDBNull(8)
                        ? FormatBaseType(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4))
                        : $"[{Identifier.EscapeDelimited(reader.GetString(9), Platform.SqlServer)}].[{Identifier.EscapeDelimited(reader.GetString(8), Platform.SqlServer)}]";
                    var collation = !reader.IsDBNull(10) && !reader.GetString(10).Equals(databaseCollation, StringComparison.OrdinalIgnoreCase)
                        ? $" COLLATE {reader.GetString(10)}" : "";
                    var nullability = reader.GetBoolean(5) ? " NULL" : " NOT NULL";
                    var defaultSpec = reader.IsDBNull(15) ? "" : $" DEFAULT {reader.GetString(15)}";
                    var identity = reader.GetBoolean(6) ? $" IDENTITY({reader.GetString(11)}, {reader.GetString(12)})" : "";
                    columnLines.Add($"\t{columnName} {typeSpec}{collation}{nullability}{defaultSpec}{identity}");
                }
            }

            command.CommandText = $@"
SELECT i.name, i.index_id, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.ignore_dup_key,
       i.filter_definition, {(memoryOptimized ? "hi.bucket_count" : "CONVERT(INT, NULL)")} AS BucketCount,
       ic.is_included_column, c.name AS ColumnName, ic.is_descending_key
  FROM sys.indexes i
  JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
  JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
  {(memoryOptimized ? "LEFT JOIN sys.hash_indexes hi ON hi.object_id = i.object_id AND hi.index_id = i.index_id" : "")}
 WHERE i.object_id = {objectId}
   AND i.type > 0
 ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal";

            // A PRIMARY KEY or UNIQUE constraint on a table type cannot be named, so only a plain index keeps its name.
            var indexes = new List<(int Id, string Name, string TypeDesc, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint, bool IgnoreDupKey, string Filter, int? BucketCount, List<string> Keys, List<string> Includes)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var indexId = reader.GetInt32(1);
                    if (indexes.Count == 0 || indexes[^1].Id != indexId)
                        indexes.Add((indexId, reader.GetBoolean(4) || reader.GetBoolean(5) || reader.IsDBNull(0) ? null : reader.GetString(0),
                            reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), [], []));
                    var column = $"[{Identifier.EscapeDelimited(reader.GetString(10), Platform.SqlServer)}]";
                    if (reader.GetBoolean(9)) indexes[^1].Includes.Add(column);
                    // A hash index takes no sort order: ASC or DESC on its key is refused.
                    else indexes[^1].Keys.Add(column + (indexes[^1].TypeDesc.Contains("HASH", StringComparison.OrdinalIgnoreCase) ? "" : reader.GetBoolean(11) ? " DESC" : " ASC"));
                }
            }

            command.CommandText = $@"
SELECT cc.definition
  FROM sys.check_constraints cc
 WHERE cc.parent_object_id = {objectId}
 ORDER BY cc.name";
            var checks = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) checks.Add(reader.GetString(0));
            }

            var constraintLines = new List<string>();
            foreach (var index in indexes)
            {
                var keys = string.Join(", ", index.Keys);
                var with = new List<string>();
                if (index.IgnoreDupKey) with.Add("IGNORE_DUP_KEY = ON");
                if (index.BucketCount is > 0) with.Add($"BUCKET_COUNT = {index.BucketCount}");
                var withClause = with.Count > 0 ? $" WITH ({string.Join(", ", with)})" : "";
                if (index.IsPrimaryKey)
                    constraintLines.Add($"\tPRIMARY KEY {index.TypeDesc} ({keys}){withClause}");
                else if (index.IsUniqueConstraint)
                    constraintLines.Add($"\tUNIQUE {index.TypeDesc} ({keys}){withClause}");
                else if (index.Name == null)
                    constraintLines.Add($"\t{(index.IsUnique ? "UNIQUE " : "")}{index.TypeDesc} ({keys}){withClause}");
                else
                {
                    var include = index.Includes.Count > 0 ? $" INCLUDE ({string.Join(", ", index.Includes)})" : "";
                    var filter = index.Filter != null ? $" WHERE {index.Filter}" : "";
                    constraintLines.Add($"\tINDEX [{Identifier.EscapeDelimited(index.Name, Platform.SqlServer)}] {(index.IsUnique ? "UNIQUE " : "")}{index.TypeDesc} ({keys}){include}{filter}{withClause}");
                }
            }
            foreach (var check in checks)
                constraintLines.Add($"\tCHECK {check}");

            var body = string.Join(",\r\n", columnLines.Concat(constraintLines));
            var createScript = $"CREATE TYPE [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}] AS TABLE(\r\n{body}\r\n)" +
                               (isMemoryOptimized ? "\r\nWITH (MEMORY_OPTIMIZED = ON)" : "");

            var script = $"IF NOT EXISTS (SELECT * FROM sys.types st JOIN sys.schemas ss ON st.schema_id = ss.schema_id WHERE st.name = N'{EscapeSql(name)}' AND ss.name = N'{EscapeSql(schema)}')\r\n" +
                         createScript;

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.DataTypes++;
        }
    }

    private void ScriptSqlServerSequences(IDbCommand command)
    {
        _progressLog.Info("Casting Sequences");
        var castPath = GetCastPath(ScriptObjectType.Sequences, "Sequences");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        // The values are sql_variant and a DECIMAL(38, 0) sequence's bounds overflow the driver's decimal, so they are
        // converted to text on the server. The type keeps its precision and an alias type keeps its name; CACHE is
        // written when it is explicit (NO CACHE, or a size), and the server default otherwise.
        command.CommandText = @"
SELECT s.name AS SchemaName, sq.name AS SequenceName,
       CASE WHEN t.is_user_defined = 1 THEN QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)
            WHEN TYPE_NAME(sq.system_type_id) IN ('decimal', 'numeric') THEN TYPE_NAME(sq.system_type_id) + '(' + CONVERT(VARCHAR(5), sq.precision) + ')'
            ELSE TYPE_NAME(sq.system_type_id) END AS DataType,
       CONVERT(VARCHAR(50), sq.start_value) AS StartValue, CONVERT(VARCHAR(50), sq.increment) AS Increment,
       CONVERT(VARCHAR(50), sq.minimum_value) AS MinValue, CONVERT(VARCHAR(50), sq.maximum_value) AS MaxValue,
       sq.is_cycling, sq.is_cached, sq.cache_size
  FROM sys.sequences sq
  JOIN sys.schemas s ON sq.schema_id = s.schema_id
  JOIN sys.types t ON t.user_type_id = sq.user_type_id
 ORDER BY s.name, sq.name";

        var sequences = new List<(string Schema, string Name, string DataType, string StartValue, string Increment, string MinValue, string MaxValue, bool IsCycling, bool IsCached, int? CacheSize)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                sequences.Add((schema, name, reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetBoolean(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetInt32(9)));
            }
        }

        foreach (var (schema, name, dataType, startValue, increment, minValue, maxValue, isCycling, isCached, cacheSize) in sequences)
        {
            var cache = !isCached ? "\r\n\tNO CACHE" : cacheSize is > 0 ? $"\r\n\tCACHE {cacheSize}" : "";
            var createScript =
                $"CREATE SEQUENCE [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}]\r\n" +
                $"\tAS {dataType}\r\n" +
                $"\tSTART WITH {startValue}\r\n" +
                $"\tINCREMENT BY {increment}\r\n" +
                $"\tMINVALUE {minValue}\r\n" +
                $"\tMAXVALUE {maxValue}\r\n" +
                $"\t{(isCycling ? "CYCLE" : "NO CYCLE")}{cache}";

            // CREATE SEQUENCE has no IF NOT EXISTS form on SQL Server (unlike PostgreSQL); guard with the
            // same catalog-existence check the Schemas/DataTypes folders use above.
            var script = $"IF NOT EXISTS (SELECT * FROM sys.sequences sq JOIN sys.schemas ss ON sq.schema_id = ss.schema_id WHERE sq.name = N'{EscapeSql(name)}' AND ss.name = N'{EscapeSql(schema)}')\r\n" +
                         createScript;

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.Sequences++;
        }
    }

    // The catalog's own description of a policy, used twice so the two can only agree: on the source it becomes the
    // expected value written into the script, and on the target the script recomputes it to decide whether to re-create.
    internal const string SecurityPolicySignatureSql =
        "SELECT CONVERT(NVARCHAR(1), p.is_enabled) + CONVERT(NVARCHAR(1), p.is_schema_bound) + CONVERT(NVARCHAR(1), p.is_not_for_replication) + " +
        "ISNULL((SELECT N'|' + sp.predicate_type_desc + N':' + ISNULL(sp.operation_desc, N'') + N':' + QUOTENAME(OBJECT_SCHEMA_NAME(sp.target_object_id)) + N'.' + " +
        "QUOTENAME(OBJECT_NAME(sp.target_object_id)) + N':' + sp.predicate_definition FROM sys.security_predicates sp WHERE sp.[object_id] = p.[object_id] " +
        "ORDER BY OBJECT_SCHEMA_NAME(sp.target_object_id), OBJECT_NAME(sp.target_object_id), sp.predicate_type_desc, sp.operation_desc, sp.predicate_definition " +
        "FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), N'') FROM sys.security_policies p WHERE p.[object_id] = OBJECT_ID(@policy)";

    // Row-level security policies, one script each. Creating one where it is missing is not enough, since a changed
    // predicate would never apply, and re-creating it on every deploy would switch the security off for a moment.
    // So the script compares the live policy with the extracted one and re-creates it only when they differ, inside a
    // transaction, so a reader never sees the table unprotected.
    private void ScriptSqlServerSecurityPolicies(IDbCommand command)
    {
        _progressLog.Info("Casting Security Policies");
        var castPath = GetCastPath(ScriptObjectType.SecurityPolicies, "SecurityPolicies");

        command.CommandText = @"
SELECT SCHEMA_NAME(p.[schema_id]), p.[name], p.is_enabled, p.is_schema_bound, p.is_not_for_replication,
       sp.predicate_type_desc, sp.operation_desc, OBJECT_SCHEMA_NAME(sp.target_object_id), OBJECT_NAME(sp.target_object_id), sp.predicate_definition
  FROM sys.security_policies p
  LEFT JOIN sys.security_predicates sp ON sp.[object_id] = p.[object_id]
 ORDER BY SCHEMA_NAME(p.[schema_id]), p.[name], sp.security_predicate_id";
        var rows = new List<(string Schema, string Name, bool Enabled, bool SchemaBound, bool NotForReplication, string Type, string Operation, string TargetSchema, string Target, string Definition)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
                          reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                          reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));

        foreach (var policy in rows.GroupBy(r => (r.Schema, r.Name))
                     .Where(g => ShouldExtractFromSchema(g.Key.Schema))
                     .Where(g => _objectsToCast.Length == 0 || _objectsToCast.Contains(g.Key.Name.ToLower()) || _objectsToCast.Contains($"{g.Key.Schema}.{g.Key.Name}".ToLower())))
        {
            var qualified = $"[{Identifier.EscapeDelimited(policy.Key.Schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(policy.Key.Name, Platform.SqlServer)}]";
            command.CommandText = "DECLARE @policy NVARCHAR(600) = @p; " + SecurityPolicySignatureSql;
            command.Parameters.Clear();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p";
            parameter.Value = qualified;
            command.Parameters.Add(parameter);
            var signature = command.ExecuteScalar()?.ToString() ?? "";
            command.Parameters.Clear();

            var first = policy.First();
            var predicates = string.Join(",\r\n  ", policy.Where(p => p.Type != null).Select(p =>
                $"ADD {p.Type} PREDICATE {StripOuterParentheses(p.Definition)} ON [{Identifier.EscapeDelimited(p.TargetSchema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(p.Target, Platform.SqlServer)}]" +
                (string.IsNullOrEmpty(p.Operation) ? "" : " " + p.Operation)));
            var create = $"CREATE SECURITY POLICY {qualified}\r\n  {predicates}\r\n  WITH (STATE = {(first.Enabled ? "ON" : "OFF")}, SCHEMABINDING = {(first.SchemaBound ? "ON" : "OFF")})" +
                         (first.NotForReplication ? " NOT FOR REPLICATION" : "");

            // The signature query's FOR XML .value() fails under QUOTED_IDENTIFIER OFF, which a deploy's session keeps from
            // a module scripted that way, so the script sets it, as a module script does.
            var script =
                "SET QUOTED_IDENTIFIER ON\r\n" +
                "IF OBJECT_ID('sys.security_policies') IS NULL\r\n" +
                $"  RAISERROR('  Security policy {EscapeSql(qualified).Replace("%", "%%")} skipped: row-level security needs SQL Server 2016', 10, 1) WITH NOWAIT\r\n" +
                "ELSE\r\nBEGIN\r\n" +
                $"  DECLARE @policy NVARCHAR(600) = N'{EscapeSql(qualified)}', @v_Live NVARCHAR(MAX)\r\n" +
                $"  SET @v_Live = ({SecurityPolicySignatureSql})\r\n" +
                $"  IF @v_Live IS NULL OR @v_Live <> N'{EscapeSql(signature)}'\r\n" +
                "  BEGIN\r\n" +
                "  BEGIN TRY\r\n" +
                "    BEGIN TRANSACTION\r\n" +
                $"    IF @v_Live IS NOT NULL EXEC(N'DROP SECURITY POLICY {EscapeSql(qualified)}')\r\n" +
                $"    EXEC(N'{EscapeSql(create)}')\r\n" +
                "    COMMIT\r\n" +
                "  END TRY\r\n" +
                "  BEGIN CATCH\r\n" +
                "    IF @@TRANCOUNT > 0 ROLLBACK\r\n" +
                "    DECLARE @v_Error NVARCHAR(2048) = ERROR_MESSAGE()\r\n" +
                "    RAISERROR(@v_Error, 16, 1)\r\n" +
                "  END CATCH\r\n" +
                "  END\r\n" +
                "END\r\n";

            if (_stats.SecurityPolicies == 0) DeclareFolderWrittenOnUse(ScriptObjectType.SecurityPolicies, castPath);
            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(policy.Key.Schema, policy.Key.Name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.SecurityPolicies++;
        }
    }

    // A folder in FoldersWrittenOnUse joins the template's ScriptFolders with the first object extracted into it.
    private void DeclareFolderWrittenOnUse(ScriptObjectType type, string castPath)
    {
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
        var templateFile = Path.Join(_templatePath, "Template.json");
        if (!FileWrapper.GetFromFactory().Exists(templateFile)) return;
        var template = JsonHelper.Load<Template>(templateFile);
        if (template.ScriptFolders.Any(f => f.ObjectType == type)) return;
        var folder = Template.GetDefaultTemplateFolders(_platform, _isSchemaTemplate).First(f => f.ObjectType == type);
        template.ScriptFolders.Add(new TemplateFolder { FolderPath = ResolveFolderName(folder.FolderPath, type), QuenchSlot = folder.QuenchSlot, ObjectType = type });
        WritePackageObject(templateFile, template, "templates");
    }

    // The catalog wraps each predicate in one pair of parentheses that CREATE SECURITY POLICY does not take.
    internal static string StripOuterParentheses(string definition)
    {
        var text = definition.Trim();
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')') return text;
        var depth = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            if (depth == 0) return text;
        }
        return text[1..^1].Trim();
    }

    private void ScriptSqlServerSynonyms(IDbCommand command)
    {
        _progressLog.Info("Casting Synonyms");
        var castPath = GetCastPath(ScriptObjectType.Synonyms, "Synonyms");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name AS SchemaName, sy.name AS SynonymName, sy.base_object_name
  FROM sys.synonyms sy
  JOIN sys.schemas s ON sy.schema_id = s.schema_id
 ORDER BY s.name, sy.name";

        var synonyms = new List<(string Schema, string Name, string BaseObjectName)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                synonyms.Add((schema, name, reader.GetString(2)));
            }
        }

        foreach (var (schema, name, baseObjectName) in synonyms)
        {
            // CREATE SYNONYM has no IF NOT EXISTS form and must be the first statement in its batch, so
            // (like Schemas above) the guarded create goes through EXEC sys.sp_executesql dynamic SQL
            // rather than a plain IF block.
            var script = $"IF NOT EXISTS (SELECT * FROM sys.synonyms sy JOIN sys.schemas ss ON sy.schema_id = ss.schema_id WHERE sy.name = N'{EscapeSql(name)}' AND ss.name = N'{EscapeSql(schema)}')\r\n" +
                         $"EXEC sys.sp_executesql N'CREATE SYNONYM [{EscapeSql(Identifier.EscapeDelimited(schema, Platform.SqlServer))}].[{EscapeSql(Identifier.EscapeDelimited(name, Platform.SqlServer))}] FOR {EscapeSql(baseObjectName)}'\r\n";

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.Synonyms++;
        }
    }

    private void ScriptSqlServerFunctions(IDbCommand command)
    {
        _progressLog.Info("Casting Function Scripts");
        var castPath = GetCastPath(ScriptObjectType.Functions, "Functions");
        // #323: a SCHEMABINDING module goes to its own folder on the AfterTablesObjects slot,
        // so a column change it blocks can drop it and this pass can put it back afterwards.
        var sbCastPath = GetCastPath(ScriptObjectType.SchemaBoundFunctions, "SchemaBound Functions");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, CONVERT(BIT, ISNULL(sm.is_schema_bound, 0)) AS IsSchemaBound
  FROM sys.objects o
  JOIN sys.schemas s ON o.schema_id = s.schema_id
  LEFT JOIN sys.sql_modules sm ON o.object_id = sm.object_id
 WHERE o.type IN ('FN', 'IF', 'TF')
   AND o.is_ms_shipped = 0
   AND s.name <> 'SchemaSmith'
 ORDER BY s.name, o.name";

        var functions = new List<(string Schema, string Name, bool SchemaBound)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                functions.Add((schema, name, reader.GetBoolean(2)));
            }
        }

        foreach (var (schema, name, schemaBound) in functions)
        {
            var sql = ScriptSqlServerProgrammableObject(command, schema, name, "FUNCTION");
            if (sql == null) continue;

            if (schemaBound) DirectoryWrapper.GetFromFactory().CreateDirectory(sbCastPath);
            var fileName = ResolveOutputPath(schemaBound ? sbCastPath : castPath, EncodeObjectFileName(schema, name, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { _stats.Functions++; continue; }
            sql = RewriteSqlBodyForSchemaTemplate(sql, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, sql);
            ValidateAndHandleScript(command.Connection, fileName, sql, schemaBound ? ScriptObjectType.SchemaBoundFunctions : ScriptObjectType.Functions);
            _stats.Functions++;
        }
    }

    private void ScriptSqlServerViews(IDbCommand command)
    {
        _progressLog.Info("Casting View Scripts");
        var castPath = GetCastPath(ScriptObjectType.Views, "Views");
        // #323: a SCHEMABINDING module goes to its own folder on the AfterTablesObjects slot,
        // so a column change it blocks can drop it and this pass can put it back afterwards.
        var sbCastPath = GetCastPath(ScriptObjectType.SchemaBoundViews, "SchemaBound Views");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, CONVERT(BIT, ISNULL(sm.is_schema_bound, 0)) AS IsSchemaBound
  FROM sys.objects o
  JOIN sys.schemas s ON o.schema_id = s.schema_id
  LEFT JOIN sys.sql_modules sm ON o.object_id = sm.object_id
 WHERE o.type = 'V'
   AND o.is_ms_shipped = 0
   AND s.name <> 'SchemaSmith'
   AND o.name NOT LIKE 'MSSQL[_]DroppedLedgerView[_]%'" + LedgerViewFilter() +
            // An indexed view is extracted as Indexed Views JSON; scripting it here as well created it twice.
            (_includeIndexedViews ? @"
   AND ISNULL(OBJECTPROPERTY(o.object_id, 'IsIndexed'), 0) = 0" : "") + @"
 ORDER BY s.name, o.name";

        var views = new List<(string Schema, string Name, bool SchemaBound)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                views.Add((schema, name, reader.GetBoolean(2)));
            }
        }

        foreach (var (schema, name, schemaBound) in views)
        {
            var sql = ScriptSqlServerProgrammableObject(command, schema, name, "VIEW");
            if (sql == null) continue;

            if (schemaBound) DirectoryWrapper.GetFromFactory().CreateDirectory(sbCastPath);
            var fileName = ResolveOutputPath(schemaBound ? sbCastPath : castPath, EncodeObjectFileName(schema, name, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { _stats.Views++; continue; }
            sql = RewriteSqlBodyForSchemaTemplate(sql, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, sql);
            ValidateAndHandleScript(command.Connection, fileName, sql, schemaBound ? ScriptObjectType.SchemaBoundViews : ScriptObjectType.Views);
            _stats.Views++;
        }
    }

    private void ScriptSqlServerProcedures(IDbCommand command)
    {
        _progressLog.Info("Casting Stored Procedure Scripts");
        var castPath = GetCastPath(ScriptObjectType.Procedures, "Procedures");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name AS SchemaName, o.name AS ObjectName
  FROM sys.objects o
  JOIN sys.schemas s ON o.schema_id = s.schema_id
 WHERE o.type = 'P'
   AND o.is_ms_shipped = 0
   AND s.name <> 'SchemaSmith'
 ORDER BY s.name, o.name";

        var procedures = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                procedures.Add((schema, name));
            }
        }

        foreach (var (schema, name) in procedures)
        {
            var sql = ScriptSqlServerProgrammableObject(command, schema, name, "PROCEDURE");
            if (sql == null) continue;

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { _stats.Procedures++; continue; }
            sql = RewriteSqlBodyForSchemaTemplate(sql, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, sql);
            ValidateAndHandleScript(command.Connection, fileName, sql, ScriptObjectType.Procedures);
            _stats.Procedures++;
        }

        WarnNumberedProcedures(command, procedures);
    }

    // A numbered procedure (dbo.p;2 and on, deprecated since 2008) shares its name with dbo.p, whose definition is the
    // only one SchemaTongs reads, so each later number would be lost without a word.
    private void WarnNumberedProcedures(IDbCommand command, List<(string Schema, string Name)> procedures)
    {
        command.CommandText = @"
SELECT OBJECT_SCHEMA_NAME(np.[object_id]), OBJECT_NAME(np.[object_id]), np.procedure_number
  FROM sys.numbered_procedures np
 WHERE np.procedure_number > 1
 ORDER BY 1, 2, 3";
        var numbered = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (procedures.Contains((reader.GetString(0), reader.GetString(1))))
                    numbered.Add($"{reader.GetString(0)}.{reader.GetString(1)};{reader.GetInt16(2)}");
        foreach (var procedure in numbered)
        {
            _progressLog.Warn($"  WARNING: {procedure} is a numbered procedure, which a package cannot declare; only its ;1 is extracted");
            _stats.SkippedObjects++;
        }
    }

    // FileTables (2012) and external tables (2016) are tables SQL Server builds from something outside the table
    // definition, a FILESTREAM share or an external data source, so a table file of their columns would deploy as an
    // ordinary table. Each is named, counted as skipped, and left out.
    private void WarnSqlServerTablesNotExtracted(IDbCommand command)
    {
        if (_sourceMajor is > 0 and < 11) return;
        command.CommandText = "SELECT SCHEMA_NAME([schema_id]), [name], 'a FileTable' FROM sys.tables WHERE is_filetable = 1" +
                              (_sourceMajor is 0 or >= 13 ? " UNION ALL SELECT SCHEMA_NAME([schema_id]), [name], 'an external table' FROM sys.tables WHERE is_external = 1" : "") +
                              " ORDER BY 1, 2";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (schema, name, kind) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!ShouldExtractFromSchema(schema)) continue;
            _progressLog.Warn($"  WARNING: {schema}.{name} is {kind}, which a package cannot declare; not extracted");
            _stats.SkippedObjects++;
        }
    }

    private void ScriptSqlServerTableTriggers(IDbCommand command)
    {
        _progressLog.Info("Casting Table Trigger Scripts");
        var castPath = GetCastPath(ScriptObjectType.Triggers, "Triggers");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT s.name AS TableSchema, pt.name AS TableName, tr.name AS TriggerName
  FROM sys.triggers tr
  JOIN sys.objects pt ON tr.parent_id = pt.object_id
  JOIN sys.schemas s ON pt.schema_id = s.schema_id
 WHERE tr.parent_class = 1
   AND pt.is_ms_shipped = 0
   AND s.name <> 'SchemaSmith'
 ORDER BY s.name, pt.name, tr.name";

        var triggers = new List<(string TableSchema, string TableName, string TriggerName)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var triggerName = reader.GetString(2);
                var schemaName = reader.GetString(0);
                if (!ShouldExtractFromSchema(schemaName)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(triggerName.ToLower())) continue;
                triggers.Add((schemaName, reader.GetString(1), triggerName));
            }
        }

        foreach (var (tableSchema, tableName, triggerName) in triggers)
        {
            var sql = ScriptSqlServerProgrammableObject(command, tableSchema, triggerName, "TRIGGER", "TRIGGER", tableName);
            if (sql == null) continue;
            sql += SqlServerTriggerTail(command, tableSchema, tableName, triggerName);

            var escapedSchema = Regex.Escape(tableSchema);
            var escapedTable = Regex.Escape(tableName);
            var tablePattern = $@"(?<=\bON\s+)\[?{escapedSchema}\]?\.\[?{escapedTable}\]?";
            sql = Regex.Replace(sql, tablePattern, $"[{Identifier.EscapeDelimited(tableSchema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(tableName, Platform.SqlServer)}]", RegexOptions.IgnoreCase);

            // Schema-template mode: drop the schema-qualified prefix in the filename.
            var encodedFile = _isSchemaTemplate
                ? $"{FileNameEncoder.Encode(tableName)}.{FileNameEncoder.Encode(triggerName)}.sql"
                : $"{FileNameEncoder.Encode(tableSchema)}.{FileNameEncoder.Encode(tableName)}.{FileNameEncoder.Encode(triggerName)}.sql";
            var fileName = ResolveOutputPath(castPath, encodedFile);
            if (ShouldSkipKnownBadScript(fileName)) { _stats.Triggers++; continue; }
            sql = RewriteSqlBodyForSchemaTemplate(sql, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, sql);
            ValidateAndHandleScript(command.Connection, fileName, sql, ScriptObjectType.Triggers);
            _stats.Triggers++;
        }
    }

    internal const string AlwaysEncryptedKeysScript = "AlwaysEncryptedKeys.sql";

    // The column master and column encryption keys an Always Encrypted column needs before its table can be created.
    // Neither is modelled, so they go to a generated prerequisite script that creates only what is missing and runs
    // ahead of the tables. Rewritten in full on every extraction; removed when the database no longer has keys.
    private void ScriptSqlServerAlwaysEncryptedKeys(IDbCommand command, string targetDb)
    {
        var masterKeys = new List<(string Name, string Provider, string Path, bool Enclave, byte[] Signature)>();
        command.CommandText = @"
IF COL_LENGTH('sys.column_master_keys', 'allow_enclave_computations') IS NOT NULL
  EXEC sp_executesql N'SELECT [name], key_store_provider_name, key_path, allow_enclave_computations, [signature] FROM sys.column_master_keys ORDER BY [name]'
ELSE
  SELECT [name], key_store_provider_name, key_path, CAST(0 AS BIT), CAST(NULL AS VARBINARY(MAX)) FROM sys.column_master_keys ORDER BY [name]";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                masterKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3) && Convert.ToBoolean(reader.GetValue(3)),
                                reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4)));

        var encryptionKeys = new List<(string Name, string MasterKey, string Algorithm, byte[] Value)>();
        command.CommandText = @"
SELECT k.[name], m.[name], v.encryption_algorithm_name, v.encrypted_value
  FROM sys.column_encryption_keys k
  JOIN sys.column_encryption_key_values v ON v.column_encryption_key_id = k.column_encryption_key_id
  JOIN sys.column_master_keys m ON m.column_master_key_id = v.column_master_key_id
 ORDER BY k.[name], m.[name]";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                encryptionKeys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), (byte[])reader.GetValue(3)));

        var folder = Path.Join(_templatePath, Template.PrerequisitesFolder);
        var file = Path.Join(folder, AlwaysEncryptedKeysScript);
        var files = FileWrapper.GetFromFactory();
        if (masterKeys.Count == 0)
        {
            if (files.Exists(file) && GeneratedScript.IsGenerated(files.ReadAllText(file))) files.Delete(file);
            return;
        }

        _progressLog.Info("Casting Always Encrypted Keys");
        string Quote(string name) => "[" + Identifier.EscapeDelimited(name, Platform.SqlServer) + "]";
        string Hex(byte[] bytes) => "0x" + Convert.ToHexString(bytes);
        string Exec(string sql) => $"EXEC(N'{EscapeSql(sql)}')";

        var sb = new StringBuilder();
        sb.Append($"-- The Always Encrypted keys of {targetDb}, written by SchemaTongs on every extraction: change the source database,\r\n");
        sb.Append("-- not this file. A key is created only where it is missing; one that exists is left as it is. KEY_PATH names a\r\n");
        sb.Append("-- certificate or vault key outside the database, which any client that reads the encrypted columns needs access to.\r\n");
        sb.Append("IF OBJECT_ID('sys.column_master_keys') IS NULL\r\n");
        sb.Append("  RAISERROR('  Always Encrypted keys skipped: they need SQL Server 2016', 10, 1) WITH NOWAIT\r\n");
        sb.Append("ELSE\r\nBEGIN\r\n");
        sb.Append(string.Concat(masterKeys.Select(key =>
            $"  IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE [name] = N'{EscapeSql(key.Name)}')\r\n    " +
            Exec($"CREATE COLUMN MASTER KEY {Quote(key.Name)} WITH (KEY_STORE_PROVIDER_NAME = N'{EscapeSql(key.Provider)}', KEY_PATH = N'{EscapeSql(key.Path)}'" +
                 (key.Enclave && key.Signature != null ? $", ENCLAVE_COMPUTATIONS (SIGNATURE = {Hex(key.Signature)})" : "") + ")") + "\r\n")));
        sb.Append(string.Concat(encryptionKeys.GroupBy(k => k.Name).Select(key =>
            $"  IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE [name] = N'{EscapeSql(key.Key)}')\r\n    " +
            Exec($"CREATE COLUMN ENCRYPTION KEY {Quote(key.Key)} WITH VALUES " +
                 string.Join(", ", key.Select(v => $"(COLUMN_MASTER_KEY = {Quote(v.MasterKey)}, ALGORITHM = '{EscapeSql(v.Algorithm)}', ENCRYPTED_VALUE = {Hex(v.Value)})"))) + "\r\n")));
        sb.Append("END\r\n");

        DirectoryWrapper.GetFromFactory().CreateDirectory(folder);
        EnsurePrerequisitesFolder();
        _progressLog.Info($"  Casting {file}");
        if (!GeneratedScript.WriteUnlessOwned(file, sb.ToString()))
            _progressLog.Info($"  Keeping {file}: its generated marker was removed, so it is yours and is not overwritten");
    }

    internal const string PartitionSchemesScript = "PartitionSchemes.sql";

    // The partition functions and schemes the database's tables and indexes are stored on. The package names a scheme on a
    // table or index but never creates one, so a partitioned package failed on an empty database. They go to a generated
    // prerequisite script that creates only what is missing: an existing function or scheme is the operator's, boundaries
    // and all, and is left exactly as it is. Rewritten on every extraction; removed when nothing is partitioned.
    private void ScriptSqlServerPartitionSchemes(IDbCommand command, string targetDb)
    {
        var functions = new List<(string Name, string Type, bool Right)>();
        command.CommandText = @"
SELECT DISTINCT pf.[name], t.[name] +
       CASE WHEN t.[name] IN ('varchar', 'char', 'varbinary', 'binary')
            THEN '(' + CASE WHEN pp.max_length = -1 THEN 'max' ELSE CAST(pp.max_length AS VARCHAR(10)) END + ')'
            WHEN t.[name] IN ('nvarchar', 'nchar')
            THEN '(' + CASE WHEN pp.max_length = -1 THEN 'max' ELSE CAST(pp.max_length / 2 AS VARCHAR(10)) END + ')'
            WHEN t.[name] IN ('decimal', 'numeric') THEN '(' + CAST(pp.[precision] AS VARCHAR(10)) + ',' + CAST(pp.scale AS VARCHAR(10)) + ')'
            WHEN t.[name] IN ('datetime2', 'time', 'datetimeoffset') THEN '(' + CAST(pp.scale AS VARCHAR(10)) + ')'
            ELSE '' END,
       pf.boundary_value_on_right
  FROM sys.indexes i
  JOIN sys.objects o ON o.[object_id] = i.[object_id] AND o.is_ms_shipped = 0
  JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
  JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
  JOIN sys.partition_parameters pp ON pp.function_id = pf.function_id AND pp.parameter_id = 1
  JOIN sys.types t ON t.user_type_id = pp.user_type_id
 ORDER BY pf.[name]";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                functions.Add((reader.GetString(0), reader.GetString(1), Convert.ToBoolean(reader.GetValue(2))));

        var folder = Path.Join(_templatePath, Template.PrerequisitesFolder);
        var file = Path.Join(folder, PartitionSchemesScript);
        var files = FileWrapper.GetFromFactory();
        if (functions.Count == 0)
        {
            if (files.Exists(file) && GeneratedScript.IsGenerated(files.ReadAllText(file))) files.Delete(file);
            return;
        }

        // Each boundary as a literal of its own type: the value is a sql_variant, and the default conversion of a date or
        // a float loses precision.
        var boundaries = new List<(string Function, string Value)>();
        command.CommandText = @"
SELECT pf.[name],
       CASE WHEN rv.[value] IS NULL THEN 'NULL'
            WHEN v.bt IN ('tinyint', 'smallint', 'int', 'bigint', 'bit', 'decimal', 'numeric') THEN CONVERT(NVARCHAR(4000), rv.[value])
            WHEN v.bt IN ('money', 'smallmoney') THEN CONVERT(NVARCHAR(4000), CONVERT(MONEY, rv.[value]), 2)
            WHEN v.bt IN ('float', 'real') THEN CONVERT(NVARCHAR(4000), CONVERT(FLOAT, rv.[value]), 2)
            WHEN v.bt = 'date' THEN N'''' + CONVERT(NVARCHAR(30), CONVERT(DATE, rv.[value]), 23) + N''''
            WHEN v.bt IN ('datetime', 'smalldatetime') THEN N'''' + CONVERT(NVARCHAR(30), CONVERT(DATETIME, rv.[value]), 126) + N''''
            WHEN v.bt = 'datetime2' THEN N'''' + CONVERT(NVARCHAR(40), CONVERT(DATETIME2(7), rv.[value]), 126) + N''''
            WHEN v.bt = 'datetimeoffset' THEN N'''' + CONVERT(NVARCHAR(40), CONVERT(DATETIMEOFFSET(7), rv.[value]), 126) + N''''
            WHEN v.bt = 'time' THEN N'''' + CONVERT(NVARCHAR(20), CONVERT(TIME(7), rv.[value])) + N''''
            WHEN v.bt IN ('binary', 'varbinary') THEN CONVERT(NVARCHAR(4000), CONVERT(VARBINARY(8000), rv.[value]), 1)
            ELSE N'N''' + REPLACE(CONVERT(NVARCHAR(4000), rv.[value]), N'''', N'''''') + N'''' END
  FROM sys.partition_functions pf
  JOIN sys.partition_range_values rv ON rv.function_id = pf.function_id
 CROSS APPLY (SELECT CONVERT(SYSNAME, SQL_VARIANT_PROPERTY(rv.[value], 'BaseType')) AS bt) v
 ORDER BY pf.[name], rv.boundary_id";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                boundaries.Add((reader.GetString(0), reader.GetString(1)));

        var schemes = new List<(string Name, string Function, string FileGroup)>();
        command.CommandText = @"
SELECT ps.[name], pf.[name], fg.[name]
  FROM sys.partition_schemes ps
  JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
  JOIN sys.destination_data_spaces dds ON dds.partition_scheme_id = ps.data_space_id
  JOIN sys.filegroups fg ON fg.data_space_id = dds.data_space_id
 WHERE ps.data_space_id IN (SELECT i.data_space_id FROM sys.indexes i JOIN sys.objects o ON o.[object_id] = i.[object_id] AND o.is_ms_shipped = 0)
 ORDER BY ps.[name], dds.destination_id";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                schemes.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        _progressLog.Info("Casting Partition Schemes");
        string Quote(string name) => "[" + Identifier.EscapeDelimited(name, Platform.SqlServer) + "]";
        string Exec(string sql) => $"EXEC(N'{EscapeSql(sql)}')";

        var sb = new StringBuilder();
        sb.Append($"-- The partition functions and schemes the tables of {targetDb} are stored on, written by SchemaTongs on every\r\n");
        sb.Append("-- extraction: change the source database, not this file. One is created only where it is missing; one that exists is\r\n");
        sb.Append("-- left as it is, boundaries and all. The filegroups are the source's and must exist on the target.\r\n");
        foreach (var function in functions)
            sb.Append($"IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE [name] = N'{EscapeSql(function.Name)}')\r\n  " +
                      Exec($"CREATE PARTITION FUNCTION {Quote(function.Name)} ({function.Type}) AS RANGE {(function.Right ? "RIGHT" : "LEFT")} FOR VALUES (" +
                           string.Join(", ", boundaries.Where(b => b.Function == function.Name).Select(b => b.Value)) + ")") + "\r\n");
        foreach (var scheme in schemes.GroupBy(x => (x.Name, x.Function)))
        {
            var fileGroups = scheme.Select(x => x.FileGroup).ToList();
            // ALL TO also marks a NEXT USED filegroup, so it is written only where the source has that extra destination.
            var partitions = boundaries.Count(b => b.Function == scheme.Key.Function) + 1;
            var to = fileGroups.All(fg => fg == "PRIMARY") && fileGroups.Count == partitions + 1
                ? "ALL TO ([PRIMARY])"
                : "TO (" + string.Join(", ", fileGroups.Select(Quote)) + ")";
            sb.Append($"IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE [name] = N'{EscapeSql(scheme.Key.Name)}')\r\n  " +
                      Exec($"CREATE PARTITION SCHEME {Quote(scheme.Key.Name)} AS PARTITION {Quote(scheme.Key.Function)} {to}") + "\r\n");
        }

        DirectoryWrapper.GetFromFactory().CreateDirectory(folder);
        EnsurePrerequisitesFolder();
        _progressLog.Info($"  Casting {file}");
        if (!GeneratedScript.WriteUnlessOwned(file, sb.ToString()))
            _progressLog.Info($"  Keeping {file}: its generated marker was removed, so it is yours and is not overwritten");
    }

    // A template that lists its folders runs only those, so one extracted before the Prerequisites folder existed gets it
    // added, first, the first time a prerequisite is written.
    private void EnsurePrerequisitesFolder()
    {
        var templateFile = Path.Join(_templatePath, "Template.json");
        if (!FileWrapper.GetFromFactory().Exists(templateFile)) return;
        var template = JsonHelper.Load<Template>(templateFile);
        if (template == null || template.ScriptFolders.Count == 0
            || template.ScriptFolders.Any(f => string.Equals(f.FolderPath, Template.PrerequisitesFolder, StringComparison.OrdinalIgnoreCase))) return;
        template.ScriptFolders.Insert(0, new TemplateFolder { FolderPath = Template.PrerequisitesFolder, QuenchSlot = TemplateQuenchSlot.Objects });
        _progressLog.Info($"  Adding the {Template.PrerequisitesFolder} folder to {templateFile}");
        WritePackageObject(templateFile, template, "templates");
    }

    private void ScriptSqlServerFullTextCatalogs(IDbCommand command)
    {
        _progressLog.Info("Casting FullText Catalog Scripts");
        var castPath = GetCastPath(ScriptObjectType.FullTextCatalogs, "FullTextCatalogs");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT name, is_accent_sensitivity_on, is_default
  FROM sys.fulltext_catalogs
 ORDER BY name";

        var catalogs = new List<(string Name, bool AccentSensitive, bool IsDefault)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
                catalogs.Add((name, reader.GetBoolean(1), reader.GetBoolean(2)));
            }
        }

        foreach (var (name, accentSensitive, isDefault) in catalogs)
        {
            var script = $"IF NOT EXISTS (SELECT * FROM sys.fulltext_catalogs ftc WHERE ftc.name = N'{EscapeSql(name)}')\r\n" +
                         $"CREATE FULLTEXT CATALOG [{Identifier.EscapeDelimited(name, Platform.SqlServer)}] WITH ACCENT_SENSITIVITY = {(accentSensitive ? "ON" : "OFF")}{(isDefault ? " AS DEFAULT" : "")}";

            var fileName = ResolveOutputPath(castPath, EncodeFileName(name, ".sql"));
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.FullTextCatalogs++;
        }
    }

    private void ScriptSqlServerFullTextStopLists(IDbCommand command)
    {
        _progressLog.Info("Casting FullText Stop List Scripts");
        var castPath = GetCastPath(ScriptObjectType.FullTextStopLists, "FullTextStopLists");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT stoplist_id, name
  FROM sys.fulltext_stoplists
 ORDER BY name";

        var stopLists = new List<(int Id, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
                stopLists.Add((reader.GetInt32(0), name));
            }
        }

        var script = new StringBuilder();
        foreach (var (id, name) in stopLists)
        {
            command.CommandText = $@"
SELECT stopword, language
  FROM sys.fulltext_stopwords
 WHERE stoplist_id = {id}
 ORDER BY stopword, language";

            var stopWords = new List<(string Word, string Language)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    stopWords.Add((reader.GetString(0), reader.GetString(1)));
            }

            var escapedListName = Identifier.EscapeDelimited(name, Platform.SqlServer);
            script.Clear();
            script.Append($"IF NOT EXISTS (SELECT * FROM sys.fulltext_stoplists ftsl WHERE ftsl.name = N'{EscapeSql(name)}')\r\n")
                .Append("BEGIN\r\n")
                .Append($"CREATE FULLTEXT STOPLIST [{escapedListName}]\r\n")
                .Append(";\r\n");

            foreach (var (word, language) in stopWords)
                script.Append($"ALTER FULLTEXT STOPLIST [{escapedListName}] ADD '{EscapeSql(word)}' LANGUAGE '{EscapeSql(language)}';\r\n");

            script.Append("END\r\n");

            var fileName = ResolveOutputPath(castPath, EncodeFileName(name, ".sql"));
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script.ToString());
            _stats.FullTextStopLists++;
        }
    }

    // A trigger's enabled state and its first/last order are not part of its definition, so a disabled trigger or one
    // set first or last came back enabled and unordered on the next deploy. The tail carries that state in its own
    // batches after the module.
    private string SqlServerTriggerTail(IDbCommand command, string tableSchema, string tableName, string triggerName)
    {
        var trigger = $"[{Identifier.EscapeDelimited(tableSchema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(triggerName, Platform.SqlServer)}]";
        var table = $"[{Identifier.EscapeDelimited(tableSchema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(tableName, Platform.SqlServer)}]";
        command.CommandText = $@"
SELECT tr.is_disabled,
       OBJECTPROPERTY(tr.object_id, 'ExecIsFirstInsertTrigger'), OBJECTPROPERTY(tr.object_id, 'ExecIsLastInsertTrigger'),
       OBJECTPROPERTY(tr.object_id, 'ExecIsFirstUpdateTrigger'), OBJECTPROPERTY(tr.object_id, 'ExecIsLastUpdateTrigger'),
       OBJECTPROPERTY(tr.object_id, 'ExecIsFirstDeleteTrigger'), OBJECTPROPERTY(tr.object_id, 'ExecIsLastDeleteTrigger')
  FROM sys.triggers tr
 WHERE tr.object_id = OBJECT_ID(N'{EscapeSql(trigger)}')";
        bool disabled;
        var flags = new int[6];
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return "";
            disabled = reader.GetBoolean(0);
            for (var i = 0; i < flags.Length; i++) flags[i] = reader.IsDBNull(i + 1) ? 0 : reader.GetInt32(i + 1);
        }

        var tail = "";
        if (disabled) tail += $"DISABLE TRIGGER {trigger} ON {table}\r\nGO\r\n";
        var orders = new[] { ("First", "INSERT", flags[0]), ("Last", "INSERT", flags[1]), ("First", "UPDATE", flags[2]), ("Last", "UPDATE", flags[3]), ("First", "DELETE", flags[4]), ("Last", "DELETE", flags[5]) };
        foreach (var (order, statement, set) in orders)
            if (set == 1) tail += $"EXEC sp_settriggerorder @triggername = N'{EscapeSql(trigger)}', @order = N'{order}', @stmttype = N'{statement}'\r\nGO\r\n";
        return tail.Length == 0 ? "" : "\r\n" + tail;
    }

    private void ScriptSqlServerDDLTriggers(IDbCommand command)
    {
        _progressLog.Info("Casting Database DDL Trigger Scripts");
        var castPath = GetCastPath(ScriptObjectType.DDLTriggers, "DDLTriggers");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT tr.name AS TriggerName
  FROM sys.triggers tr
 WHERE tr.parent_class = 0
 ORDER BY tr.name";

        var triggers = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
                triggers.Add(name);
            }
        }

        foreach (var triggerName in triggers)
        {
            command.CommandText = $@"
SELECT sm.definition, sm.uses_ansi_nulls, sm.uses_quoted_identifier, tr.is_disabled
  FROM sys.sql_modules sm
  JOIN sys.triggers tr ON sm.object_id = tr.object_id
 WHERE tr.parent_class = 0 AND tr.name = '{EscapeSql(triggerName)}'";

            string definition = null;
            bool usesAnsiNulls = true;
            bool usesQuotedIdentifier = true;
            bool disabled = false;

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        _progressLog.Warn($"  WARNING: {triggerName} is encrypted, skipping");
                        _stats.SkippedObjects++;
                        continue;
                    }
                    definition = reader.GetString(0);
                    usesAnsiNulls = reader.GetBoolean(1);
                    usesQuotedIdentifier = reader.GetBoolean(2);
                    disabled = reader.GetBoolean(3);
                }
            }

            if (definition == null) continue;

            definition = definition.Trim();

            if (!definition.Contains("\r\n"))
                definition = definition.Replace("\n", "\r\n");

            var createMatch = Regex.Match(definition, @"(?<!\w)CREATE(\s+)TRIGGER\b", RegexOptions.IgnoreCase);
            if (createMatch.Success)
                definition = WithOrAlter(definition, createMatch);

            var escapedName = Regex.Escape(triggerName);
            var namePattern = $@"(?<=TRIGGER\s+)\[?{escapedName}\]?";
            definition = Regex.Replace(definition, namePattern, $"[{Identifier.EscapeDelimited(triggerName, Platform.SqlServer)}]", RegexOptions.IgnoreCase);

            definition = Regex.Replace(definition, @"(?<=\bAS[ \t]*\r\n)([ \t]*)(?=\S)", "$1\r\n");

            if (_moduleSyntax == ModuleSyntax.Legacy)
                definition = ConvertToLegacyModuleForm(definition, null, triggerName);

            var extProps = GetExtendedProperties(command, "TRIGGER", triggerName);

            var ansiNulls = usesAnsiNulls ? "ON" : "OFF";
            var quotedIdentifier = usesQuotedIdentifier ? "ON" : "OFF";

            var sql = $"SET ANSI_NULLS {ansiNulls}\r\n" +
                      $"SET QUOTED_IDENTIFIER {quotedIdentifier}\r\n" +
                      $"GO\r\n\r\n" +
                      $"{definition}\r\n\r\n" +
                      $"GO\r\n";

            if (extProps.Length > 0)
                sql += extProps + "\r\nGO\r\n";
            if (disabled)
                sql += $"\r\nDISABLE TRIGGER [{Identifier.EscapeDelimited(triggerName, Platform.SqlServer)}] ON DATABASE\r\nGO\r\n";

            var fileName = ResolveOutputPath(castPath, EncodeFileName(triggerName, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { _stats.DDLTriggers++; continue; }
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, sql);
            ValidateAndHandleScript(command.Connection, fileName, sql, ScriptObjectType.DDLTriggers);
            _stats.DDLTriggers++;
        }
    }

    private void ScriptSqlServerXmlSchemaCollections(IDbCommand command)
    {
        _progressLog.Info("Casting XML Schema Collection Scripts");
        var castPath = GetCastPath(ScriptObjectType.XMLSchemaCollections, "XMLSchemaCollections");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
            SELECT s.name AS SchemaName, xsc.name AS CollectionName
              FROM sys.xml_schema_collections xsc
              JOIN sys.schemas s ON xsc.schema_id = s.schema_id
             WHERE xsc.xml_collection_id > 1
             ORDER BY s.name, xsc.name";
        var collections = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                collections.Add((reader.GetString(0), reader.GetString(1)));

        foreach (var (schema, name) in collections)
        {
            if (!ShouldExtractFromSchema(schema)) continue;
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;

            command.CommandText = $"SELECT CAST(XML_SCHEMA_NAMESPACE(N'{EscapeSql(schema)}', N'{EscapeSql(name)}') AS NVARCHAR(MAX))";
            var xmlContent = (string)command.ExecuteScalar();

            var script =
                $"IF NOT EXISTS (SELECT * FROM sys.xml_schema_collections c, sys.schemas s WHERE c.schema_id = s.schema_id AND (quotename(s.name) + '.' + quotename(c.name)) = N'[{EscapeSql(Identifier.EscapeDelimited(schema, Platform.SqlServer))}].[{EscapeSql(Identifier.EscapeDelimited(name, Platform.SqlServer))}]')\r\n" +
                $"CREATE XML SCHEMA COLLECTION [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}] AS N'{xmlContent}'";
            script = FormatXmlInScript(script);

            var extProps = GetExtendedProperties(command, "SCHEMA", schema, "XML SCHEMA COLLECTION", name);
            if (extProps.Length > 0)
                script += "\r\nGO\r\n" + extProps;

            var fileName = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.XmlSchemaCollections++;
        }
    }

    // The object's name as its stored definition spells it. After sp_rename the catalog name changes but the stored
    // text does not, so the two disagree and a script written from the text would create the old name.
    private static readonly Regex CreateHeaderName = new(
        @"(?<!\w)CREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?<full>(\[[^\]]+\]|""[^""]+""|[\w$#@]+)(\s*\.\s*(\[[^\]]+\]|""[^""]+""|[\w$#@]+))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string DefinedObjectName(string definition)
    {
        var match = CreateHeaderName.Match(definition);
        if (!match.Success) return null;
        var full = match.Groups["full"].Value;
        var last = full.LastIndexOf('.');
        return (last >= 0 ? full[(last + 1)..] : full).Trim().Trim('[', ']', '"');
    }

    internal static string ReplaceDefinedName(string definition, string schemaName, string objectName)
    {
        var match = CreateHeaderName.Match(definition);
        if (!match.Success) return definition;
        var full = match.Groups["full"];
        return definition[..full.Index] + $"[{Identifier.EscapeDelimited(schemaName, Platform.SqlServer)}].[{Identifier.EscapeDelimited(objectName, Platform.SqlServer)}]" + definition[(full.Index + full.Length)..];
    }

    // SQL Server stores a CREATE OR ALTER module with the two words cut out and the whitespace around them kept, so a gap
    // of three or more is where they were. They go back into it: inserted ahead of it, each round trip through the
    // server widened the gap by two.
    internal static string WithOrAlter(string definition, Match createMatch)
    {
        var gap = createMatch.Groups[1].Value;
        var header = gap.Length >= 3 ? $"CREATE{gap[0]}OR{gap[1]}ALTER{gap[2..]}" : "CREATE OR ALTER" + gap;
        return definition[..createMatch.Index] + header + definition[(createMatch.Index + "CREATE".Length + gap.Length)..];
    }

    internal static string ConvertToCreateOrAlter(string definition, string schemaName, string objectName)
    {
        var createMatch = Regex.Match(definition,
            @"(?<!\w)CREATE(\s+)(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\b",
            RegexOptions.IgnoreCase);
        var result = createMatch.Success ? WithOrAlter(definition, createMatch) : definition;

        var escapedSchema = Regex.Escape(schemaName);
        var escapedName = Regex.Escape(objectName);
        var namePattern = $@"\[?{escapedSchema}\]?\.\[?{escapedName}\]?";
        var match = Regex.Match(result, namePattern, RegexOptions.IgnoreCase);
        if (match.Success)
            result = result.Substring(0, match.Index) + $"[{Identifier.EscapeDelimited(schemaName, Platform.SqlServer)}].[{Identifier.EscapeDelimited(objectName, Platform.SqlServer)}]" + result.Substring(match.Index + match.Length);

        return result;
    }

    private ModuleSyntax ReadModuleSyntax(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ModuleSyntax.CreateOrAlter;
        if (Enum.TryParse<ModuleSyntax>(value.Trim(), true, out var syntax) && Enum.IsDefined(syntax)) return syntax;
        _progressLog.Warn($"ShouldCast:ModuleSyntax '{value}' is not CreateOrAlter or Legacy; modules are written with CREATE OR ALTER.");
        return ModuleSyntax.CreateOrAlter;
    }

    // The form every supported version accepts: a guarded stub creates the object when it is missing, then ALTER carries
    // the real body. The stub mirrors the kind, a function's shape and a trigger's first event, because ALTER cannot
    // change those and the stub must be creatable wherever the real object is. A null schema is a database trigger.
    internal static string ConvertToLegacyModuleForm(string definition, string schemaName, string objectName, string parentTableName = null)
    {
        var header = Regex.Match(definition, @"(?<!\w)CREATE\s+(OR\s+ALTER\s+)?(?<kind>PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase);
        if (!header.Success) return definition;

        var name = $"[{Identifier.EscapeDelimited(objectName, Platform.SqlServer)}]";
        var qualified = schemaName == null ? name : $"[{Identifier.EscapeDelimited(schemaName, Platform.SqlServer)}].{name}";
        var guard = $"IF OBJECT_ID(N'{EscapeSql(qualified)}') IS NULL";
        string stub;
        switch (header.Groups["kind"].Value.ToUpperInvariant())
        {
            case "VIEW":
                stub = $"CREATE VIEW {qualified} AS SELECT 1 AS x";
                break;
            case "FUNCTION":
                stub = Regex.IsMatch(definition, @"\bRETURNS\s+@\w+\s+TABLE\b", RegexOptions.IgnoreCase) ? $"CREATE FUNCTION {qualified}() RETURNS @t TABLE (x INT) AS BEGIN RETURN END"
                    : Regex.IsMatch(definition, @"\bRETURNS\s+TABLE\b", RegexOptions.IgnoreCase) ? $"CREATE FUNCTION {qualified}() RETURNS TABLE AS RETURN SELECT 1 AS x"
                    : $"CREATE FUNCTION {qualified}() RETURNS INT AS BEGIN RETURN 0 END";
                break;
            case "TRIGGER" when parentTableName != null:
            {
                var firing = Regex.Match(definition, @"\b(?<mode>AFTER|FOR|INSTEAD\s+OF)\s+(?<event>INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
                var mode = firing.Success && firing.Groups["mode"].Value.StartsWith("INSTEAD", StringComparison.OrdinalIgnoreCase) ? "INSTEAD OF" : "AFTER";
                var firstEvent = firing.Success ? firing.Groups["event"].Value.ToUpperInvariant() : "INSERT";
                stub = $"CREATE TRIGGER {qualified} ON [{Identifier.EscapeDelimited(schemaName, Platform.SqlServer)}].[{Identifier.EscapeDelimited(parentTableName, Platform.SqlServer)}] {mode} {firstEvent} AS RETURN";
                break;
            }
            case "TRIGGER":
                guard = $"IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'{EscapeSql(objectName)}')";
                stub = $"CREATE TRIGGER {qualified} ON DATABASE FOR DDL_DATABASE_LEVEL_EVENTS AS RETURN";
                break;
            default:
                stub = $"CREATE PROCEDURE {qualified} AS RETURN";
                break;
        }

        var altered = definition[..header.Index] + "ALTER " + header.Groups["kind"].Value + definition[(header.Index + header.Length)..];
        return $"{guard} EXEC('{EscapeSql(stub)}')\r\nGO\r\n\r\n{altered}";
    }

    internal string ScriptSqlServerProgrammableObject(IDbCommand command, string schemaName, string objectName,
        string level1Type, string level2Type = null, string level2ParentName = null)
    {
        command.CommandText = $@"
SELECT sm.definition, sm.uses_ansi_nulls, sm.uses_quoted_identifier
  FROM sys.sql_modules sm
  JOIN sys.objects o ON sm.object_id = o.object_id
  JOIN sys.schemas s ON o.schema_id = s.schema_id
 WHERE s.name = '{EscapeSql(schemaName)}' AND o.name = '{EscapeSql(objectName)}'";

        string definition = null;
        bool usesAnsiNulls = true;
        bool usesQuotedIdentifier = true;

        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    _progressLog.Warn($"  WARNING: {schemaName}.{objectName} is encrypted, skipping");
                    _stats.SkippedObjects++;
                    return null;
                }
                definition = reader.GetString(0);
                usesAnsiNulls = reader.GetBoolean(1);
                usesQuotedIdentifier = reader.GetBoolean(2);
            }
        }

        if (definition == null) return null;

        definition = definition.Trim();

        if (!definition.Contains("\r\n"))
            definition = definition.Replace("\n", "\r\n");

        var definedName = DefinedObjectName(definition);
        if (definedName != null && !definedName.Equals(objectName, StringComparison.OrdinalIgnoreCase))
        {
            _progressLog.Warn($"  WARNING: {schemaName}.{objectName} was renamed with sp_rename; its stored definition still creates {definedName}. The script is written under the current name; check the body for the old one.");
            definition = ReplaceDefinedName(definition, schemaName, objectName);
        }

        definition = ConvertToCreateOrAlter(definition, schemaName, objectName);

        definition = Regex.Replace(definition, @"(?<=\bAS[ \t]*\r\n)([ \t]*)(?=\S)", "$1\r\n");

        if (_moduleSyntax == ModuleSyntax.Legacy)
            definition = ConvertToLegacyModuleForm(definition, schemaName, objectName, level2ParentName);

        var level1Name = level2Type != null ? level2ParentName : objectName;
        var extPropsLevel1Type = level2Type != null ? "TABLE" : level1Type;
        var extPropsLevel2Type = level2Type;
        var extPropsLevel2Name = level2Type != null ? objectName : null;
        var extProps = GetExtendedProperties(command, "SCHEMA", schemaName,
            extPropsLevel1Type, level1Name, extPropsLevel2Type, extPropsLevel2Name);

        var ansiNulls = usesAnsiNulls ? "ON" : "OFF";
        var quotedIdentifier = usesQuotedIdentifier ? "ON" : "OFF";

        var script = $"SET ANSI_NULLS {ansiNulls}\r\n" +
                     $"SET QUOTED_IDENTIFIER {quotedIdentifier}\r\n" +
                     $"GO\r\n\r\n" +
                     $"{definition}\r\n\r\n" +
                     $"GO\r\n";

        if (extProps.Length > 0)
            script += extProps + "\r\nGO\r\n";

        return script;
    }

    internal static string FormatXmlInScript(string script)
    {
        if (!script.Contains(" AS N'")) return script;

        var xmlStart = script.IndexOfIgnoringCase(" AS N'") + 6;
        var xml = script.Substring(xmlStart, script.Length - (xmlStart + 1));
        var formattedXml = "\r\n" + string.Join("\r\n", xml.Replace("</xsd:schema>", "</xsd:schema>\r").Split('\r').Select(FormatXml));
        return script.Replace(xml, formattedXml);
    }

    private static string FormatXml(string xml)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml;
            var formatted = XDocument.Parse(xml).ToString();
            if (!formatted.Contains("\r\n"))
                formatted = formatted.Replace("\n", "\r\n");
            return formatted;
        }
        catch
        {
            return xml;
        }
    }

    private void CastSqlServerIndexedViews(IDbCommand command)
    {
        command.CommandText = @"
SELECT s.name AS SchemaName, v.name AS ViewName
  FROM sys.views v
 INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
 WHERE OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1
   AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'SchemaSmith')
   AND v.is_ms_shipped = 0
 ORDER BY s.name, v.name";

        _progressLog.Info("Casting Indexed View Structures");
        var indexedViews = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schemaName = reader["SchemaName"].ToString();
                if (!ShouldExtractFromSchema(schemaName)) continue;
                indexedViews.Add((schemaName, reader["ViewName"].ToString()));
            }
        }

        if (indexedViews.Count == 0) return;

        // Install the extraction function — the XML twin (GenerateIndexedViewXml) for the legacy encoding,
        // else the JSON generator. The XML variant carries a twin name, so both live in SchemaSmith. Split on
        // GO: the pre-2016 idempotent create form (IF OBJECT_ID(...) DROP; GO; CREATE …) is two batches.
        var generateFn = ResourceLoader.Load(
            _ingestEncoding == IngestEncoding.Xml
                ? "SchemaSmith.GenerateIndexedViewXml.sql"
                : "SchemaSmith.GenerateIndexedViewJson.sql", _platform)
            ?? throw new Exception("GenerateIndexedView generator script not found");
        foreach (var batch in SqlServerBatchSplitter.Split(generateFn))
        {
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }

        var castPath = Path.Combine(_templatePath, "Indexed Views");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var (schema, name) in indexedViews)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{name}".ToLower()) && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast {_ingestEncoding} for {schema}.{name}");
            var viewJson = ExtractIndexedViewModel(command, schema, name);
            if (string.IsNullOrWhiteSpace(viewJson) || viewJson.Trim().Equals("{}"))
            {
                _progressLog.Error($"    No json returned for {schema}.{name}");
                _stats.SkippedObjects++;
                continue;
            }
            var viewObj = JsonText.Deserialize<SqlServerIndexedView>(viewJson);
            var viewFile = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            // Schema-template mode (design §7.2 / §7.3): strip the Schema field; rewrite the indexed
            // view's Definition body AND any contained filtered-index FilterExpression so source-
            // schema references collapse to {{SchemaName}}. Capture unqualified-ref audit hits.
            if (_isSchemaTemplate)
            {
                viewObj.Schema = null;
                var relPath = ResolveAuditRelativePath(viewFile);
                viewObj.Definition = RewriteJsonExpression(viewObj.Definition, relPath, $"IndexedView.Definition '{viewObj.Name}'");
                foreach (var idx in viewObj.Indexes ?? new List<SqlServerIndex>())
                    idx.FilterExpression = RewriteJsonExpression(idx.FilterExpression, relPath, $"Index.FilterExpression '{idx.Name}'");
            }
            _progressLog.Info($"    Casting {viewFile}");
            WritePackageObject(viewFile, viewObj, "indexedviews");
            _stats.IndexedViews++;
        }
    }

    // Legacy encoding calls the GenerateIndexedViewXml twin (FOR XML PATH) and converts the XML back to the
    // JSON model; the modern encoding uses the GenerateIndexedViewJson function directly.
    private string ExtractIndexedViewModel(IDbCommand command, string schema, string name)
    {
        if (_ingestEncoding == IngestEncoding.Xml)
        {
            command.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewXml]('{EscapeSql(schema)}', '{EscapeSql(name)}')";
            var xml = command.ExecuteScalar()?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(xml) ? "" : ModelXmlSerializer.FromIngestXml(xml);
        }

        command.CommandText = $"SELECT [SchemaSmith].[GenerateIndexedViewJson]('{EscapeSql(schema)}', '{EscapeSql(name)}')";
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    #endregion

    #region PostgreSQL Extraction

    private void CastPostgreSqlObjects(string targetDb)
    {
        using var connection = GetConnection(targetDb);
        try
        {
            using var command = connection.CreateCommand();

            _progressLog.Info("Kindling The Forge");
            ForgeKindler.KindleTheForge(command, _platform, allowReadOnlyTarget: true);

            if (_includeTables) CastPostgreSqlTableDefinitions(command, targetDb);
            if (_includeSchemas) CastPostgreSqlSchemas(command);
            if (_includeDomainTypes) CastPostgreSqlDomainTypes(command);
            if (_includeEnumTypes) CastPostgreSqlEnumTypes(command);
            if (_includeCompositeTypes) CastPostgreSqlCompositeTypes(command);
            if (_includeFunctions) CastPostgreSqlFunctions(command);
            if (_includeAggregates) CastPostgreSqlAggregates(command);
            if (_includeProcedures) CastPostgreSqlProcedures(command);
            if (_includeSequences) CastPostgreSqlSequences(command);
            if (_includeRules) CastPostgreSqlRules(command);
            if (_includeTriggers) CastPostgreSqlTriggers(command);
            if (_includeViews) CastPostgreSqlViews(command);
            if (_includeMaterializedViews) CastPostgreSqlMaterializedViews(command);
            if (_includeCollations) CastPostgreSqlCollations(command);
            if (_includePublications) CastPostgreSqlPublications(command);
            if (_includeRangeTypes) CastPostgreSqlRangeTypes(command);
            if (_includeTextSearchConfigurations) CastPostgreSqlTextSearchConfigurations(command);
            if (_includeOperators) CastPostgreSqlOperators(command);
            if (_includeCasts) CastPostgreSqlCasts(command);
            if (_includeEventTriggers) CastPostgreSqlEventTriggers(command);
            LogPostgreSqlExtensionMembersSkipped(command);
            LogPostgreSqlDeclinedKinds(command);
        }
        finally
        {
            connection.Close();
        }
    }

    // An installed extension's members (pg_depend deptype 'e') belong to the extension, not the package: extracting
    // them wrote pg_trgm's thirty functions under Functions/ and PostGIS's spatial_ref_sys under Tables/, which then
    // deployed as if the package owned them. Every enumeration query below carries this predicate for its catalog.
    private static string PostgreSqlNotExtensionMember(string classRegclass, string oidExpression) =>
        $"NOT EXISTS (SELECT 1 FROM pg_depend xd WHERE xd.classid = '{classRegclass}'::regclass AND xd.objid = {oidExpression} AND xd.deptype = 'e')";

    // Also not what the engine created along with another object (deptype 'i'): a range type's constructor functions
    // (LANGUAGE internal) and its multirange casts come back with the type.
    private static string PostgreSqlNotEngineOwned(string classRegclass, string oidExpression) =>
        $"NOT EXISTS (SELECT 1 FROM pg_depend xd WHERE xd.classid = '{classRegclass}'::regclass AND xd.objid = {oidExpression} AND xd.deptype IN ('e', 'i'))";

    // PG-009's declined kinds need support functions compiled into a server library, so a package cannot create one on an
    // empty database. They are named rather than silently absent; an extension's own are in its line above.
    private void LogPostgreSqlDeclinedKinds(IDbCommand command)
    {
        const string userSchema = "n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith') AND n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'";
        command.CommandText = $@"
SELECT 'base type' AS Kind, n.nspname AS SchemaName, t.typname AS Name
  FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
 WHERE t.typtype = 'b' AND {userSchema} AND {PostgreSqlNotEngineOwned("pg_type", "t.oid")}
UNION ALL
SELECT 'operator class', n.nspname, c.opcname || ' (' || am.amname || ')'
  FROM pg_opclass c JOIN pg_namespace n ON n.oid = c.opcnamespace JOIN pg_am am ON am.oid = c.opcmethod
 WHERE {userSchema} AND {PostgreSqlNotExtensionMember("pg_opclass", "c.oid")}
UNION ALL
SELECT 'operator family', n.nspname, f.opfname || ' (' || am.amname || ')'
  FROM pg_opfamily f JOIN pg_namespace n ON n.oid = f.opfnamespace JOIN pg_am am ON am.oid = f.opfmethod
 WHERE {userSchema} AND {PostgreSqlNotExtensionMember("pg_opfamily", "f.oid")}
   -- An operator class created without FAMILY makes a family of its own name, which the class's line already covers.
   AND NOT EXISTS (SELECT 1 FROM pg_opclass c WHERE c.opcfamily = f.oid AND c.opcname = f.opfname AND c.opcnamespace = f.opfnamespace)
UNION ALL
SELECT 'text search parser', n.nspname, p.prsname
  FROM pg_ts_parser p JOIN pg_namespace n ON n.oid = p.prsnamespace
 WHERE {userSchema} AND {PostgreSqlNotExtensionMember("pg_ts_parser", "p.oid")}
UNION ALL
SELECT 'text search template', n.nspname, tp.tmplname
  FROM pg_ts_template tp JOIN pg_namespace n ON n.oid = tp.tmplnamespace
 WHERE {userSchema} AND {PostgreSqlNotExtensionMember("pg_ts_template", "tp.oid")}
 ORDER BY 1, 2, 3;
";
        var declined = new List<(string Kind, string Name)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (ShouldExtractFromSchema(reader.GetString(1)))
                    declined.Add((reader.GetString(0), $"{reader.GetString(1)}.{reader.GetString(2)}"));
        foreach (var (kind, name) in declined)
        {
            _progressLog.Warn($"  Skipping {kind} {name}: it needs support functions compiled into a server library, so a package cannot create it");
            _stats.SkippedObjects++;
        }
    }

    private void LogPostgreSqlExtensionMembersSkipped(IDbCommand command)
    {
        command.CommandText = @"
SELECT e.extname, COUNT(*) AS members
  FROM pg_depend d
  JOIN pg_extension e ON e.oid = d.refobjid
  WHERE d.refclassid = 'pg_extension'::regclass
    AND d.deptype = 'e'
    AND e.extname <> 'plpgsql'
  GROUP BY e.extname
  ORDER BY e.extname;
";
        var skipped = new List<(string Extension, long Members)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var extension = reader["extname"]?.ToString();
                if (!string.IsNullOrEmpty(extension)) skipped.Add((extension, Convert.ToInt64(reader["members"] ?? 0)));
            }
        foreach (var (extension, members) in skipped)
            _progressLog.Info($"  Skipping {members} objects owned by extension {extension} (an extension's members are not extracted; the extension itself is a prerequisite of the target)");
    }

    private void CastPostgreSqlTableDefinitions(IDbCommand command, string targetDb)
    {
        using var gateConnection = GetConnection(targetDb);
        using var gateCommand = gateConnection.CreateCommand();
        bool IsVariantActive(string expr) =>
            GateEvaluator.ShouldApply(gateCommand, SqlScript.TokenReplace(expr, _packageTokens, _platform));

        // relkind and relispartition let the loop below tell a partitioned parent (relkind = 'p')
        // and its partitions (relispartition = true) apart from an ordinary table — both need
        // different handling than a plain table once enumerated (see the skip below).
        // relpersistence 'u' is an UNLOGGED table, which the model carries as Persistence: Unlogged; only a temporary
        // table ('t') is left out.
        command.CommandText = $@"
SELECT t.schemaname, t.tablename, c.relkind, c.relispartition,
       CASE WHEN c.reloftype <> 0 THEN FORMAT_TYPE(c.reloftype, NULL) END AS of_type,
       CASE WHEN NOT c.relispartition THEN (SELECT STRING_AGG(pn.nspname || '.' || p.relname, ', ' ORDER BY i.inhseqno)
                                              FROM pg_inherits i
                                              JOIN pg_class p ON p.oid = i.inhparent
                                              JOIN pg_namespace pn ON pn.oid = p.relnamespace
                                             WHERE i.inhrelid = c.oid) END AS inherits_from
  FROM pg_tables t
  JOIN pg_class c ON c.relname = t.tablename
                 AND c.relnamespace = (SELECT n.oid FROM pg_namespace n WHERE n.nspname = t.schemaname)
                 AND c.relpersistence IN ('p', 'u')
  WHERE t.schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND {PostgreSqlNotExtensionMember("pg_class", "c.oid")}
  ORDER BY t.schemaname, t.tablename;
";

        _progressLog.Info("Casting Table Structures");
        var tables = new List<(string Schema, string Table, string RelKind, bool IsPartitionChild, string Refusal)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["schemaname"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                // PG-008. Neither shape is modelled: written as a plain table, a child would lose the parent's columns
                // and a typed table its type.
                var refusal = reader["of_type"] is string ofType
                    ? $"is a typed table (OF {ofType}) — typed tables are not modeled; skipped rather than emitted as a table that no longer follows its type."
                    : reader["inherits_from"] is string parents
                        ? $"inherits from {parents} — table inheritance is not modeled; skipped rather than emitted as an unrelated standalone table. The parent is extracted, and DataTongs reads only its own rows."
                        : null;
                tables.Add((sch, reader["tablename"].ToString(), reader["relkind"].ToString(), Convert.ToBoolean(reader["relispartition"]), refusal));
            }
        }

        var castPath = Path.Combine(_templatePath, "Tables");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
        var resolver = new TableFileResolver(castPath, _platform, _isSchemaTemplate, IsVariantActive);

        foreach (var (schema, table, relKind, isPartitionChild, refusal) in tables)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{table}".ToLower()) && !_objectsToCast.Contains(table.ToLower())) continue;

            // A warning and a skip, not an error: the rest of the database extracts, and the run exits 1 as for any skip.
            if (refusal != null)
            {
                _progressLog.Warn($"    WARNING: {schema}.{table} {refusal}");
                _stats.SkippedObjects++;
                continue;
            }

            // SchemaSmith does not model PostgreSQL declarative partitioning (see the growth-up spike
            // findings) and the generator query cannot see a partitioned parent at all (relkind = 'p'
            // matches neither its relkind = 'r' nor its relam join). Left unhandled, the parent
            // silently disappears while each partition extracts as an ordinary standalone table --
            // the package looks complete but no longer means what the database means. Failing both
            // the parent and its partitions through the same per-table skip used for any other
            // unextractable table keeps that misrepresentation from reaching disk at all.
            if (relKind == "p" || isPartitionChild)
            {
                _progressLog.Error(relKind == "p"
                    ? $"    ERROR: {schema}.{table} is a partitioned table — PostgreSQL partitioning is not modeled; skipped rather than emitted as a table with no partitions."
                    : $"    ERROR: {schema}.{table} is a partition of a partitioned table — PostgreSQL partitioning is not modeled; skipped rather than emitted as an unrelated standalone table.");
                _stats.TableErrors++;
                continue;
            }

            _progressLog.Info($"  Cast Json for {schema}.{table}");
            try
            {
                command.CommandText = $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('{EscapeSql(schema)}', '{EscapeSql(table)}', '{_objectOrder}')";
                var tableJson = command.ExecuteScalar()?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(tableJson) || tableJson.Trim().Equals("{}"))
                {
                    _progressLog.Error($"    No json returned for {schema}.{table}");
                    _stats.TableErrors++;
                    continue;
                }
                // Deserialize to the typed PostgreSqlTable so the Schema property exists on the in-memory
                // object — needed both to scrub it in schema-template mode and to omit the default schema
                // in regular mode. Base Table would drop Schema (and PG-specific column properties),
                // leaving content that disagrees with a catalog-derived filename (SS-FILE-NAME-003) and,
                // worse, round-tripping a non-default-schema table as public.<name> on the next deploy.
                var tableObj = PlatformDeserializer.DeserializeTable(tableJson, _platform);

                if (_checkConstraintStyle == CheckConstraintStyle.ColumnLevel && tableObj is PostgreSqlTable columnLevelTable)
                {
                    // The generator emits every check table-level; conkey is what attributes a
                    // single-column one back to its column. array_length = 1 excludes multi-column
                    // checks, which have no single column to belong to.
                    command.CommandText = $@"
SELECT con.conname AS ""Name"",
       a.attname AS ""ColumnName""
  FROM pg_catalog.pg_constraint con
  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[1]
 WHERE con.conrelid = '""{EscapeSql(schema)}"".""{EscapeSql(table)}""'::regclass
   AND con.contype = 'c'
   AND array_length(con.conkey, 1) = 1
 ORDER BY con.conname";

                    var singleColumnChecks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using (var ccReader = command.ExecuteReader())
                    {
                        while (ccReader.Read())
                            singleColumnChecks[$"{ccReader["Name"]}"] = $"{ccReader["ColumnName"]}";
                    }
                    DemoteSingleColumnChecksToColumnLevel(columnLevelTable, singleColumnChecks);
                }

                // Regular mode: omit the PostgreSQL default schema (public) from content so the package
                // follows the default-schema-omission convention (deploy re-resolves an unset Schema to
                // public). A named non-default schema is kept. The write target then derives from this
                // same content Schema — schema-less for public, qualified for a named schema — so the
                // extraction output passes SS-FILE-NAME-003 by construction. Schema-template mode nulls
                // Schema in ScrubSchemaForTemplate below, so it is excluded here.
                if (!_isSchemaTemplate && tableObj is PostgreSqlTable pgTable
                    && string.Equals(Identifier.Unwrap(pgTable.Schema, _platform),
                                     _platform.GetDefaultSchema(), _schemaNameComparison))
                    pgTable.Schema = null;

                var contentSchema = (tableObj as IDeliverableTable)?.Schema ?? "";
                var resolution = resolver.Resolve(contentSchema, table);
                var tableFile = resolution.WritePath;
                // A refreshed file keeps the form it was written in: one that declares "Schema": "public" keeps
                // declaring it, so its file name and content still agree.
                if (!_isSchemaTemplate && string.IsNullOrEmpty(contentSchema) && !string.IsNullOrEmpty(resolution.ExistingSchema)
                    && tableObj is PostgreSqlTable refreshed)
                    refreshed.Schema = resolution.ExistingSchema;
                MarkPathWritten(castPath, tableFile);
                if (resolution.UngatedEmit)
                    _progressLog.Warn($"    Extracted {schema}.{table} did not match any active variant — writing ungated '{Path.GetFileName(tableFile)}'; resolve its gating (SS-DUP-001).");
                var oldName = tableObj.OldName?.Trim('"') ?? "";
                var oldTableFile = string.IsNullOrEmpty(oldName)
                    ? null
                    : ResolveOutputPath(castPath, TableFileName.Canonical(
                        contentSchema, oldName, "", _isSchemaTemplate || string.IsNullOrEmpty(contentSchema)));
                _progressLog.Info($"    Casting {tableFile}");
                if (FileWrapper.GetFromFactory().Exists(tableFile) || (oldTableFile != null && FileWrapper.GetFromFactory().Exists(oldTableFile)))
                {
                    var original = LoadOriginalForPreservation(FileWrapper.GetFromFactory().Exists(tableFile) ? tableFile : oldTableFile);
                    ImportTableHelper.PreserveDataDeliveryAndCustomProperties(tableObj, original, IsVariantActive, SourceCanReportXmlCompression);
                        if (_preserveExistingOrder)
                            ImportTableHelper.PreserveListOrder(tableObj, original, _objectOrder);
                }
                ScrubSchemaForTemplate(tableObj, tableFile);
                WritePackageObject(tableFile, tableObj, "tables");
                _stats.Tables++;
            }
            catch (NpgsqlException ex)
            {
                _progressLog.Error($"    ERROR: PostgreSQL error extracting {schema}.{table}: {ex.Message}");
                _stats.TableErrors++;
            }
            catch (JsonException ex)
            {
                _progressLog.Error($"    ERROR: JSON parsing error for {schema}.{table}: {ex.Message}");
                _stats.TableErrors++;
            }
            catch (IOException ex)
            {
                _progressLog.Error($"    ERROR: File write error for {schema}.{table}: {ex.Message}");
                _stats.TableErrors++;
            }
        }
    }

    private void CastPostgreSqlSchemas(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Schemas' AS Folder,
       n.nspname AS FullName,
       'CREATE SCHEMA IF NOT EXISTS ' || QUOTE_IDENT(n.nspname) ||
       ';' AS Code
  FROM pg_namespace n
  WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'public', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%';
";
        PerformPostgreSqlCasting(command, "Schemas");
    }

    /// <summary>
    /// Casts domain types as DECLARATIVE .json rather than a guarded CREATE DOMAIN script (F5).
    /// <para><b>The script this replaces was the bug</b>, and in exactly the shape the enum one was: it
    /// emitted <c>DO $$ IF NOT EXISTS (…) THEN CREATE DOMAIN … $$</c>, and once the domain existed that
    /// guard skipped. Verified on a live server — re-running a guarded create carrying
    /// <c>CHECK (VALUE &gt; 100)</c> left the domain with its original <c>CHECK (VALUE &gt; 0)</c>, silently,
    /// reporting success. There is no <c>CREATE OR REPLACE DOMAIN</c>, so the scripted form could not be
    /// written any other way.</para>
    /// <para>Written into the same <c>Domain Types/</c> folder, which now holds both forms: a <c>.sql</c>
    /// file there still runs through the Objects slot exactly as before, so nothing existing breaks.</para>
    /// </summary>
    private void CastPostgreSqlDomainTypes(IDbCommand command)
    {
        command.CommandText = $@"
SELECT n.nspname AS SchemaName, t.typname AS TypeName
  FROM pg_type t
  JOIN pg_namespace n ON n.oid = t.typnamespace
  WHERE t.typtype = 'd'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
    AND {PostgreSqlNotExtensionMember("pg_type", "t.oid")}
  ORDER BY n.nspname, t.typname;
";
        _progressLog.Info("Casting Domain Type Structures");
        var types = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["SchemaName"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                types.Add((sch, reader["TypeName"].ToString()));
            }
        }

        if (types.Count == 0) return;

        command.CommandText = ResourceLoader.Load("SchemaSmith.GenerateDomainTypeJson.sql", _platform);
        command.ExecuteNonQuery();

        var castPath = Path.Join(_templatePath, ResolveFolderName("Domain Types", ScriptObjectType.DomainTypes));
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var (schema, name) in types)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{name}".ToLower()) && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast Json for domain type {schema}.{name}");
            command.CommandText = $"SELECT \"SchemaSmith\".\"GenerateDomainTypeJSON\"('{EscapeSql(schema)}', '{EscapeSql(name)}')";
            var typeJson = command.ExecuteScalar()?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(typeJson) || typeJson.Trim() == "{}")
            {
                _progressLog.Error($"    No json returned for domain type {schema}.{name}");
                _stats.SkippedObjects++;
                continue;
            }

            var typeObj = JsonText.Deserialize<PostgreSqlDomainType>(typeJson);
            if (_isSchemaTemplate) typeObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            WritePackageObject(file, typeObj, "domaintypes");
            _stats.DomainTypes++;
        }
    }

    /// <summary>
    /// Casts enum types as DECLARATIVE .json rather than a guarded CREATE TYPE script (F5).
    /// <para><b>The script this replaces was the bug.</b> It emitted
    /// <c>DO $$ IF NOT EXISTS (…) THEN CREATE TYPE … $$</c>, and once the type existed that guard skipped
    /// — so a package whose enum gained a value deployed clean and changed nothing, forever. Extracting
    /// the value list declaratively is what makes the difference visible and appliable.</para>
    /// <para>Written into the same <c>Enum Types/</c> folder, which now holds both forms: a <c>.sql</c>
    /// file there still runs through the Objects slot exactly as before, so nothing existing breaks.</para>
    /// </summary>
    private void CastPostgreSqlEnumTypes(IDbCommand command)
    {
        command.CommandText = $@"
SELECT n.nspname AS SchemaName, t.typname AS TypeName
  FROM pg_type t
  JOIN pg_namespace n ON n.oid = t.typnamespace
  WHERE t.typtype = 'e'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
    AND {PostgreSqlNotExtensionMember("pg_type", "t.oid")}
  ORDER BY n.nspname, t.typname;
";
        _progressLog.Info("Casting Enum Type Structures");
        var types = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["SchemaName"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                types.Add((sch, reader["TypeName"].ToString()));
            }
        }

        if (types.Count == 0) return;

        command.CommandText = ResourceLoader.Load("SchemaSmith.GenerateEnumTypeJson.sql", _platform);
        command.ExecuteNonQuery();

        var castPath = Path.Join(_templatePath, ResolveFolderName("Enum Types", ScriptObjectType.EnumTypes));
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var (schema, name) in types)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{name}".ToLower()) && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast Json for enum type {schema}.{name}");
            command.CommandText = $"SELECT \"SchemaSmith\".\"GenerateEnumTypeJSON\"('{EscapeSql(schema)}', '{EscapeSql(name)}')";
            var typeJson = command.ExecuteScalar()?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(typeJson) || typeJson.Trim() == "{}")
            {
                _progressLog.Error($"    No json returned for enum type {schema}.{name}");
                _stats.SkippedObjects++;
                continue;
            }

            var typeObj = JsonText.Deserialize<PostgreSqlEnumType>(typeJson);
            if (_isSchemaTemplate) typeObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            WritePackageObject(file, typeObj, "enumtypes");
            _stats.EnumTypes++;
        }
    }

    private void CastPostgreSqlCompositeTypes(IDbCommand command)
    {
        // Attribute names are quoted and a collation that differs from the type's own is written out; both were lost
        // and a reserved-word attribute made the script fail. The old pg_type_is_visible filter also skipped every
        // composite type in a schema that was not on the search_path, which is any named schema.
        command.CommandText = @"
SELECT 'Composite Types' AS Folder,
       ns.nspname || '.' || t.typname AS FullName,
       E'\nDO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = ' || QUOTE_LITERAL(ns.nspname) || ' AND t.typname = ' || QUOTE_LITERAL(t.typname) || E') THEN\n        CREATE TYPE ' ||
       QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(t.typname) || ' AS (' ||
       STRING_AGG(QUOTE_IDENT(a.attname) || ' ' || FORMAT_TYPE(a.atttypid, a.atttypmod) ||
                  CASE WHEN a.attcollation <> 0 AND a.attcollation <> ty.typcollation
                       THEN ' COLLATE ' || QUOTE_IDENT(cn.nspname) || '.' || QUOTE_IDENT(co.collname) ELSE '' END,
                  ', ' ORDER BY a.attnum) ||
       E');\n    END IF;\nEND\n$$;' AS Code
  FROM pg_type t
  JOIN pg_class c ON t.typrelid = c.oid
  JOIN pg_attribute a ON c.oid = a.attrelid
  JOIN pg_type ty ON ty.oid = a.atttypid
  LEFT JOIN pg_collation co ON co.oid = a.attcollation
  LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
  JOIN pg_namespace ns ON t.typnamespace = ns.oid
  WHERE t.typtype = 'c'
    AND c.relkind = 'c'
    AND a.attnum > 0
    AND NOT a.attisdropped
    AND ns.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND ns.nspname NOT LIKE 'pg_temp_%'
    AND ns.nspname NOT LIKE 'pg_toast_temp_%'
    AND " + PostgreSqlNotExtensionMember("pg_type", "t.oid") + @"
  GROUP BY ns.nspname, t.typname;
";
        PerformPostgreSqlCasting(command, "Composite Types");
    }

    private void CastPostgreSqlCollations(IDbCommand command)
    {
        // The catalog moved the ICU locale twice (collcollate below 15, colliculocale on 15-16, colllocale from 17),
        // added RULES on 16 and the builtin provider on 17, so the text is chosen by the server's version.
        var version = PostgreSqlServerVersionNum(command);
        var locale = version >= 170000 ? "c.colllocale" : version >= 150000 ? "c.colliculocale" : "c.collcollate";
        var rules = version >= 160000 ? "COALESCE(', RULES = ' || QUOTE_LITERAL(c.collicurules), '')" : "''";
        command.CommandText = $@"
SELECT 'Collations' AS Folder,
       n.nspname || '.' || c.collname AS FullName,
       'CREATE COLLATION IF NOT EXISTS ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.collname) || ' (' ||
       CASE c.collprovider
            WHEN 'i' THEN 'PROVIDER = icu, LOCALE = ' || QUOTE_LITERAL(COALESCE({locale}, c.collname)) || {rules}
            WHEN 'b' THEN 'PROVIDER = builtin, LOCALE = ' || QUOTE_LITERAL({locale})
            ELSE 'LC_COLLATE = ' || QUOTE_LITERAL(c.collcollate) || ', LC_CTYPE = ' || QUOTE_LITERAL(c.collctype)
       END ||
       CASE WHEN c.collisdeterministic = false THEN ', DETERMINISTIC = false' ELSE '' END ||
       ');' AS Code
  FROM pg_collation c
  JOIN pg_namespace n ON c.collnamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%'
  WHERE {PostgreSqlNotExtensionMember("pg_collation", "c.oid")};
";
        PerformPostgreSqlCasting(command, "Collations");
    }

    // server_version_num's shape (17.5 is 170005), from the version the driver already read at connect time; a
    // connection that is not Npgsql's (a test double) is treated as the newest server.
    private static int PostgreSqlServerVersionNum(IDbCommand command) =>
        command.Connection is NpgsqlConnection npgsql ? npgsql.PostgreSqlVersion.Major * 10000 + Math.Max(npgsql.PostgreSqlVersion.Minor, 0) : int.MaxValue;

    private void CastPostgreSqlFunctions(IDbCommand command)
    {
        command.CommandText = @"
SELECT CASE p.prokind
            WHEN 'f' THEN CASE WHEN prorettype = (SELECT oid FROM pg_type WHERE typname = 'trigger') THEN 'Trigger ' ELSE '' END || 'Functions'
            WHEN 'w' THEN 'Window Functions'
            END AS Folder,
       n.nspname || '.' || p.proname ||
       CASE WHEN COUNT(*) OVER (PARTITION BY p.pronamespace, p.proname) > 1
            THEN '(' || COALESCE((SELECT STRING_AGG(FORMAT_TYPE(u.t, NULL), ', ' ORDER BY u.n) FROM UNNEST(p.proargtypes::oid[]) WITH ORDINALITY AS u(t, n)), '') || ')' ELSE '' END AS FullName,
       n.nspname || '.' || p.proname AS BaseName,
       PG_GET_FUNCTIONDEF(p.oid) AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%'
  WHERE p.prokind IN ('f', 'w')
    AND " + PostgreSqlNotEngineOwned("pg_proc", "p.oid") + @";
";
        PerformPostgreSqlCasting(command, "Functions");
    }

    private void CastPostgreSqlAggregates(IDbCommand command)
    {
        // pg_dump's shape: the declared argument list, every support function schema-qualified, the quoted initial
        // condition, and the final, combine, serial, moving-aggregate, sort-operator, parallel and hypothetical
        // clauses. The old text used the state type as the argument list and left the condition unquoted.
        command.CommandText = @"
WITH fn AS (SELECT f.oid, QUOTE_IDENT(fn.nspname) || '.' || QUOTE_IDENT(f.proname) AS qname
              FROM pg_proc f JOIN pg_namespace fn ON fn.oid = f.pronamespace)
SELECT 'Aggregates' AS Folder,
       n.nspname || '.' || p.proname ||
       CASE WHEN COUNT(*) OVER (PARTITION BY p.pronamespace, p.proname) > 1
            THEN '(' || COALESCE((SELECT STRING_AGG(FORMAT_TYPE(u.t, NULL), ', ' ORDER BY u.n) FROM UNNEST(p.proargtypes::oid[]) WITH ORDINALITY AS u(t, n)), '') || ')' ELSE '' END AS FullName,
       n.nspname || '.' || p.proname AS BaseName,
       'CREATE OR REPLACE AGGREGATE ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(p.proname) || '(' || PG_GET_FUNCTION_IDENTITY_ARGUMENTS(p.oid) || ') (' ||
       E'\n    SFUNC = ' || sf.qname ||
       E',\n    STYPE = ' || FORMAT_TYPE(a.aggtranstype, NULL) ||
       CASE WHEN a.aggtransspace > 0 THEN E',\n    SSPACE = ' || a.aggtransspace ELSE '' END ||
       COALESCE(E',\n    FINALFUNC = ' || ff.qname ||
                CASE WHEN a.aggfinalextra THEN E',\n    FINALFUNC_EXTRA' ELSE '' END ||
                CASE WHEN a.aggkind = 'n' AND a.aggfinalmodify = 's' THEN E',\n    FINALFUNC_MODIFY = SHAREABLE'
                     WHEN a.aggkind = 'n' AND a.aggfinalmodify = 'w' THEN E',\n    FINALFUNC_MODIFY = READ_WRITE'
                     WHEN a.aggkind <> 'n' AND a.aggfinalmodify = 'r' THEN E',\n    FINALFUNC_MODIFY = READ_ONLY'
                     WHEN a.aggkind <> 'n' AND a.aggfinalmodify = 's' THEN E',\n    FINALFUNC_MODIFY = SHAREABLE'
                     ELSE '' END, '') ||
       COALESCE(E',\n    COMBINEFUNC = ' || cf.qname, '') ||
       COALESCE(E',\n    SERIALFUNC = ' || srf.qname, '') ||
       COALESCE(E',\n    DESERIALFUNC = ' || dsf.qname, '') ||
       COALESCE(E',\n    INITCOND = ' || QUOTE_LITERAL(a.agginitval), '') ||
       COALESCE(E',\n    MSFUNC = ' || msf.qname || E',\n    MINVFUNC = ' || minv.qname || E',\n    MSTYPE = ' || FORMAT_TYPE(a.aggmtranstype, NULL) ||
                CASE WHEN a.aggmtransspace > 0 THEN E',\n    MSSPACE = ' || a.aggmtransspace ELSE '' END ||
                COALESCE(E',\n    MFINALFUNC = ' || mff.qname ||
                         CASE WHEN a.aggmfinalextra THEN E',\n    MFINALFUNC_EXTRA' ELSE '' END ||
                         CASE WHEN a.aggmfinalmodify = 's' THEN E',\n    MFINALFUNC_MODIFY = SHAREABLE'
                              WHEN a.aggmfinalmodify = 'w' THEN E',\n    MFINALFUNC_MODIFY = READ_WRITE' ELSE '' END, '') ||
                COALESCE(E',\n    MINITCOND = ' || QUOTE_LITERAL(a.aggminitval), ''), '') ||
       COALESCE(E',\n    SORTOP = OPERATOR(' || QUOTE_IDENT(opn.nspname) || '.' || op.oprname || ')', '') ||
       CASE p.proparallel WHEN 's' THEN E',\n    PARALLEL = SAFE' WHEN 'r' THEN E',\n    PARALLEL = RESTRICTED' ELSE '' END ||
       CASE WHEN a.aggkind = 'h' THEN E',\n    HYPOTHETICAL' ELSE '' END ||
       E'\n);' AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
  JOIN pg_aggregate a ON a.aggfnoid = p.oid
  JOIN fn sf ON sf.oid = a.aggtransfn
  LEFT JOIN fn ff ON ff.oid = a.aggfinalfn
  LEFT JOIN fn cf ON cf.oid = a.aggcombinefn
  LEFT JOIN fn srf ON srf.oid = a.aggserialfn
  LEFT JOIN fn dsf ON dsf.oid = a.aggdeserialfn
  LEFT JOIN fn msf ON msf.oid = a.aggmtransfn
  LEFT JOIN fn minv ON minv.oid = a.aggminvtransfn
  LEFT JOIN fn mff ON mff.oid = a.aggmfinalfn
  LEFT JOIN pg_operator op ON op.oid = a.aggsortop
  LEFT JOIN pg_namespace opn ON opn.oid = op.oprnamespace
  WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
    AND " + PostgreSqlNotExtensionMember("pg_proc", "p.oid") + @";
";
        PerformPostgreSqlCasting(command, "Aggregates");
    }

    private void CastPostgreSqlProcedures(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Procedures' AS Folder,
       n.nspname || '.' || p.proname ||
       CASE WHEN COUNT(*) OVER (PARTITION BY p.pronamespace, p.proname) > 1
            THEN '(' || COALESCE((SELECT STRING_AGG(FORMAT_TYPE(u.t, NULL), ', ' ORDER BY u.n) FROM UNNEST(p.proargtypes::oid[]) WITH ORDINALITY AS u(t, n)), '') || ')' ELSE '' END AS FullName,
       n.nspname || '.' || p.proname AS BaseName,
       PG_GET_FUNCTIONDEF(p.oid) AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%'
  WHERE p.prokind = 'p'
    AND " + PostgreSqlNotExtensionMember("pg_proc", "p.oid") + @";
";
        PerformPostgreSqlCasting(command, "Procedures");
    }

    /// <summary>
    /// Casts sequences as DECLARATIVE .json rather than a CREATE SEQUENCE script (F5).
    /// <para><b>This also fixes a real extraction bug.</b> The previous filter excluded only
    /// <c>deptype = 'i'</c>, which covers an <c>IDENTITY</c> column's sequence but NOT a <c>serial</c>
    /// column's — that one is <c>'a'</c> (auto). So every <c>serial</c> column's engine-generated sequence
    /// was extracted as a standalone object. On deploy the package then created it first, and
    /// <c>CREATE TABLE … serial</c>, finding the name taken, generated <c>&lt;name&gt;1</c> and pointed the
    /// column at THAT — leaving an orphan sequence behind and a column whose sequence name no longer
    /// matches the source. Verified end to end. Both deptypes are excluded now.</para>
    /// <para>The current value is never captured: it is data, and a package carrying it would reset a live
    /// sequence on the next deploy.</para>
    /// </summary>
    private void CastPostgreSqlSequences(IDbCommand command)
    {
        command.CommandText = @"
SELECT n.nspname AS SchemaName, s.relname AS SequenceName
  FROM pg_class s
  JOIN pg_namespace n ON n.oid = s.relnamespace
  WHERE s.relkind = 'S'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
    AND NOT EXISTS (SELECT 1 FROM pg_depend d
                    WHERE d.objid = s.oid
                      AND d.classid = 'pg_class'::regclass
                      AND d.deptype IN ('i', 'a', 'e'))
  ORDER BY n.nspname, s.relname;
";
        _progressLog.Info("Casting Sequence Structures");
        var sequences = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["SchemaName"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                sequences.Add((sch, reader["SequenceName"].ToString()));
            }
        }

        if (sequences.Count == 0) return;

        command.CommandText = ResourceLoader.Load("SchemaSmith.GenerateSequenceJson.sql", _platform);
        command.ExecuteNonQuery();

        var castPath = Path.Join(_templatePath, ResolveFolderName("Sequences", ScriptObjectType.Sequences));
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var (schema, name) in sequences)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{name}".ToLower()) && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast Json for sequence {schema}.{name}");
            command.CommandText = $"SELECT \"SchemaSmith\".\"GenerateSequenceJSON\"('{EscapeSql(schema)}', '{EscapeSql(name)}')";
            var seqJson = command.ExecuteScalar()?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(seqJson) || seqJson.Trim() == "{}")
            {
                _progressLog.Error($"    No json returned for sequence {schema}.{name}");
                _stats.SkippedObjects++;
                continue;
            }

            var seqObj = JsonText.Deserialize<PostgreSqlSequence>(seqJson);
            if (_isSchemaTemplate) seqObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            WritePackageObject(file, seqObj, "sequences");
            _stats.Sequences++;
        }
    }

    private void CastPostgreSqlPublications(IDbCommand command)
    {
        // The catalog grew by version: publish_via_partition_root on 13; a row filter, a column list and TABLES IN
        // SCHEMA on 15; publish_generated_columns on 18. The text is chosen by the server's version, and a publication
        // with no tables is written without a FOR clause. CREATE PUBLICATION has no IF NOT EXISTS form, so the guarded
        // create goes through a DO $$ ... $$ block. The column list comes from pg_publication_rel.prattrs, which is
        // NULL when none was declared; pg_publication_tables.attnames lists every column either way.
        // The guard skips the create once the publication exists, so ALTER PUBLICATION ... SET follows it for the tables
        // and options. SET TABLE keeps the members it already has, and the options are written out in full, so a change
        // back to a default is applied too. FOR ALL TABLES cannot be altered.
        var version = PostgreSqlServerVersionNum(command);
        var columnList = version >= 150000
            ? "COALESCE(' (' || (SELECT STRING_AGG(QUOTE_IDENT(att.attname), ', ' ORDER BY att.attnum) FROM pg_attribute att WHERE att.attrelid = pr.prrelid AND att.attnum = ANY(pr.prattrs)) || ')', '')"
            : "''";
        var rowFilter = version >= 150000 ? "COALESCE(' WHERE ' || PG_GET_EXPR(pr.prqual, pr.prrelid), '')" : "''";
        var schemas = version >= 150000
            ? "(SELECT STRING_AGG(QUOTE_IDENT(sn.nspname), ', ') FROM pg_publication_namespace pn JOIN pg_namespace sn ON sn.oid = pn.pnnspid WHERE pn.pnpubid = p.oid)"
            : "NULL::text";
        var viaRoot = version >= 130000 ? "CASE WHEN p.pubviaroot THEN ', publish_via_partition_root = true' ELSE '' END" : "''";
        var generated = version >= 180000 ? "CASE WHEN p.pubgencols = 's' THEN ', publish_generated_columns = stored' ELSE '' END" : "''";
        var setViaRoot = version >= 130000 ? "', publish_via_partition_root = ' || CASE WHEN p.pubviaroot THEN 'true' ELSE 'false' END" : "''";
        var setGenerated = version >= 180000 ? "', publish_generated_columns = ' || CASE WHEN p.pubgencols = 's' THEN 'stored' ELSE 'none' END" : "''";
        const string publish = @"QUOTE_LITERAL(CONCAT_WS(', ',
            CASE WHEN p.pubinsert THEN 'insert' END,
            CASE WHEN p.pubupdate THEN 'update' END,
            CASE WHEN p.pubdelete THEN 'delete' END,
            CASE WHEN p.pubtruncate THEN 'truncate' END))";
        command.CommandText = $@"
SELECT 'Publications' AS Folder,
       p.pubname AS FullName,
       E'\nDO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = ' || QUOTE_LITERAL(p.pubname) ||
       E') THEN\n        CREATE PUBLICATION ' || QUOTE_IDENT(p.pubname) ||
       CASE WHEN p.puballtables THEN ' FOR ALL TABLES'
            WHEN t.tables IS NULL AND s.schemas IS NULL THEN ''
            ELSE ' FOR ' || CONCAT_WS(', ', 'TABLE ' || t.tables, 'TABLES IN SCHEMA ' || s.schemas)
       END ||
       ' WITH (publish = ' || {publish} ||
       {viaRoot} || {generated} ||
       E');\n    END IF;\nEND\n$$;' ||
       CASE WHEN p.puballtables OR (t.tables IS NULL AND s.schemas IS NULL) THEN ''
            ELSE E'\nALTER PUBLICATION ' || QUOTE_IDENT(p.pubname) || ' SET ' || CONCAT_WS(', ', 'TABLE ' || t.tables, 'TABLES IN SCHEMA ' || s.schemas) || ';'
       END ||
       E'\nALTER PUBLICATION ' || QUOTE_IDENT(p.pubname) || ' SET (publish = ' || {publish} || {setViaRoot} || {setGenerated} || ');' AS Code
  FROM pg_publication p
  LEFT JOIN LATERAL (
       SELECT STRING_AGG(QUOTE_IDENT(rn.nspname) || '.' || QUOTE_IDENT(rc.relname) || {columnList} || {rowFilter}, ', ' ORDER BY rn.nspname, rc.relname) AS tables
         FROM pg_publication_rel pr
         JOIN pg_class rc ON rc.oid = pr.prrelid
         JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        WHERE pr.prpubid = p.oid) t ON true
  LEFT JOIN LATERAL (SELECT {schemas} AS schemas) s ON true
 WHERE p.pubname NOT LIKE 'pg\_%';
";
        PerformPostgreSqlCasting(command, "Publications");
    }

    // pg_dump's shape: the operator class and collation only where they differ from the subtype's defaults, the
    // support functions, and (14+) a multirange type not named by the engine's rule (the first "range" becomes
    // "multirange", or "_multirange" is appended). CREATE TYPE has no IF NOT EXISTS, so the create is guarded; ALTER TYPE
    // cannot change a range type, so an existing one is left as it is.
    private void CastPostgreSqlRangeTypes(IDbCommand command)
    {
        var multirange = PostgreSqlServerVersionNum(command) >= 140000
            ? @"COALESCE((SELECT ', MULTIRANGE_TYPE_NAME = ' || QUOTE_IDENT(mn.nspname) || '.' || QUOTE_IDENT(mt.typname)
                   FROM pg_type mt JOIN pg_namespace mn ON mn.oid = mt.typnamespace
                  WHERE mt.oid = r.rngmultitypid
                    AND (mt.typnamespace <> t.typnamespace
                         OR mt.typname <> CASE WHEN STRPOS(t.typname, 'range') > 0 THEN REGEXP_REPLACE(t.typname, 'range', 'multirange') ELSE t.typname || '_multirange' END)), '')"
            : "''";
        command.CommandText = $@"
SELECT 'Range Types' AS Folder,
       n.nspname || '.' || t.typname AS FullName,
       E'DO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = ' || QUOTE_LITERAL(n.nspname) || ' AND t.typname = ' || QUOTE_LITERAL(t.typname) || E') THEN\n        CREATE TYPE ' ||
       QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(t.typname) || ' AS RANGE (SUBTYPE = ' || FORMAT_TYPE(r.rngsubtype, NULL) ||
       CASE WHEN NOT oc.opcdefault THEN ', SUBTYPE_OPCLASS = ' || QUOTE_IDENT(ocn.nspname) || '.' || QUOTE_IDENT(oc.opcname) ELSE '' END ||
       CASE WHEN r.rngcollation <> 0 AND r.rngcollation <> st.typcollation
            THEN ', COLLATION = ' || QUOTE_IDENT(cn.nspname) || '.' || QUOTE_IDENT(co.collname) ELSE '' END ||
       COALESCE(', CANONICAL = ' || (SELECT QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = r.rngcanonical), '') ||
       COALESCE(', SUBTYPE_DIFF = ' || (SELECT QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = r.rngsubdiff), '') ||
       {multirange} ||
       E');\n    END IF;\nEND\n$$;' AS Code
  FROM pg_range r
  JOIN pg_type t ON t.oid = r.rngtypid
  JOIN pg_namespace n ON n.oid = t.typnamespace
  JOIN pg_type st ON st.oid = r.rngsubtype
  JOIN pg_opclass oc ON oc.oid = r.rngsubopc
  JOIN pg_namespace ocn ON ocn.oid = oc.opcnamespace
  LEFT JOIN pg_collation co ON co.oid = r.rngcollation
  LEFT JOIN pg_namespace cn ON cn.oid = co.collnamespace
 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
   AND n.nspname NOT LIKE 'pg_temp_%'
   AND n.nspname NOT LIKE 'pg_toast_temp_%'
   AND {PostgreSqlNotExtensionMember("pg_type", "t.oid")};
";
        PerformPostgreSqlCasting(command, "Range Types");
    }

    // A configuration is a parser and a token-to-dictionaries map. The script creates any dictionary it maps to outside
    // pg_catalog (each guarded, so two configurations sharing one are fine), creates the configuration where it is
    // missing, then replaces every mapping, so a re-run puts back a mapping changed by hand. The parser is create-time.
    private void CastPostgreSqlTextSearchConfigurations(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Text Search Configurations' AS Folder,
       n.nspname || '.' || c.cfgname AS FullName,
       COALESCE((SELECT STRING_AGG(E'DO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_ts_dict d JOIN pg_namespace n ON n.oid = d.dictnamespace WHERE n.nspname = ' || QUOTE_LITERAL(dn.nspname) || ' AND d.dictname = ' || QUOTE_LITERAL(d.dictname) || E') THEN\n        CREATE TEXT SEARCH DICTIONARY ' ||
                                   QUOTE_IDENT(dn.nspname) || '.' || QUOTE_IDENT(d.dictname) || ' (TEMPLATE = ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tp.tmplname) ||
                                   COALESCE(', ' || d.dictinitoption, '') || E');\n    END IF;\nEND\n$$;\n', '' ORDER BY dn.nspname, d.dictname)
                   FROM pg_ts_dict d
                   JOIN pg_namespace dn ON dn.oid = d.dictnamespace
                   JOIN pg_ts_template tp ON tp.oid = d.dicttemplate
                   JOIN pg_namespace tn ON tn.oid = tp.tmplnamespace
                  WHERE dn.nspname <> 'pg_catalog'
                    AND d.oid IN (SELECT m.mapdict FROM pg_ts_config_map m WHERE m.mapcfg = c.oid)), '') ||
       E'DO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_ts_config c JOIN pg_namespace n ON n.oid = c.cfgnamespace WHERE n.nspname = ' || QUOTE_LITERAL(n.nspname) || ' AND c.cfgname = ' || QUOTE_LITERAL(c.cfgname) || E') THEN\n        CREATE TEXT SEARCH CONFIGURATION ' ||
       QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.cfgname) || ' (PARSER = ' || QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.prsname) || E');\n    END IF;\nEND\n$$;\n' ||
       'ALTER TEXT SEARCH CONFIGURATION ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.cfgname) || ' DROP MAPPING IF EXISTS FOR ' ||
       (SELECT STRING_AGG(QUOTE_IDENT(tt.alias), ', ' ORDER BY tt.tokid) FROM TS_TOKEN_TYPE(c.cfgparser) tt) || E';\n' ||
       COALESCE((SELECT STRING_AGG('ALTER TEXT SEARCH CONFIGURATION ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.cfgname) || ' ADD MAPPING FOR ' || g.tokens || ' WITH ' || g.dicts || E';\n', '' ORDER BY g.first_token)
                   FROM (SELECT STRING_AGG(QUOTE_IDENT(tt.alias), ', ' ORDER BY tt.tokid) AS tokens, MIN(tt.tokid) AS first_token, md.dicts
                           FROM (SELECT m.maptokentype,
                                        STRING_AGG(QUOTE_IDENT(dn.nspname) || '.' || QUOTE_IDENT(d.dictname), ', ' ORDER BY m.mapseqno) AS dicts
                                   FROM pg_ts_config_map m
                                   JOIN pg_ts_dict d ON d.oid = m.mapdict
                                   JOIN pg_namespace dn ON dn.oid = d.dictnamespace
                                  WHERE m.mapcfg = c.oid
                                  GROUP BY m.maptokentype) md
                           JOIN TS_TOKEN_TYPE(c.cfgparser) tt ON tt.tokid = md.maptokentype
                          GROUP BY md.dicts) g), '') AS Code
  FROM pg_ts_config c
  JOIN pg_namespace n ON n.oid = c.cfgnamespace
  JOIN pg_ts_parser p ON p.oid = c.cfgparser
  JOIN pg_namespace pn ON pn.oid = p.prsnamespace
 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
   AND n.nspname NOT LIKE 'pg_temp_%'
   AND n.nspname NOT LIKE 'pg_toast_temp_%'
   AND " + PostgreSqlNotExtensionMember("pg_ts_config", "c.oid") + @";
";
        PerformPostgreSqlCasting(command, "Text Search Configurations");
    }

    // One file per signature, named schema.op(left, right) with NONE for a missing side. CREATE OPERATOR has no IF NOT
    // EXISTS; the guard looks for a real operator, not the shell another operator's COMMUTATOR or NEGATOR leaves, because
    // CREATE OPERATOR fills a shell in. An existing operator is left as it is (ALTER OPERATOR changes only some options).
    private void CastPostgreSqlOperators(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Operators' AS Folder,
       n.nspname || '.' || o.oprname || '(' || CASE WHEN o.oprleft = 0 THEN 'NONE' ELSE FORMAT_TYPE(o.oprleft, NULL) END || ', ' || CASE WHEN o.oprright = 0 THEN 'NONE' ELSE FORMAT_TYPE(o.oprright, NULL) END || ')' AS FullName,
       E'DO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_operator o JOIN pg_namespace n ON n.oid = o.oprnamespace WHERE n.nspname = ' || QUOTE_LITERAL(n.nspname) || ' AND o.oprname = ' || QUOTE_LITERAL(o.oprname) ||
       ' AND o.oprleft = ' || CASE WHEN o.oprleft = 0 THEN '0' ELSE QUOTE_LITERAL(FORMAT_TYPE(o.oprleft, NULL)) || '::regtype' END ||
       ' AND o.oprright = ' || CASE WHEN o.oprright = 0 THEN '0' ELSE QUOTE_LITERAL(FORMAT_TYPE(o.oprright, NULL)) || '::regtype' END ||
       E' AND o.oprcode <> 0) THEN\n        CREATE OPERATOR ' || QUOTE_IDENT(n.nspname) || '.' || o.oprname || ' (FUNCTION = ' ||
       (SELECT QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = o.oprcode) ||
       CASE WHEN o.oprleft <> 0 THEN ', LEFTARG = ' || FORMAT_TYPE(o.oprleft, NULL) ELSE '' END ||
       CASE WHEN o.oprright <> 0 THEN ', RIGHTARG = ' || FORMAT_TYPE(o.oprright, NULL) ELSE '' END ||
       COALESCE((SELECT ', COMMUTATOR = OPERATOR(' || QUOTE_IDENT(cn.nspname) || '.' || co.oprname || ')' FROM pg_operator co JOIN pg_namespace cn ON cn.oid = co.oprnamespace WHERE co.oid = o.oprcom), '') ||
       COALESCE((SELECT ', NEGATOR = OPERATOR(' || QUOTE_IDENT(nn.nspname) || '.' || ng.oprname || ')' FROM pg_operator ng JOIN pg_namespace nn ON nn.oid = ng.oprnamespace WHERE ng.oid = o.oprnegate), '') ||
       COALESCE((SELECT ', RESTRICT = ' || QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = o.oprrest), '') ||
       COALESCE((SELECT ', JOIN = ' || QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = o.oprjoin), '') ||
       CASE WHEN o.oprcanhash THEN ', HASHES' ELSE '' END ||
       CASE WHEN o.oprcanmerge THEN ', MERGES' ELSE '' END ||
       E');\n    END IF;\nEND\n$$;' AS Code
  FROM pg_operator o
  JOIN pg_namespace n ON n.oid = o.oprnamespace
 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
   AND n.nspname NOT LIKE 'pg_temp_%'
   AND n.nspname NOT LIKE 'pg_toast_temp_%'
   AND o.oprcode <> 0
   AND " + PostgreSqlNotExtensionMember("pg_operator", "o.oid") + @";
";
        PerformPostgreSqlCasting(command, "Operators");
    }

    // A cast has no schema; it is named by its two types. Only user casts (past the bootstrap oid range) and not the
    // multirange casts the engine creates with a range type. CREATE CAST has no IF NOT EXISTS, so the create is guarded.
    private void CastPostgreSqlCasts(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Casts' AS Folder,
       FORMAT_TYPE(c.castsource, NULL) || ' AS ' || FORMAT_TYPE(c.casttarget, NULL) AS FullName,
       E'DO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_cast WHERE castsource = ' || QUOTE_LITERAL(FORMAT_TYPE(c.castsource, NULL)) || '::regtype AND casttarget = ' || QUOTE_LITERAL(FORMAT_TYPE(c.casttarget, NULL)) ||
       E'::regtype) THEN\n        CREATE CAST (' || FORMAT_TYPE(c.castsource, NULL) || ' AS ' || FORMAT_TYPE(c.casttarget, NULL) || ') ' ||
       CASE c.castmethod
            WHEN 'f' THEN 'WITH FUNCTION ' || (SELECT QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) || '(' || OIDVECTORTYPES(p.proargtypes) || ')' FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = c.castfunc)
            WHEN 'i' THEN 'WITH INOUT'
            ELSE 'WITHOUT FUNCTION' END ||
       CASE c.castcontext WHEN 'a' THEN ' AS ASSIGNMENT' WHEN 'i' THEN ' AS IMPLICIT' ELSE '' END ||
       E';\n    END IF;\nEND\n$$;' AS Code
  FROM pg_cast c
 WHERE c.oid >= 16384
   AND " + PostgreSqlNotEngineOwned("pg_cast", "c.oid") + @";
";
        PerformPostgreSqlCasting(command, "Casts");
    }

    // Dropped and re-created on every deploy, like table triggers, then set to its enabled state, which is not part of
    // the definition. Creating one needs a superuser.
    private void CastPostgreSqlEventTriggers(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Event Triggers' AS Folder,
       e.evtname AS FullName,
       'DROP EVENT TRIGGER IF EXISTS ' || QUOTE_IDENT(e.evtname) || E';\nCREATE EVENT TRIGGER ' || QUOTE_IDENT(e.evtname) || ' ON ' || QUOTE_IDENT(e.evtevent) ||
       COALESCE(' WHEN TAG IN (' || (SELECT STRING_AGG(QUOTE_LITERAL(u.tag), ', ' ORDER BY u.ord) FROM UNNEST(e.evttags) WITH ORDINALITY AS u(tag, ord)) || ')', '') ||
       E'\n    EXECUTE FUNCTION ' || (SELECT QUOTE_IDENT(pn.nspname) || '.' || QUOTE_IDENT(p.proname) FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace WHERE p.oid = e.evtfoid) || '();' ||
       CASE e.evtenabled
            WHEN 'D' THEN E'\nALTER EVENT TRIGGER ' || QUOTE_IDENT(e.evtname) || ' DISABLE;'
            WHEN 'R' THEN E'\nALTER EVENT TRIGGER ' || QUOTE_IDENT(e.evtname) || ' ENABLE REPLICA;'
            WHEN 'A' THEN E'\nALTER EVENT TRIGGER ' || QUOTE_IDENT(e.evtname) || ' ENABLE ALWAYS;'
            ELSE '' END AS Code
  FROM pg_event_trigger e
 WHERE " + PostgreSqlNotExtensionMember("pg_event_trigger", "e.oid") + @";
";
        PerformPostgreSqlCasting(command, "Event Triggers");
    }

    private void CastPostgreSqlRules(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Rules' AS Folder,
       ns.nspname || '.' || c.relname || '.' || r.rulename AS FullName,
       REPLACE(PG_GET_RULEDEF(r.oid), 'CREATE RULE', 'CREATE OR REPLACE RULE') AS Code
  FROM pg_rewrite r
  JOIN pg_class c ON r.ev_class = c.oid
  JOIN pg_namespace ns ON c.relnamespace = ns.oid
  WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND ns.nspname NOT LIKE 'pg_temp_%'
    AND ns.nspname NOT LIKE 'pg_toast_temp_%'
    AND r.rulename != '_RETURN'
    AND c.reloftype = 0
    AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = c.oid)
    AND " + PostgreSqlNotExtensionMember("pg_rewrite", "r.oid") + @";
";
        PerformPostgreSqlCasting(command, "Rules");
    }

    private void CastPostgreSqlTriggers(IDbCommand command)
    {
        // DROP IF EXISTS then the engine's own CREATE: CREATE OR REPLACE TRIGGER is 14+ and failed on the 12 floor, a
        // constraint trigger re-run as a plain CREATE failed on the second deploy, and a disabled or replica trigger
        // came back enabled because tgenabled is not part of the definition.
        command.CommandText = @"
SELECT 'Triggers' AS Folder,
       ns.nspname || '.' || tbl.relname || '.' || t.tgname AS FullName,
       'DROP TRIGGER IF EXISTS ' || QUOTE_IDENT(t.tgname) || ' ON ' || QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(tbl.relname) || E';\n' ||
       PG_GET_TRIGGERDEF(t.oid) || ';' ||
       CASE t.tgenabled
            WHEN 'D' THEN E'\nALTER TABLE ' || QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(tbl.relname) || ' DISABLE TRIGGER ' || QUOTE_IDENT(t.tgname) || ';'
            WHEN 'R' THEN E'\nALTER TABLE ' || QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(tbl.relname) || ' ENABLE REPLICA TRIGGER ' || QUOTE_IDENT(t.tgname) || ';'
            WHEN 'A' THEN E'\nALTER TABLE ' || QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(tbl.relname) || ' ENABLE ALWAYS TRIGGER ' || QUOTE_IDENT(t.tgname) || ';'
            ELSE '' END AS Code
  FROM pg_trigger t
  JOIN pg_class tbl ON t.tgrelid = tbl.oid
  JOIN pg_namespace ns ON tbl.relnamespace = ns.oid
  WHERE NOT t.tgisinternal
    AND ns.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND ns.nspname NOT LIKE 'pg_temp_%'
    AND ns.nspname NOT LIKE 'pg_toast_temp_%'
    -- A partitioned table and its partitions are refused at extraction (partitioning is not modelled), so a trigger
    -- on either -- including the copies the engine clones onto each partition -- would name a table the package
    -- does not contain.
    AND tbl.relkind <> 'p'
    AND NOT tbl.relispartition
    AND tbl.reloftype = 0
    AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = tbl.oid)
    AND " + PostgreSqlNotExtensionMember("pg_trigger", "t.oid") + @";
";
        PerformPostgreSqlCasting(command, "Triggers");
    }

    private void CastPostgreSqlViews(IDbCommand command)
    {
        // The view's options (security_barrier, security_invoker, check_option) are stored as reloptions and are
        // written back in the WITH clause; CREATE OR REPLACE VIEW without them resets them, which is how a
        // security_invoker view lost its setting on every redeploy.
        command.CommandText = @"
SELECT 'Views' AS Folder,
       n.nspname || '.' || c.relname AS FullName,
       'CREATE OR REPLACE VIEW ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.relname) ||
       COALESCE(' WITH (' || ARRAY_TO_STRING(c.reloptions, ', ') || ')', '') ||
       E' AS\n' || PG_GET_VIEWDEF(c.oid, true) AS Code
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relkind = 'v'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
    AND " + PostgreSqlNotExtensionMember("pg_class", "c.oid") + @";
";
        PerformPostgreSqlCasting(command, "Views");
    }

    private void CastPostgreSqlMaterializedViews(IDbCommand command)
    {
        command.CommandText = $@"
SELECT mv.schemaname, mv.matviewname
  FROM pg_matviews mv
  JOIN pg_class c ON c.relname = mv.matviewname
                 AND c.relnamespace = (SELECT n.oid FROM pg_namespace n WHERE n.nspname = mv.schemaname)
  WHERE mv.schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND mv.schemaname NOT LIKE 'pg_temp_%'
    AND mv.schemaname NOT LIKE 'pg_toast_temp_%'
    AND {PostgreSqlNotExtensionMember("pg_class", "c.oid")}
  ORDER BY mv.schemaname, mv.matviewname;
";

        _progressLog.Info("Casting Materialized View Structures");
        var matViews = new List<(string Schema, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["schemaname"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                matViews.Add((sch, reader["matviewname"].ToString()));
            }
        }

        if (matViews.Count == 0) return;

        // Install the extraction function
        command.CommandText = ResourceLoader.Load("SchemaSmith.GenerateMaterializedViewJson.sql", _platform);
        command.ExecuteNonQuery();

        var castPath = Path.Combine(_templatePath, "Materialized Views");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var (schema, name) in matViews)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{name}".ToLower()) && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast Json for {schema}.{name}");
            command.CommandText = $"SELECT \"SchemaSmith\".\"GenerateMaterializedViewJson\"('{EscapeSql(schema)}', '{EscapeSql(name)}')";
            var viewJson = command.ExecuteScalar()?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(viewJson) || viewJson.Trim().Equals("{}"))
            {
                _progressLog.Error($"    No json returned for {schema}.{name}");
                _stats.SkippedObjects++;
                continue;
            }
            var viewObj = JsonText.Deserialize<PostgreSqlMaterializedView>(viewJson);
            var viewFile = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            // Schema-template mode (design §7.2 / §7.3): strip the Schema field; rewrite the
            // materialized view's Definition body AND any contained filtered-index FilterExpression
            // so source-schema references collapse to {{SchemaName}}. Capture audit hits.
            if (_isSchemaTemplate)
            {
                viewObj.Schema = null;
                var relPath = ResolveAuditRelativePath(viewFile);
                viewObj.Definition = RewriteJsonExpression(viewObj.Definition, relPath, $"MaterializedView.Definition '{viewObj.Name}'");
                foreach (var idx in viewObj.Indexes ?? new List<PostgreSqlIndex>())
                    idx.FilterExpression = RewriteJsonExpression(idx.FilterExpression, relPath, $"Index.FilterExpression '{idx.Name}'");
            }
            _progressLog.Info($"    Casting {viewFile}");
            WritePackageObject(viewFile, viewObj, "materializedviews");
            _stats.MaterializedViews++;
        }
    }

    // An overloaded routine is named with its argument types (public.f(integer)), so listing public.f, or f, takes every
    // overload as it takes a routine that has none; the full signature takes that one alone.
    private bool IsListedToCast(string fullName)
    {
        var open = fullName.IndexOf('(');
        var baseName = open > 0 ? fullName[..open] : fullName;
        return new[] { fullName, baseName }.Any(n =>
            _objectsToCast.Contains(n.ToLower()) || _objectsToCast.Contains(Identifier.SplitQualifiedName(n, _platform).Name.ToLower()));
    }

    private void PerformPostgreSqlCasting(IDbCommand command, string castType)
    {
        _progressLog.Info($"Casting {castType}");

        var records = new List<(string CastPath, string FullName, string FileName, string Script, string FolderName, bool Skip)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var castPath = Path.Combine(_templatePath, reader["Folder"].ToString());
                DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
                var fullName = reader["FullName"].ToString();
                if (_objectsToCast.Length > 0 && !IsListedToCast(fullName)) continue;

                // Schema-template mode: skip objects outside the source schema; emit unqualified filenames.
                // Delimiter-aware split so a dotted delimited schema name ([my.schema]) is not mis-split (#272).
                var (qualifiedSchema, objectName) = Identifier.SplitQualifiedName(fullName, _platform);
                if (_isSchemaTemplate && qualifiedSchema != null && !ShouldExtractFromSchema(qualifiedSchema)) continue;

                var outputName = _isSchemaTemplate && qualifiedSchema != null
                    ? EncodeFileName(objectName, ".sql")
                    : EncodeFullName(fullName, ".sql");
                var fileName = ResolveOutputPath(castPath, outputName);
                var folderName = reader["Folder"].ToString();
                RetireUnsuffixedRoutineFile(reader, castPath, fullName, fileName);
                if (ShouldSkipKnownBadScript(fileName)) { IncrementStatForFolder(folderName); continue; }
                var script = string.Join("\r\n", reader["Code"].ToString());
                records.Add((castPath, fullName, fileName, script, folderName, false));
            }
        }

        foreach (var (_, _, fileName, script, folderName, _) in records)
        {
            var body = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, body);
            var objectType = ScriptFolderTypeInference.InferFromFolderName(folderName);
            ValidateAndHandleScript(command.Connection, fileName, body, objectType);
            IncrementStatForFolder(folderName);
        }

        foreach (var (castPath, folderName) in records.Select(r => (r.CastPath, r.FolderName)).Distinct())
        {
            var objectType = ScriptFolderTypeInference.InferFromFolderName(folderName);
            if (RepositoryHelper.FoldersWrittenOnUse.Contains(objectType)) DeclareFolderWrittenOnUse(objectType, castPath);
        }
    }

    #endregion

    #region MySQL Extraction

    private void CastMySqlObjects(string targetSchema)
    {
        try
        {
            using var connection = GetConnection(targetSchema);
            try
            {
                using var command = connection.CreateCommand();
                _progressLog.Info("Kindling The Forge");
                ForgeKindler.KindleTheForge(command, _platform, allowReadOnlyTarget: true);

                // The routine-scripting queries below build text with || and backslash escapes. Set only after the kindle,
                // which creates the procedures under the server's own mode, so both tools stamp the same kindle.
                using var sqlMode = MySqlSessionSettings.UseParseNeutralSqlMode(command, "PIPES_AS_CONCAT");

                if (_includeTables) ExtractMySqlTableDefinitions(command, targetSchema);
                if (_includeUserDefinedFunctions) ScriptMySqlFunctions(command, targetSchema);
                if (_includeViews) ScriptMySqlViews(command, targetSchema);
                if (_includeStoredProcedures) ScriptMySqlProcedures(command, targetSchema);
                if (_includeTableTriggers) ScriptMySqlTriggers(command, targetSchema);
                if (_includeEvents) ScriptMySqlEvents(command, targetSchema);
                if (_includeSequences) ScriptMariaDbSequences(command, targetSchema);
                if (_includePackages) ScriptMariaDbPackages(command, targetSchema);
            }
            finally
            {
                connection.Close();
            }
        }
        catch (MySqlException ex)
        {
            _progressLog.Error($"MySQL Error: {ex.Message}");
            throw new RunFailedException($"Database connection or query failed: {ex.Message}", ex);
        }
    }

    private void ScriptMySqlFunctions(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Function Scripts");
        _stats.Functions = ScriptMySqlRoutines(command, targetSchema, "FUNCTION", "Functions", ScriptObjectType.Functions);
    }

    private void ScriptMySqlProcedures(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Stored Procedure Scripts");
        _stats.Procedures = ScriptMySqlRoutines(command, targetSchema, "PROCEDURE", "Procedures", ScriptObjectType.Procedures);
    }

    // Views, routines and triggers are written from SHOW CREATE, the engine's own DDL, rather than rebuilt from
    // INFORMATION_SCHEMA fragments: the fragments lost parameter modes, charsets, quoted names, defaults, aggregates,
    // multi-event and UPDATE OF triggers, trigger order, COMMENT, SQL_MODE and the view options, and VIEW_DEFINITION
    // qualified every reference with the source database so a deployed view read the source. SHOW CREATE leaves a
    // reference to the CURRENT database unqualified and a cross-database one qualified, which is why the extraction
    // connection is opened on the source database.
    private int ScriptMySqlRoutines(IDbCommand command, string targetSchema, string kind, string folder, ScriptObjectType objectType)
    {
        command.CommandText = $@"
SELECT ROUTINE_NAME, COALESCE(EXTERNAL_LANGUAGE, 'SQL')
  FROM INFORMATION_SCHEMA.ROUTINES
  WHERE {MySqlNameMatch.FoldedRoutineSchema("ROUTINE_SCHEMA", $"'{EscapeSql(targetSchema)}'")}
    AND ROUTINE_TYPE = '{kind}'
    AND ROUTINE_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY ROUTINE_NAME";
        // MY-#24. A JavaScript routine (MySQL Enterprise's MLE component) cannot be created on a server without it, and
        // its libraries are not modelled, so it is named and skipped rather than written.
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var language = reader.GetString(1);
                if (language is "" or "SQL") { names.Add(reader.GetString(0)); continue; }
                _progressLog.Warn($"  Skipping {folder}/{reader.GetString(0)}: a {language} routine is not extracted (it needs the server's {language} language component)");
                _stats.SkippedObjects++;
            }
        }
        var createColumn = kind == "FUNCTION" ? "Create Function" : "Create Procedure";
        return ScriptMySqlShowCreateObjects(command, names, folder, objectType, name =>
        {
            var shown = ShowCreate(command, kind, targetSchema, name, createColumn);
            return shown == null ? null : WrapMySqlDelimitedScript(kind, name, shown.Value.SqlMode, StripDefiner(shown.Value.Create));
        });
    }

    private void ScriptMySqlViews(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting View Scripts");
        command.CommandText = $@"
SELECT TABLE_NAME
  FROM INFORMATION_SCHEMA.VIEWS
  WHERE TABLE_SCHEMA = '{EscapeSql(targetSchema)}'
    AND TABLE_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY TABLE_NAME";
        // CREATE OR REPLACE: no window with the view missing, and a failed replace leaves the old view in place.
        // A view stores no sql_mode, so there is nothing to pin.
        _stats.Views = ScriptMySqlShowCreateObjects(command, ReadNames(command), "Views", ScriptObjectType.Views, name =>
        {
            var shown = ShowCreate(command, "VIEW", targetSchema, name, "Create View");
            if (shown == null) return null;
            var create = StripDefiner(shown.Value.Create);
            return Regex.Replace(create, @"^CREATE\s+(?!OR\s+REPLACE\b)", "CREATE OR REPLACE ", RegexOptions.IgnoreCase) + ";\n";
        });
    }

    private void ScriptMySqlTriggers(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Trigger Scripts");
        command.CommandText = $@"
SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
  FROM INFORMATION_SCHEMA.TRIGGERS
  WHERE TRIGGER_SCHEMA = '{EscapeSql(targetSchema)}'
    AND TRIGGER_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY TRIGGER_NAME";
        var triggers = new List<(string Name, string Table, string Timing, string[] Events, int Order)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                triggers.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Convert.ToInt32(reader.GetValue(4))));
        }

        // Without the TRIGGER privilege the catalog does not list the triggers at all, so there is nothing to skip and
        // warn about one by one; an empty list from an account without the privilege is said once.
        if (triggers.Count == 0 && !HasMySqlPrivilege(command, "TRIGGER", targetSchema))
            _progressLog.Warn("  No triggers were listed, and the extracting account has no TRIGGER privilege on this database, so any it has are invisible to it.");

        // SHOW CREATE TRIGGER returns the statement as the user wrote it, which never carries FOLLOWS / PRECEDES:
        // the order lives only in ACTION_ORDER, so the clause is rebuilt from the trigger one position earlier in
        // the same table, timing and event.
        string Predecessor((string Name, string Table, string Timing, string[] Events, int Order) t) =>
            t.Order <= 1 ? null
                : triggers.Where(o => o.Name != t.Name && o.Table == t.Table && o.Timing == t.Timing && o.Order == t.Order - 1
                                      && o.Events.Intersect(t.Events, StringComparer.OrdinalIgnoreCase).Any())
                           .Select(o => o.Name).Distinct().ToList() is { Count: 1 } one ? one[0] : null;

        _stats.Triggers = ScriptMySqlShowCreateObjects(command, triggers.Select(t => t.Name).ToList(), "Triggers", ScriptObjectType.Triggers, name =>
        {
            var shown = ShowCreate(command, "TRIGGER", targetSchema, name, "SQL Original Statement");
            if (shown == null) return null;
            var create = StripDefiner(shown.Value.Create);
            var predecessor = Predecessor(triggers.First(t => t.Name == name));
            if (predecessor != null && !Regex.IsMatch(create, @"\bFOR\s+EACH\s+ROW\s+(FOLLOWS|PRECEDES)\b", RegexOptions.IgnoreCase))
                create = Regex.Replace(create, @"\bFOR\s+EACH\s+ROW\b", m => $"{m.Value} FOLLOWS `{predecessor.Replace("`", "``")}`", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            return WrapMySqlDelimitedScript("TRIGGER", name, shown.Value.SqlMode, create);
        });
    }

    // Global, database-level or table-level; a privilege held through a role is not seen, so a false "no" is possible
    // and the warning that uses this is advisory.
    private static bool HasMySqlPrivilege(IDbCommand command, string privilege, string database)
    {
        command.CommandText = $@"
SELECT COUNT(*)
  FROM (SELECT GRANTEE, PRIVILEGE_TYPE FROM INFORMATION_SCHEMA.USER_PRIVILEGES
        UNION ALL
        SELECT GRANTEE, PRIVILEGE_TYPE FROM INFORMATION_SCHEMA.SCHEMA_PRIVILEGES WHERE TABLE_SCHEMA = '{EscapeSql(database)}'
        UNION ALL
        SELECT GRANTEE, PRIVILEGE_TYPE FROM INFORMATION_SCHEMA.TABLE_PRIVILEGES WHERE TABLE_SCHEMA = '{EscapeSql(database)}') p
  WHERE p.PRIVILEGE_TYPE = '{privilege}'
    AND p.GRANTEE = CONCAT('''', SUBSTRING_INDEX(CURRENT_USER(), '@', 1), '''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''')";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static List<string> ReadNames(IDbCommand command)
    {
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    // Null when the server will not show the definition: SHOW CREATE PROCEDURE / FUNCTION returns a NULL body to an
    // account that is not the definer and lacks global SELECT (SHOW_ROUTINE on 8.0.20+, SELECT on mysql.proc on
    // MariaDB); SHOW CREATE VIEW and TRIGGER refuse outright (1142) without SHOW VIEW / TRIGGER. The caller logs it.
    private (string Create, string SqlMode)? ShowCreate(IDbCommand command, string kind, string database, string name, string createColumn)
    {
        command.CommandText = $"SHOW CREATE {kind} `{database.Replace("`", "``")}`.`{name.Replace("`", "``")}`";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var create = reader.GetOrdinal(createColumn) is var c && !reader.IsDBNull(c) ? reader.GetString(c) : null;
        if (string.IsNullOrWhiteSpace(create)) return null;
        var modeOrdinal = Enumerable.Range(0, reader.FieldCount).FirstOrDefault(i => reader.GetName(i).Equals("sql_mode", StringComparison.OrdinalIgnoreCase), -1);
        var mode = modeOrdinal >= 0 && !reader.IsDBNull(modeOrdinal) ? reader.GetString(modeOrdinal) : "";
        return (create, mode);
    }

    // The deploying account becomes the definer, as it always has. Under ANSI_QUOTES the engine renders the clause
    // with double quotes; a bare user@host form is possible too.
    private static readonly Regex DefinerClause = new(
        @"\bDEFINER\s*=\s*(`[^`]*`|""[^""]*""|[^\s@`""]+)@(`[^`]*`|""[^""]*""|\S+)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string StripDefiner(string create) => DefinerClause.Replace(create, "", 1);

    // SHOW CREATE renders a routine or trigger in the sql_mode it was created under (double-quoted identifiers
    // under ANSI_QUOTES, Oracle syntax under ORACLE), and the engine runs its body under that mode, so the script
    // sets it before the CREATE and restores the session's own mode after. Backticks are valid under every mode,
    // so the DROP needs no pin.
    internal static string WrapMySqlDelimitedScript(string kind, string name, string sqlMode, string create)
    {
        var quoted = $"`{name.Replace("`", "``")}`";
        return string.Join("\n",
            $"DROP {kind} IF EXISTS {quoted};",
            "SET @ss_saved_sql_mode = @@SESSION.sql_mode;",
            $"SET SESSION sql_mode = '{EscapeSql(sqlMode ?? "")}';",
            "DELIMITER //",
            $"{create.TrimEnd()} //",
            "DELIMITER ;",
            "SET SESSION sql_mode = @ss_saved_sql_mode;",
            "");
    }

    // Validation runs after the object's own SHOW CREATE reader is closed: a second command on the connection while
    // a reader is open is a protocol error, which is how validation used to end the run.
    private int ScriptMySqlShowCreateObjects(IDbCommand command, List<string> names, string folder, ScriptObjectType objectType, Func<string, string> buildScript)
    {
        if (names.Count == 0) return 0;
        var castPath = GetCastPath(objectType, folder);
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
        var count = 0;
        foreach (var name in names)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
            var fileName = ResolveOutputPath(castPath, EncodeFullName(name, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { count++; continue; }

            string script;
            try
            {
                script = buildScript(name);
            }
            catch (MySqlException ex) when (ex.Number is 1142 or 1227 or 1305 or 1360)
            {
                script = null;
                _progressLog.Warn($"  Skipping {folder}/{name}: {ex.Message}");
            }
            if (script == null)
            {
                _progressLog.Warn($"  Skipping {folder}/{name}: the server did not return its definition (the extracting account cannot see it)");
                _stats.SkippedObjects++;
                continue;
            }

            var body = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {name}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, body);
            ValidateAndHandleScript(command.Connection, fileName, body, objectType);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Casts scheduled events as DECLARATIVE .json rather than raw .sql (F4).
    /// <para>Written into the same <c>Events/</c> folder the scripted form has always used, which now
    /// holds both: a <c>.sql</c> file there still runs through the Objects slot exactly as before, so no
    /// existing package changes behaviour and migration is per-event and optional. The audit called for
    /// removal-and-replace; that would have broken every package that already has an Events folder.</para>
    /// <para>The catalog-to-package translation lives in <c>SchemaSmith_GenerateEventJSON</c>, a kindled
    /// script, matching how tables and materialized views are extracted — which also means it can be
    /// certified against a live server rather than only through this whole pipeline. It is kindled
    /// with the rest of the helper set before extraction runs, so there is nothing to install here.</para>
    /// </summary>
    private void ScriptMySqlEvents(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Event Structures");
        command.CommandText = $@"
SELECT EVENT_NAME
  FROM INFORMATION_SCHEMA.EVENTS
  WHERE EVENT_SCHEMA = '{EscapeSql(targetSchema)}'
    AND EVENT_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY EVENT_NAME
";
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        if (names.Count == 0) return;

        var castPath = Path.Join(_templatePath, ResolveFolderName("Events", ScriptObjectType.Events));
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var name in names)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;

            _progressLog.Info($"  Cast Json for event {name}");
            command.CommandText = $"CALL SchemaSmith_GenerateEventJSON('{EscapeSql(targetSchema)}', '{EscapeSql(name)}')";
            var eventJson = command.ExecuteScalar()?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(eventJson) || eventJson.Trim() == "{}")
            {
                _progressLog.Error($"    No json returned for event {name}");
                _stats.SkippedObjects++;
                continue;
            }

            var eventObj = JsonText.Deserialize<MySqlEvent>(eventJson);
            var fileName = EncodeFileName(name, ".json");
            var file = ResolveOutputPath(castPath, fileName);
            WritePackageObject(file, eventObj, "events");
            RetireDottedEventFile(castPath, fileName, file);
            _stats.Events++;
        }
    }

    // An overloaded PostgreSQL routine is written as schema.name(argtypes).sql, one file per signature; a single
    // signature keeps schema.name.sql. When a name becomes overloaded, the file the single signature had is the
    // same object under its old name, so the refreshed file replaces it rather than sitting beside it as an orphan.
    private void RetireUnsuffixedRoutineFile(IDataRecord record, string castPath, string fullName, string writtenPath)
    {
        var baseIndex = Enumerable.Range(0, record.FieldCount).FirstOrDefault(i => string.Equals(record.GetName(i), "BaseName", StringComparison.OrdinalIgnoreCase), -1);
        if (baseIndex < 0) return;
        var baseName = record.GetValue(baseIndex)?.ToString();
        if (string.IsNullOrEmpty(baseName) || string.Equals(baseName, fullName, StringComparison.Ordinal)) return;
        var (qualifiedSchema, objectName) = Identifier.SplitQualifiedName(baseName, _platform);
        var oldName = _isSchemaTemplate && qualifiedSchema != null ? EncodeFileName(objectName, ".sql") : EncodeFullName(baseName, ".sql");
        var oldPath = Path.Join(castPath, oldName);
        var file = FileWrapper.GetFromFactory();
        if (string.Equals(oldPath, writtenPath, StringComparison.OrdinalIgnoreCase) || !file.Exists(oldPath)) return;
        _folderIndexes.GetValueOrDefault(GetRelativeFolderName(castPath))?.MarkWritten(oldPath);
        file.Delete(oldPath);
        _progressLog.Info($"    Renamed {oldName} to {Path.GetFileName(writtenPath)} (the name is now overloaded)");
    }

    // Earlier releases wrote events as ".name.json" (an empty schema segment), which Linux and macOS hide and which
    // --Validate flags as non-canonical. The refreshed file replaces it rather than sitting beside it.
    private void RetireDottedEventFile(string castPath, string fileName, string writtenPath)
    {
        var dotted = Path.Join(castPath, "." + fileName);
        var file = FileWrapper.GetFromFactory();
        if (string.Equals(dotted, writtenPath, StringComparison.OrdinalIgnoreCase) || !file.Exists(dotted)) return;
        _folderIndexes.GetValueOrDefault(GetRelativeFolderName(castPath))?.MarkWritten(dotted);
        file.Delete(dotted);
        _progressLog.Info($"    Renamed {Path.GetFileName(dotted)} to {fileName}");
    }

    /// <summary>
    /// MariaDB sequences are written as declared JSON (MariaDbSequence) rather than a scripted CREATE: the deploy
    /// converges every attribute in place and never moves the current value, which a script could not promise.
    /// SHOW CREATE SEQUENCE is the source: it is the engine's own rendering of the options, and the current value is
    /// not in it. A .sql file in Sequences/ still deploys as before.
    /// </summary>
    // MA-c24. A package and its body are one file: DROP PACKAGE drops both, and each half is created under the sql_mode
    // it was created in (ORACLE, or from 11.4 any mode). Its members are not routines of their own and are not listed.
    private void ScriptMariaDbPackages(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Package Scripts");
        command.CommandText = $@"
SELECT p.ROUTINE_NAME,
       EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.ROUTINES b
                WHERE b.ROUTINE_SCHEMA = p.ROUTINE_SCHEMA AND b.ROUTINE_NAME = p.ROUTINE_NAME AND b.ROUTINE_TYPE = 'PACKAGE BODY') AS HAS_BODY
  FROM INFORMATION_SCHEMA.ROUTINES p
  WHERE {MySqlNameMatch.FoldedRoutineSchema("p.ROUTINE_SCHEMA", $"'{EscapeSql(targetSchema)}'")}
    AND p.ROUTINE_TYPE = 'PACKAGE'
    AND p.ROUTINE_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY p.ROUTINE_NAME";
        var withBody = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
                if (Convert.ToInt32(reader.GetValue(1)) == 1) withBody.Add(reader.GetString(0));
            }
        }
        var castPath = GetCastPath(ScriptObjectType.Packages, "Packages");
        _stats.Packages = ScriptMySqlShowCreateObjects(command, names, "Packages", ScriptObjectType.Packages, name =>
        {
            var spec = ShowCreate(command, "PACKAGE", targetSchema, name, "Create Package");
            if (spec == null) return null;
            var body = withBody.Contains(name) ? ShowCreate(command, "PACKAGE BODY", targetSchema, name, "Create Package Body") : null;
            return WrapMariaDbPackageScript(name, spec.Value, body);
        });
        if (_stats.Packages > 0) DeclareFolderWrittenOnUse(ScriptObjectType.Packages, castPath);
    }

    // Before 11.4 PACKAGE is a keyword only under ORACLE mode, so the DROP runs under the package's own mode too.
    internal static string WrapMariaDbPackageScript(string name, (string Create, string SqlMode) spec, (string Create, string SqlMode)? body)
    {
        var lines = new List<string>
        {
            "SET @ss_saved_sql_mode = @@SESSION.sql_mode;",
            $"SET SESSION sql_mode = '{EscapeSql(spec.SqlMode ?? "")}';",
            $"DROP PACKAGE IF EXISTS `{name.Replace("`", "``")}`;",
            "DELIMITER //",
            $"{StripDefiner(spec.Create).TrimEnd()} //",
            "DELIMITER ;"
        };
        if (body != null)
            lines.AddRange(new[]
            {
                $"SET SESSION sql_mode = '{EscapeSql(body.Value.SqlMode ?? "")}';",
                "DELIMITER //",
                $"{StripDefiner(body.Value.Create).TrimEnd()} //",
                "DELIMITER ;"
            });
        lines.Add("SET SESSION sql_mode = @ss_saved_sql_mode;");
        lines.Add("");
        return string.Join("\n", lines);
    }

    private void ScriptMariaDbSequences(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Sequences");
        command.CommandText = $@"
SELECT TABLE_NAME
  FROM INFORMATION_SCHEMA.TABLES
 WHERE TABLE_SCHEMA = '{EscapeSql(targetSchema)}'
   AND TABLE_TYPE = 'SEQUENCE'
 ORDER BY TABLE_NAME";

        var sequenceNames = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                sequenceNames.Add(reader.GetString(0));
        }

        if (sequenceNames.Count == 0) return; // lazy folder creation, matching PerformMySqlCasting's convention

        var castPath = GetCastPath(ScriptObjectType.Sequences, "Sequences");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        foreach (var name in sequenceNames)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;

            command.CommandText = $"SHOW CREATE SEQUENCE `{targetSchema.Replace("`", "``")}`.`{name.Replace("`", "``")}`";
            string createStatement;
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                createStatement = reader.GetString(1); // "Create Table" column carries the CREATE SEQUENCE DDL
            }

            var sequence = ParseMariaDbSequence(name, createStatement);
            var file = ResolveOutputPath(castPath, EncodeFullName(name, ".json"));
            _progressLog.Info($"  Cast Json for sequence {name}");
            WritePackageObject(file, sequence, "sequences");
            _stats.Sequences++;
        }
    }

    // SHOW CREATE SEQUENCE renders, in this order: CREATE SEQUENCE `s` [as <type>] start with N minvalue N maxvalue N
    // increment by N cache N cycle|nocycle ENGINE=... . MariaDB's own defaults are left out of the file, so it says only
    // what the author chose: minvalue 1 and maxvalue 9223372036854775806 for an ascending sequence (the mirror for a
    // descending one), and a start equal to the lower (upper) bound.
    internal static MariaDbSequence ParseMariaDbSequence(string name, string createStatement)
    {
        long Number(string keyword, long fallback)
        {
            var m = Regex.Match(createStatement, $@"\b{keyword}\s+(?<n>-?\d+)", RegexOptions.IgnoreCase);
            return m.Success ? long.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }

        var sequence = new MariaDbSequence { Name = name };
        var type = Regex.Match(createStatement, @"\bas\s+(?<type>\w+(?:\s+unsigned)?)\s+start\b", RegexOptions.IgnoreCase);
        if (type.Success) sequence.DataType = type.Groups["type"].Value.ToLowerInvariant();
        sequence.Increment = Number("increment by", 1);
        // A cache of 0 is written "nocache", with no number to read.
        sequence.Cache = Regex.IsMatch(createStatement, @"\bnocache\b", RegexOptions.IgnoreCase) ? 0 : Number("cache", 1000);
        sequence.Cycle = Regex.IsMatch(createStatement, @"\scycle\b", RegexOptions.IgnoreCase);
        var ascending = sequence.Increment > 0;
        var min = Number("minvalue", ascending ? 1 : -9223372036854775807);
        var max = Number("maxvalue", ascending ? 9223372036854775806 : -1);
        var start = Number("start with", ascending ? min : max);
        if (min != (ascending ? 1 : -9223372036854775807)) sequence.MinValue = min;
        if (max != (ascending ? 9223372036854775806 : -1)) sequence.MaxValue = max;
        if (start != (ascending ? min : max)) sequence.Start = start;
        return sequence;
    }

    // The catalog qualifies a sequence default with the database (nextval(`db`.`s`)); the package carries the bare
    // name so it deploys to any database, and the deploy compares defaults without the qualifier.
    // Under AUTO the engine adds history partitions as they fill, so the list it reports today is not the declaration.
    internal static void ApplySystemTimeOptions(MySqlPartitioning partitioning, string createTable)
    {
        partitioning.SystemTime = MariaDbSystemTimeOptions.Read(createTable);
        if (partitioning.SystemTime?.Auto != true) return;
        partitioning.Partitions.Clear();
        partitioning.PartitionCount = null;
    }

    // The engine names a subpartition <partition>sp<n> unless the table names it; a named one is not modelled.
    private static bool HasExplicitSubpartitionNames(IDbCommand command, string schema, string table, MySqlTable mySqlTable)
    {
        if (mySqlTable.Partitioning?.Subpartitioning == null) return false;
        command.CommandText = @"SELECT COUNT(*) FROM INFORMATION_SCHEMA.PARTITIONS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND SUBPARTITION_NAME IS NOT NULL
  AND CAST(SUBPARTITION_NAME AS BINARY) <> CAST(CONCAT(PARTITION_NAME, 'sp', SUBPARTITION_ORDINAL_POSITION - 1) AS BINARY)";
        command.Parameters.Clear();
        foreach (var (name, value) in new[] { ("@schema", MySqlReservedWords.Unquote(schema)), ("@table", MySqlReservedWords.Unquote(table)) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var named = Convert.ToInt32(command.ExecuteScalar());
        command.Parameters.Clear();
        return named > 0;
    }

    internal static void StripSequenceDatabase(Table table, string database)
    {
        var qualified = new Regex(@"nextval\(`" + Regex.Escape(database) + "`\\.`", RegexOptions.IgnoreCase);
        foreach (var column in table.Columns)
            if (column.Default != null) column.Default = qualified.Replace(column.Default, "nextval(`");
    }

    private void ExtractMySqlTableDefinitions(IDbCommand command, string targetSchema)
    {
        using var connectionJson = GetConnection(targetSchema);
        try
        {
            using var commandJson = connectionJson.CreateCommand();

            using var gateConnection = GetConnection(targetSchema);
            using var gateCommand = gateConnection.CreateCommand();
            bool IsVariantActive(string expr) =>
                GateEvaluator.ShouldApply(gateCommand, SqlScript.TokenReplace(expr, _packageTokens, _platform));

            command.CommandText = $@"
SELECT TABLE_SCHEMA, TABLE_NAME
  FROM INFORMATION_SCHEMA.TABLES t
  WHERE TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED')
    AND TABLE_SCHEMA = '{EscapeSql(targetSchema)}'
    AND TABLE_SCHEMA <> 'SchemaSmith'
    AND TABLE_NAME NOT LIKE 'SchemaSmith\_%'
  ORDER BY TABLE_NAME
";

            var tableDir = Path.Combine(_templatePath, "Tables");
            DirectoryWrapper.GetFromFactory().CreateDirectory(tableDir);
            var resolver = new TableFileResolver(tableDir, _platform, _isSchemaTemplate, IsVariantActive);

            var tables = new List<(string Schema, string Table)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    tables.Add(($"{reader["TABLE_SCHEMA"]}", $"{reader["TABLE_NAME"]}"));
                }
            }

            var totalTables = tables.Count;
            var filteredCount = _objectsToCast.Length > 0
                ? tables.Count(t => _objectsToCast.Contains(t.Table.ToLower()) || _objectsToCast.Contains($"{t.Schema}.{t.Table}".ToLower()))
                : totalTables;

            _progressLog.Info($"Casting Table Structures ({filteredCount} of {totalTables} tables)");

            // MySQL stored procedures take no default parameter values, so the ordering choice travels
            // as a session variable rather than an argument -- see the proc for why. Set once for the
            // connection, not per table: it is session state, and a SET per table would add a round
            // trip to every table in the schema for a value that never changes.
            commandJson.CommandText = $"SET @SchemaSmith_ObjectOrder = '{_objectOrder}'";
            commandJson.ExecuteNonQuery();

            var extracted = new List<Table>();
            foreach (var (schema, table) in tables)
            {
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(table.ToLower()) && !_objectsToCast.Contains($"{schema}.{table}".ToLower())) continue;

                _progressLog.Info($"  Cast Json for {table}");

                try
                {
                    commandJson.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{EscapeSql(MySqlReservedWords.Unquote(schema))}', '{EscapeSql(MySqlReservedWords.Unquote(table))}')";

                    string json = "";
                    using (var jsonReader = commandJson.ExecuteReader())
                    {
                        while (jsonReader.Read())
                            json += $"{jsonReader[0]}\r\n";
                    }

                    if (string.IsNullOrWhiteSpace(json) || json.Trim().Equals("{}"))
                    {
                        _progressLog.Error($"    ERROR: No json returned for {table}");
                        _stats.TableErrors++;
                        continue;
                    }

                    // Use the platform-aware deserializer so the platform subclass
                    // (e.g., SqlServerTable) materializes — otherwise the base Table
                    // type loses platform-only properties like Schema, and non-default-
                    // schema tables get round-tripped as dbo.<name> on the next quench.
                    var tableObj = PlatformDeserializer.DeserializeTable(json, _platform);
                    if (tableObj == null)
                    {
                        _progressLog.Error($"    ERROR: Failed to deserialize json for {table}");
                        _stats.TableErrors++;
                        continue;
                    }

                    if (tableObj is MySqlTable mySqlTable)
                    {
                        if (HasExplicitSubpartitionNames(commandJson, schema, table, mySqlTable))
                        {
                            _progressLog.Error($"    ERROR: {table} names its subpartitions; SchemaSmith models subpartitions as a count, which the engine names <partition>sp<n>. Skipped rather than emitted with names a deploy would not create.");
                            _stats.TableErrors++;
                            continue;
                        }

                        // A FULLTEXT parser (MY-A2), KEY partitioning's ALGORITHM, SYSTEM_TIME's options and a VECTOR index's M and
                        // DISTANCE are reported only by SHOW CREATE TABLE.
                        var keyPartitioned = mySqlTable.Partitioning?.Method?.Trim().EndsWith("KEY", StringComparison.OrdinalIgnoreCase) == true;
                        var systemTime = string.Equals(mySqlTable.Partitioning?.Method?.Trim(), "SYSTEM_TIME", StringComparison.OrdinalIgnoreCase);
                        var vectorIndexed = mySqlTable.Indexes.Exists(i => i is MySqlIndex { IndexType: var type } && string.Equals(type, "VECTOR", StringComparison.OrdinalIgnoreCase));
                        if (mySqlTable.FullTextIndexes.Count > 0 || keyPartitioned || systemTime || vectorIndexed)
                        {
                            commandJson.CommandText = $"SHOW CREATE TABLE `{MySqlReservedWords.Unquote(schema).Replace("`", "``")}`.`{MySqlReservedWords.Unquote(table).Replace("`", "``")}`";
                            using var createReader = commandJson.ExecuteReader();
                            if (createReader.Read())
                            {
                                var createTable = createReader.GetString(1);
                                MySqlFullTextParsers.Apply(mySqlTable, createTable);
                                if (vectorIndexed)
                                    MariaDbVectorIndexOptions.Apply(mySqlTable.Indexes, createTable);
                                if (keyPartitioned)
                                    mySqlTable.Partitioning.KeyAlgorithm = MySqlKeyAlgorithm.Read(createTable);
                                if (systemTime)
                                    ApplySystemTimeOptions(mySqlTable.Partitioning, createTable);
                            }
                        }
                    }

                    var resolution = resolver.Resolve("", table);
                    var filename = resolution.WritePath;
                    MarkPathWritten(tableDir, filename);
                    if (resolution.UngatedEmit)
                        _progressLog.Warn($"    Extracted {table} did not match any active variant — writing ungated '{Path.GetFileName(filename)}'; resolve its gating (SS-DUP-001).");
                    var oldTableFile = !string.IsNullOrEmpty(tableObj.OldName)
                        ? ResolveOutputPath(tableDir, EncodeFileName(tableObj.OldName.Trim('`'), ".json"))
                        : null;

                    if (FileWrapper.GetFromFactory().Exists(filename) || (oldTableFile != null && FileWrapper.GetFromFactory().Exists(oldTableFile)))
                    {
                        var originalPath = FileWrapper.GetFromFactory().Exists(filename) ? filename : oldTableFile;
                        var original = LoadOriginalForPreservation(originalPath);
                        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(tableObj, original, IsVariantActive, SourceCanReportXmlCompression);
                        if (_preserveExistingOrder)
                            ImportTableHelper.PreserveListOrder(tableObj, original, _objectOrder);
                    }

                    if (_platform == Platform.MariaDb) StripSequenceDatabase(tableObj, targetSchema);
                    WritePackageObject(filename, tableObj, "tables");
                    extracted.Add(tableObj);
                    _stats.Tables++;
                }
                catch (MySqlException ex)
                {
                    // Exception type name included alongside Message: a bare Message on a caught-and-
                    // continued table skip is otherwise indistinguishable in the log from any other
                    // MySqlException, which makes a "table silently missing from the package" report
                    // unnecessarily hard to root-cause after the fact.
                    _progressLog.Error($"    ERROR: MySQL error extracting {table}: {ex.GetType().Name}: {ex.Message}");
                    _stats.TableErrors++;
                }
                catch (JsonException ex)
                {
                    _progressLog.Error($"    ERROR: JSON parsing error for {table}: {ex.GetType().Name}: {ex.Message}");
                    _stats.TableErrors++;
                }
                catch (IOException ex)
                {
                    _progressLog.Error($"    ERROR: File write error for {table}: {ex.GetType().Name}: {ex.Message}");
                    _stats.TableErrors++;
                }
            }

            static string Bare(string name) => (name ?? "").Trim('`');
            // MySQL 8.0 deploys a foreign key to a non-unique or partial key; 8.4 refuses it by default.
            if (_platform == Platform.MySQL)
                foreach (var (owner, fk) in Schema.Validation.Checks.CoherenceCheck.NonStandardKeyReferences(extracted))
                    _progressLog.Warn($"    Foreign key {Bare(owner.Name)}.{Bare(fk.Name)} references {Bare(fk.RelatedTable)} ({fk.RelatedColumns.Replace("`", "")}), which is not a whole primary or unique key. " +
                                      "MySQL 8.4 refuses such a key while restrict_fk_on_non_standard_key is on, its default (SS-FK-008).");
        }
        finally
        {
            connectionJson.Close();
        }
    }

    #endregion

    #region Table Extraction (SQL Server)

    private void ExtractTableDefinitions(IDbCommand command, string targetDb)
    {
        using var connectionJson = GetConnection(targetDb);
        try
        {
            using var commandJson = connectionJson.CreateCommand();

            // Dedicated connection/command for gate evaluation — the enumeration reader on `command`
            // and the JSON reader on `commandJson` are both live during the loop.
            using var gateConnection = GetConnection(targetDb);
            using var gateCommand = gateConnection.CreateCommand();
            bool IsVariantActive(string expr) =>
                GateEvaluator.ShouldApply(gateCommand, SqlScript.TokenReplace(expr, _packageTokens, _platform));

            command.CommandText = @"
SELECT TABLE_SCHEMA, TABLE_NAME
  FROM INFORMATION_SCHEMA.TABLES t
  JOIN sys.objects so ON so.[object_id] = OBJECT_ID(t.TABLE_SCHEMA + '.' + t.TABLE_NAME)
                     AND so.is_ms_shipped = 0
  WHERE TABLE_TYPE = 'BASE TABLE'
    AND TABLE_NAME NOT LIKE 'MSPeer[_]%'
    AND TABLE_NAME NOT LIKE 'MSPub[_]%'
    AND TABLE_NAME NOT IN ('dtproperties', 'sysdiagrams')
    AND TABLE_SCHEMA <> 'SchemaSmith'
    -- A temporal history table is created BY the versioned table's declaration (IsTemporal plus the
    -- HistoryTable* properties), so extracting it separately puts a second table file in the package
    -- that the next deploy then tries to create in its own right. OBJECTPROPERTY is used rather than
    -- sys.tables.temporal_type on purpose: it takes the property as a STRING, so on a pre-2016 server
    -- it returns NULL instead of failing to bind -- a static temporal_type reference would be a
    -- CREATE-time invalid-column error there. 1 = HISTORY_TABLE.
    AND ISNULL(OBJECTPROPERTY(so.[object_id], 'TableTemporalType'), 0) <> 1
    -- An updatable ledger table (2022+) spawns MSSQL_LedgerHistoryFor_<object_id>, whose
    -- temporal_type is NON_TEMPORAL_TABLE, so the filter above does not reach it. The name carries an
    -- object id from the source server, so the extracted file could not be deployed anywhere. Matched
    -- by its engine-reserved prefix, the same way the replication tables above are.
    AND TABLE_NAME NOT LIKE 'MSSQL[_]LedgerHistoryFor[_]%'
    -- Dropping a ledger table does NOT remove it: SQL Server renames it to
    -- MSSQL_DroppedLedgerTable_<name>_<guid> and leaves MSSQL_DroppedLedgerHistory_* beside it.
    -- Both report is_ms_shipped = 0, and the History one has is_dropped_ledger_table = 0, so the
    -- obvious flag misses it -- the engine-reserved name prefix is the reliable discriminator.
    AND TABLE_NAME NOT LIKE 'MSSQL[_]DroppedLedger%'
    -- A FileTable or external table is named in a warning once the tables are done (WarnSqlServerTablesNotExtracted).
    AND ISNULL(OBJECTPROPERTY(so.[object_id], 'TableIsFileTable'), 0) = 0" +
    (_sourceMajor is 0 or >= 13 ? @"
    AND NOT EXISTS (SELECT 1 FROM sys.tables xt WHERE xt.[object_id] = so.[object_id] AND xt.is_external = 1)" : "") + @"
  ORDER BY 1, 2
";

            _progressLog.Info("Casting Table Structures");
            var tableDir = Path.Combine(_templatePath, "Tables");
            DirectoryWrapper.GetFromFactory().CreateDirectory(tableDir);
            var resolver = new TableFileResolver(tableDir, _platform, _isSchemaTemplate, IsVariantActive);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tableSchema = $"{reader["TABLE_SCHEMA"]}";
                var tableName = $"{reader["TABLE_NAME"]}";
                if (!ShouldExtractFromSchema(tableSchema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(tableName.ToLower()) && !_objectsToCast.Contains($"{tableSchema}.{tableName}".ToLower())) continue;

                _progressLog.Info($"  Cast {_ingestEncoding} for {tableSchema}.{tableName}");
                // One bad table (e.g. a partitioned table hitting an engine-side extraction defect)
                // must degrade to a reported skip, not abort every table still queued behind it and
                // leave a partial package on disk with no record of what's missing (see Failed above).
                try
                {
                    var json = _ingestEncoding == IngestEncoding.Xml
                        ? ExtractTableModelXml(commandJson, tableSchema, tableName, _objectOrder)
                        : ExtractTableModelJson(commandJson, tableSchema, tableName, _objectOrder);
                    if (string.IsNullOrWhiteSpace(json) || json.Trim().Equals("{}"))
                    {
                        _progressLog.Error($"    No json returned for {tableSchema}.{tableName}");
                        _stats.TableErrors++;
                        continue;
                    }

                    var resolution = resolver.Resolve(tableSchema, tableName);
                    var filename = resolution.WritePath;
                    MarkPathWritten(tableDir, filename);
                    if (resolution.UngatedEmit)
                        _progressLog.Warn($"    Extracted {tableSchema}.{tableName} did not match any active variant — writing ungated '{Path.GetFileName(filename)}'; resolve its gating (SS-DUP-001).");
                    _progressLog.Info($"    Casting {filename}");
                    // Use the platform-aware deserializer so the platform subclass
                    // (e.g., SqlServerTable) materializes — otherwise the base Table
                    // type loses platform-only properties like Schema, and non-default-
                    // schema tables get round-tripped as dbo.<name> on the next quench.
                    var tableObj = PlatformDeserializer.DeserializeTable(json, _platform);

                    if (_checkConstraintStyle == CheckConstraintStyle.TableLevel && _platform == Platform.SqlServer && tableObj is SqlServerTable sqlTable)
                    {
                        commandJson.CommandText = $@"
SELECT cc.name AS [Name],
       SchemaSmith.fn_StripParenWrapping(cc.definition) AS [Expression],
       cc.parent_column_id,
       cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication
  FROM sys.check_constraints cc
 WHERE cc.parent_object_id = OBJECT_ID('{EscapeSql(tableSchema)}.{EscapeSql(tableName)}')
 ORDER BY cc.name";

                        var allConstraints = new List<CheckConstraint>();
                        using (var ccReader = commandJson.ExecuteReader())
                        {
                            while (ccReader.Read())
                                allConstraints.Add(new SqlServerCheckConstraint
                                {
                                    Name = $"{ccReader["Name"]}", Expression = $"{ccReader["Expression"]}",
                                    Enforced = !(bool)ccReader["is_disabled"], Validated = !(bool)ccReader["is_not_trusted"],
                                    NotForReplication = (bool)ccReader["is_not_for_replication"]
                                });
                        }
                        PromoteCheckConstraintsToTableLevel(sqlTable, allConstraints);
                    }

                    var oldTableFile = !string.IsNullOrEmpty(tableObj.OldName)
                        ? ResolveOutputPath(tableDir, EncodeObjectFileName(tableSchema, tableObj.OldName.Trim('"'), ".json"))
                        : null;
                    if (FileWrapper.GetFromFactory().Exists(filename) || (oldTableFile != null && FileWrapper.GetFromFactory().Exists(oldTableFile)))
                    {
                        var original = LoadOriginalForPreservation(FileWrapper.GetFromFactory().Exists(filename) ? filename : oldTableFile);
                        ImportTableHelper.PreserveDataDeliveryAndCustomProperties(tableObj, original, IsVariantActive, SourceCanReportXmlCompression);
                        if (_preserveExistingOrder)
                            ImportTableHelper.PreserveListOrder(tableObj, original, _objectOrder);
                    }
                    // Schema-template mode: strip the platform Schema field and any same-source RelatedTableSchema
                    // values on the in-memory table object before serialization (design §7.2), and rewrite
                    // source-schema-qualified refs inside expression-bearing JSON properties (design §7.3).
                    ScrubSchemaForTemplate(tableObj, filename);
                    WritePackageObject(filename, tableObj, "tables");
                    _stats.Tables++;
                    WarnIndexesNotExtracted(commandJson, tableSchema, tableName);
                }
                catch (SqlException ex)
                {
                    _progressLog.Error($"    ERROR: SQL Server error extracting {tableSchema}.{tableName}: {ex.Message}");
                    _stats.TableErrors++;
                }
                catch (JsonException ex)
                {
                    _progressLog.Error($"    ERROR: JSON parsing error for {tableSchema}.{tableName}: {ex.Message}");
                    _stats.TableErrors++;
                }
                catch (IOException ex)
                {
                    _progressLog.Error($"    ERROR: File write error for {tableSchema}.{tableName}: {ex.Message}");
                    _stats.TableErrors++;
                }
            }
            reader.Close();
            WarnSqlServerTablesNotExtracted(commandJson);
        }
        finally
        {
            connectionJson.Close();
        }
    }

    // Modern encoding: GenerateTableJSON emits the JSON model directly, split across rows once it exceeds
    // the row size — concatenate every row.
    private static string ExtractTableModelJson(IDbCommand command, string tableSchema, string tableName, ObjectOrder objectOrder)
    {
        command.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = '{EscapeSql(tableSchema)}', @p_Table = '{EscapeSql(tableName)}', @p_ObjectOrder = '{objectOrder}'";
        using var reader = command.ExecuteReader();
        var json = new StringBuilder();
        while (reader.Read())
            json.Append($"{reader[0]}\r\n");
        return json.ToString();
    }

    // Legacy encoding (pre-2016 binary, or Source:CompatEncoding=legacy): GenerateTableXml emits the model
    // as FOR XML PATH, converted back to the JSON model via ModelXmlSerializer.FromIngestXml. FOR XML also
    // splits across rows past ~2033 chars, so concatenate every row into the single <Table> document first.
    private static string ExtractTableModelXml(IDbCommand command, string tableSchema, string tableName, ObjectOrder objectOrder)
    {
        command.CommandText = $"EXEC SchemaSmith.GenerateTableXml @p_Schema = '{EscapeSql(tableSchema)}', @p_Table = '{EscapeSql(tableName)}', @p_ObjectOrder = '{objectOrder}'";
        using var reader = command.ExecuteReader();
        var xml = new StringBuilder();
        while (reader.Read())
            xml.Append(reader[0]);
        var xmlText = xml.ToString();
        return string.IsNullOrWhiteSpace(xmlText) ? "" : ModelXmlSerializer.FromIngestXml(xmlText);
    }

    #endregion

    #region Helpers

    internal static void PromoteCheckConstraintsToTableLevel(SqlServerTable table, List<CheckConstraint> allConstraints)
    {
        foreach (var col in table.Columns.OfType<SqlServerColumn>())
            col.CheckExpression = null;

        table.CheckConstraints = allConstraints;
    }

    /// <summary>
    /// Routes a PostgreSQL single-column check back onto its column, so a column-level
    /// <c>CheckExpression</c> survives an extract instead of returning as a table-level constraint.
    /// <paramref name="singleColumnChecks"/> maps constraint name to column name, taken from
    /// <c>pg_constraint.conkey</c>.
    /// <para>Only a check already named <c>CK_&lt;table&gt;_&lt;column&gt;</c> is demoted — the name
    /// SchemaSmith itself generates from a column-level <c>CheckExpression</c>. PostgreSQL, unlike
    /// SQL Server, records no marker for how a constraint was *declared* (its docs call a column
    /// constraint "only a notational convenience", stored identically to a table constraint), so
    /// referencing one column is not evidence it was authored column-level. Demoting on column
    /// count alone would discard a user's chosen constraint name and rename it to the generated form
    /// on the next apply — a drop/recreate on every deploy. Anything otherwise named stays
    /// table-level, which is both faithful and churn-free.</para>
    /// </summary>
    internal static void DemoteSingleColumnChecksToColumnLevel(PostgreSqlTable table, Dictionary<string, string> singleColumnChecks)
    {
        if (singleColumnChecks == null || singleColumnChecks.Count == 0) return;

        var tableName = StringHelper.StripIdentifierWrapper(table.Name);

        foreach (var (constraintName, columnName) in singleColumnChecks
                     .Where(c => string.Equals(c.Key, $"CK_{tableName}_{c.Value}", StringComparison.OrdinalIgnoreCase)))
        {
            var constraint = table.CheckConstraints
                .FirstOrDefault(c => string.Equals(StringHelper.StripIdentifierWrapper(c.Name), constraintName, StringComparison.OrdinalIgnoreCase));
            var column = table.Columns.OfType<PostgreSqlColumn>()
                .FirstOrDefault(c => string.Equals(StringHelper.StripIdentifierWrapper(c.Name), columnName, StringComparison.OrdinalIgnoreCase));
            if (constraint == null || column == null) continue;

            column.CheckExpression = constraint.Expression;
            table.CheckConstraints.Remove(constraint);
        }
    }

    internal static string EscapeSql(string value) => value.Replace("'", "''");

    // Index kinds a package cannot declare yet (vector indexes) are left out of the table file, so each one is named and
    // counted as skipped. XML indexes (selective ones included), spatial and JSON indexes have lists of their own, and the
    // renderable types are ordinary indexes.
    private void WarnIndexesNotExtracted(IDbCommand command, string tableSchema, string tableName)
    {
        command.CommandText = $@"
SELECT [name], type_desc AS kind FROM sys.indexes
 WHERE [object_id] = OBJECT_ID(QUOTENAME(N'{EscapeSql(tableSchema)}') + N'.' + QUOTENAME(N'{EscapeSql(tableName)}'))
   AND index_id > 0 AND is_hypothetical = 0 AND [type] NOT IN (3, 4, 9, {ForgeKindler.SqlServerRenderableIndexTypes})
 ORDER BY [name];";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _progressLog.Warn($"    WARNING: {tableSchema}.{tableName}.{reader["name"]} is a {reader["kind"]} index, which a package cannot declare yet; not extracted");
            _stats.SkippedObjects++;
        }
    }

    internal static string FormatBaseType(string baseType, short maxLength, byte precision, byte scale)
    {
        var lower = baseType.ToLower();
        switch (lower)
        {
            case "nvarchar":
            case "nchar":
                return maxLength == -1 ? $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}](max)" : $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]({maxLength / 2})";
            case "varchar":
            case "char":
            case "varbinary":
            case "binary":
                return maxLength == -1 ? $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}](max)" : $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]({maxLength})";
            case "decimal":
            case "numeric":
                return $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]({precision}, {scale})";
            case "datetime2":
            case "datetimeoffset":
            case "time":
                return scale != 7 ? $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]({scale})" : $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]";
            // VECTOR(n[, float16]) (2025): max_length is 8 + n * 4 (float32) or 8 + n * 2 (float16), scale 1 marks float16.
            case "vector":
                return $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]({(maxLength - 8) / (scale == 1 ? 2 : 4)}{(scale == 1 ? ", float16" : "")})";
            default:
                return $"[{Identifier.EscapeDelimited(baseType, Platform.SqlServer)}]";
        }
    }

    private void IncrementStatForFolder(string folder)
    {
        switch (folder)
        {
            case "Schemas": _stats.Schemas++; break;
            case "Domain Types": _stats.DomainTypes++; break;
            case "Enum Types": _stats.EnumTypes++; break;
            case "Composite Types": _stats.CompositeTypes++; break;
            case "Functions": _stats.Functions++; break;
            case "Trigger Functions": _stats.Functions++; break;
            case "Window Functions": _stats.Functions++; break;
            case "Aggregates": _stats.Aggregates++; break;
            case "Procedures": _stats.Procedures++; break;
            case "Sequences": _stats.Sequences++; break;
            case "Rules": _stats.Rules++; break;
            case "Triggers": _stats.Triggers++; break;
            case "Views": _stats.Views++; break;
            case "Materialized Views": _stats.MaterializedViews++; break;
            case "Collations": _stats.Collations++; break;
            case "Publications": _stats.Publications++; break;
            case "Range Types": _stats.RangeTypes++; break;
            case "Text Search Configurations": _stats.TextSearchConfigurations++; break;
            case "Operators": _stats.Operators++; break;
            case "Casts": _stats.Casts++; break;
            case "Event Triggers": _stats.EventTriggers++; break;
        }
    }

    /// <summary>
    /// Loads the table file being overwritten so its authored settings can be carried forward. A file
    /// that will not parse is a WARNING, not a failure: extraction is about to replace it with a good
    /// one, so aborting the whole cast over a file we are discarding anyway is the worst of both --
    /// the operator keeps the broken file AND gets no extract. What is genuinely lost is that file's
    /// authored settings, which is exactly what the warning has to say out loud.
    /// </summary>
    private Table LoadOriginalForPreservation(string path)
    {
        try
        {
            return JsonHelper.TableLoad(path, _platform);
        }
        catch (Exception ex)
        {
            _progressLog.Warn(
                $"    Could not read existing {Path.GetFileName(path)} to carry its settings forward "
                + $"({ex.Message}). Extraction continues and the file will be replaced, but anything it "
                + "declared that cannot be read back from the database -- data delivery, ShouldApplyExpression, "
                + "drop overrides -- is NOT preserved. Check the replacement before committing it.");
            return null;
        }
    }

    private void LogSummary()
    {
        _progressLog.Info("");
        _progressLog.Info("=== Casting Summary ===");
        _progressLog.Info($"  Tables:     {_stats.Tables} extracted, {_stats.TableErrors} errors");

        switch (_platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                LogIfPositive("  Schemas:    ", _stats.Schemas);
                LogIfPositive("  DataTypes:  ", _stats.DataTypes);
                LogIfPositive("  Functions:  ", _stats.Functions);
                LogIfPositive("  Views:      ", _stats.Views);
                LogIfPositive("  Procedures: ", _stats.Procedures);
                LogIfPositive("  Triggers:   ", _stats.Triggers);
                LogIfPositive("  DDLTriggers:", _stats.DDLTriggers);
                LogIfPositive("  FTCatalogs: ", _stats.FullTextCatalogs);
                LogIfPositive("  FTStopLists:", _stats.FullTextStopLists);
                LogIfPositive("  XmlSchemas: ", _stats.XmlSchemaCollections);
                LogIfPositive("  IdxViews:   ", _stats.IndexedViews);
                LogIfPositive("  Sequences:  ", _stats.Sequences);
                LogIfPositive("  Synonyms:   ", _stats.Synonyms);
                LogIfPositive("  Security policies: ", _stats.SecurityPolicies);
                break;

            case Platform.PostgreSQL:
                LogIfPositive("  Schemas:    ", _stats.Schemas);
                LogIfPositive("  Domain Types:", _stats.DomainTypes);
                LogIfPositive("  Enum Types: ", _stats.EnumTypes);
                LogIfPositive("  Composites: ", _stats.CompositeTypes);
                LogIfPositive("  Functions:  ", _stats.Functions);
                LogIfPositive("  Aggregates: ", _stats.Aggregates);
                LogIfPositive("  Procedures: ", _stats.Procedures);
                LogIfPositive("  Sequences:  ", _stats.Sequences);
                LogIfPositive("  Rules:      ", _stats.Rules);
                LogIfPositive("  Triggers:   ", _stats.Triggers);
                LogIfPositive("  Views:      ", _stats.Views);
                LogIfPositive("  MatViews:   ", _stats.MaterializedViews);
                LogIfPositive("  Collations: ", _stats.Collations);
                LogIfPositive("  Publications:", _stats.Publications);
                LogIfPositive("  Range Types:", _stats.RangeTypes);
                LogIfPositive("  Text Search Configurations:", _stats.TextSearchConfigurations);
                LogIfPositive("  Operators:  ", _stats.Operators);
                LogIfPositive("  Casts:      ", _stats.Casts);
                LogIfPositive("  Event Triggers:", _stats.EventTriggers);
                break;

            case Platform.MySQL:
                LogIfPositive("  Views:      ", _stats.Views);
                LogIfPositive("  Functions:  ", _stats.Functions);
                LogIfPositive("  Procedures: ", _stats.Procedures);
                LogIfPositive("  Triggers:   ", _stats.Triggers);
                LogIfPositive("  Events:     ", _stats.Events);
                LogIfPositive("  Sequences:  ", _stats.Sequences);
                LogIfPositive("  Packages:   ", _stats.Packages);
                break;
        }

        LogIfPositive("  Skipped:    ", _stats.SkippedObjects);
        LogIfPositive("  Invalid:    ", _invalidScripts.Count);
        _progressLog.Info($"  Elapsed:    {_stopwatch.Elapsed.TotalSeconds:F1} seconds");
        _progressLog.Info("");
        _progressLog.Info(ExitCode switch
        {
            2 => "Casting Completed with Errors",
            1 => "Casting Completed, but some objects were not extracted (see Skipped and Invalid above)",
            _ => "Casting Completed Successfully"
        });
    }

    private void LogIfPositive(string label, int count)
    {
        if (count > 0)
            _progressLog.Info($"{label}{count} extracted");
    }

    #endregion

    internal class ExtractionStats
    {
        public int Tables { get; set; }
        public int TableErrors { get; set; }
        public int SkippedObjects { get; set; }
        public int Schemas { get; set; }
        public int DataTypes { get; set; }
        public int DomainTypes { get; set; }
        public int EnumTypes { get; set; }
        public int CompositeTypes { get; set; }
        public int Functions { get; set; }
        public int Aggregates { get; set; }
        public int Views { get; set; }
        public int MaterializedViews { get; set; }
        public int IndexedViews { get; set; }
        public int Procedures { get; set; }
        public int Triggers { get; set; }
        public int DDLTriggers { get; set; }
        public int Sequences { get; set; }
        public int Rules { get; set; }
        public int Events { get; set; }
        public int Packages { get; set; }
        public int FullTextCatalogs { get; set; }
        public int FullTextStopLists { get; set; }
        public int XmlSchemaCollections { get; set; }
        public int Synonyms { get; set; }
        public int SecurityPolicies { get; set; }
        public int Collations { get; set; }
        public int Publications { get; set; }
        public int RangeTypes { get; set; }
        public int TextSearchConfigurations { get; set; }
        public int Operators { get; set; }
        public int Casts { get; set; }
        public int EventTriggers { get; set; }
    }
}
