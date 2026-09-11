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
    private bool _scriptDynamicDependencyRemovalForFunctions;
    private bool _includeIndexedViews;
    private bool _includeSynonyms;

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

    // MySQL specific
    private bool _includeEvents;

    // Orphan handling
    private OrphanHandlingMode _orphanHandlingMode;

    // Script validation
    internal bool _validateScripts;
    internal bool _saveInvalidScripts = true;
    private readonly ScriptValidator _scriptValidator = new();
    internal readonly List<(string FileName, string ErrorMessage, ScriptObjectType ObjectType)> _invalidScripts = new();

    internal CheckConstraintStyle CheckConstraintStyle => _checkConstraintStyle;
    private CheckConstraintStyle _checkConstraintStyle;
    private ObjectOrder _objectOrder = ObjectOrder.Name;
    private bool _preserveExistingOrder = true;

    // The wire encoding used to KINDLE and to READ the SQL Server schema model back out of the source.
    // Below the OPENJSON/FOR JSON cliff (pre-2016 binary), or when Source:CompatEncoding=legacy, the
    // model is generated as XML (GenerateTableXml / GenerateIndexedViewXml) and converted back to the
    // JSON model via ModelXmlSerializer.FromIngestXml. Resolved per source in CastSqlServerObjects from
    // best-effort version detection (defaults to Json when the version is unknown).
    private IngestEncoding _ingestEncoding = IngestEncoding.Json;

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

        var templateFile = Path.Combine(_templatePath, "Template.json");
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
            else
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
            JsonHelper.Write(templateFile, template);
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
            return overrideConnection;
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

    /// <summary>
    /// Excludes a LIVE ledger view (<c>&lt;table&gt;_Ledger</c>), which a ledger table generates and
    /// which carries no engine-reserved name prefix — only <c>sys.tables.ledger_view_id</c> identifies
    /// it. That column is 2022+, so the clause is added only when the source is new enough to have it;
    /// referencing it statically would fail to bind on an older server.
    /// </summary>
    /// <summary>
    /// Whether the SOURCE server can report XML_COMPRESSION. It DEPLOYS from SQL Server 2022 but only
    /// READS from 2025 -- sys.partitions.xml_compression does not exist before then -- and the legacy XML
    /// ingest path cannot read it at any version. Where this is false the package being refreshed is the
    /// only record of the setting, so ImportTableHelper carries it forward instead of letting an
    /// extraction that structurally cannot see it strip a property the server is honouring.
    /// </summary>
    private bool SourceCanReportXmlCompression =>
        _platform == Platform.SqlServer && _sourceMajor >= 17 && _ingestEncoding != IngestEncoding.Xml;

    private string LedgerViewFilter() =>
        _sourceMajor >= 16
            ? Environment.NewLine + "   AND NOT EXISTS (SELECT 1 FROM sys.tables lt WITH (NOLOCK) WHERE lt.ledger_view_id = o.object_id)"
            : "";

    public void CastTemplate()
    {
        _stopwatch.Start();
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var targetDb = config[SettingsKeys.Source.Database] ?? config[SettingsKeys.Source.Schema];
        if (string.IsNullOrEmpty(targetDb)) throw new Exception("Source database is required. Set 'Source:Database' in appsettings.json.");
        _productPath = Path.Join(config[SettingsKeys.ProductKeys.Path] ?? ".");

        LoadShouldCastSettings(config);

        _objectsToCast = (config[SettingsKeys.ShouldCast.ObjectList]?.ToLower() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

        var configStyle = Enum.TryParse<CheckConstraintStyle>(config[SettingsKeys.ProductKeys.CheckConstraintStyle], true, out var style)
            ? style : (CheckConstraintStyle?)null;

        // Extraction ordering. Both are extraction preferences -- nothing on the deploy path reads them.
        // PreserveExistingOrder defaults ON: it only has any effect when a file already exists, so a first
        // extract is unaffected and nobody is opted into a surprise.
        _objectOrder = Enum.TryParse<ObjectOrder>(config[SettingsKeys.ProductKeys.ObjectOrder], true, out var order)
            ? order : ObjectOrder.Name;
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
                _includeIndexedViews = config[SettingsKeys.ShouldCast.IndexedViews]?.ToLower() != "false";
                _includeSequences = config[SettingsKeys.ShouldCast.Sequences]?.ToLower() != "false";
                _includeSynonyms = config[SettingsKeys.ShouldCast.Synonyms]?.ToLower() != "false";
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
                break;

            case Platform.MySQL:
                _includeUserDefinedFunctions = config[SettingsKeys.ShouldCast.Functions]?.ToLower() != "false";
                _includeStoredProcedures = config[SettingsKeys.ShouldCast.Procedures]?.ToLower() != "false";
                _includeTableTriggers = config[SettingsKeys.ShouldCast.TableTriggers]?.ToLower() != "false";
                _includeEvents = config[SettingsKeys.ShouldCast.Events]?.ToLower() != "false";
                // MariaDb-only: MySQL has no native SEQUENCE object, so the flag stays false on plain MySQL
                // regardless of config — there is nothing for ShouldCast:Sequences to mean there.
                _includeSequences = _platform == Platform.MariaDb && config[SettingsKeys.ShouldCast.Sequences]?.ToLower() != "false";
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
        }
    }

    private void ApplyCheckConstraintStyle(string productFile, bool productIsNew, CheckConstraintStyle? configStyle)
    {
        var productJson = FileWrapper.GetFromFactory().ReadAllText(productFile);
        var product = productJson != null
            ? (JsonConvert.DeserializeObject<Product>(productJson) ?? new Product())
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
            var product = productJson != null ? JsonConvert.DeserializeObject<Product>(productJson) : null;
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

    private readonly Dictionary<string, ExtractionFileIndex> _folderIndexes = new();
    private readonly List<string> _pendingSqulerrorCleanup = new();

    private void BuildFileIndexes()
    {
        var templateFile = Path.Combine(_templatePath, "Template.json");
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
                break;

            case Platform.MySQL:
                if (_includeUserDefinedFunctions) folders.Add(ResolveFolderName("Functions", ScriptObjectType.Functions));
                if (_includeStoredProcedures) folders.Add(ResolveFolderName("Procedures", ScriptObjectType.Procedures));
                if (_includeTableTriggers) folders.Add(ResolveFolderName("Triggers", ScriptObjectType.Triggers));
                if (_includeEvents) folders.Add(ResolveFolderName("Events", ScriptObjectType.Events));
                if (_includeSequences) folders.Add(ResolveFolderName("Sequences", ScriptObjectType.Sequences)); // MariaDb-only; _includeSequences is gated in LoadShouldCastSettings
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
            }

            _progressLog.Info("Kindling The Forge");
            // Bake the detected SOURCE major version into SchemaSmith.fn_ServerMajorVersion so the version-gated
            // compare/extraction helpers resolve the source version on a genuine pre-2016 binary (where the
            // former SESSION_CONTEXT transport is unavailable); 0 falls back to the server property on modern.
            ForgeKindler.KindleTheForge(command, _platform, encoding: _ingestEncoding, serverMajorVersion: sourceMajor,
                                        allowReadOnlyTarget: true);

            if (_includeTables) ExtractTableDefinitions(command, targetDb);
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
            if (_includeSequences) ScriptSqlServerSequences(command);
            if (_includeSynonyms) ScriptSqlServerSynonyms(command);
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
        command.CommandText = @"
SELECT s.name AS SchemaName, tt.name AS TypeName, tt.type_table_object_id
  FROM sys.table_types tt
  JOIN sys.schemas s ON tt.schema_id = s.schema_id
 WHERE tt.is_user_defined = 1
 ORDER BY s.name, tt.name";

        var tableTypes = new List<(string Schema, string Name, int ObjectId)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                tableTypes.Add((schema, name, reader.GetInt32(2)));
            }
        }

        foreach (var (schema, name, objectId) in tableTypes)
        {
            command.CommandText = $@"
SELECT c.name, TYPE_NAME(c.user_type_id) AS TypeName,
       c.max_length, c.precision, c.scale, c.is_nullable,
       c.is_identity, c.is_computed,
       ts.name AS UserTypeName, tss.name AS UserTypeSchema,
       c.column_id
  FROM sys.columns c
  LEFT JOIN sys.types ts ON c.user_type_id = ts.user_type_id AND ts.is_user_defined = 1
  LEFT JOIN sys.schemas tss ON ts.schema_id = tss.schema_id
 WHERE c.object_id = {objectId}
 ORDER BY c.column_id";

            var columns = new List<(string Name, string TypeName, short MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsIdentity, bool IsComputed, string UserTypeName, string UserTypeSchema)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt16(2),
                        reader.GetByte(3),
                        reader.GetByte(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9)
                    ));
                }
            }

            command.CommandText = $@"
SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
       ic.column_id, c.name AS ColumnName, ic.is_descending_key
  FROM sys.indexes i
  JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
  JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
 WHERE i.object_id = {objectId}
   AND i.type > 0
 ORDER BY i.index_id, ic.key_ordinal";

            var indexes = new Dictionary<string, (string TypeDesc, bool IsUnique, bool IsPrimaryKey, List<(string ColumnName, bool IsDescending)> Columns)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var indexName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var typeDesc = reader.GetString(1);
                    var isUnique = reader.GetBoolean(2);
                    var isPrimaryKey = reader.GetBoolean(3);
                    var columnName = reader.GetString(5);
                    var isDescending = reader.GetBoolean(6);

                    if (!indexes.ContainsKey(indexName))
                        indexes[indexName] = (typeDesc, isUnique, isPrimaryKey, new List<(string, bool)>());
                    indexes[indexName].Columns.Add((columnName, isDescending));
                }
            }

            command.CommandText = $@"
SELECT cc.name, cc.definition
  FROM sys.check_constraints cc
 WHERE cc.parent_object_id = {objectId}
 ORDER BY cc.name";

            var checkConstraints = new List<(string Name, string Definition)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    checkConstraints.Add((reader.GetString(0), reader.GetString(1)));
            }

            var lines = new List<string>();
            lines.Add($"CREATE TYPE [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}] AS TABLE(");

            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                string typeSpec;
                if (col.UserTypeName != null)
                    typeSpec = $"[{Identifier.EscapeDelimited(col.UserTypeSchema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(col.UserTypeName, Platform.SqlServer)}]";
                else
                    typeSpec = FormatBaseType(col.TypeName, col.MaxLength, col.Precision, col.Scale);

                var nullSpec = col.IsNullable ? "NULL" : "NOT NULL";
                var comma = (i < columns.Count - 1 || indexes.Count > 0 || checkConstraints.Count > 0) ? "," : "";
                lines.Add($"\t[{Identifier.EscapeDelimited(col.Name, Platform.SqlServer)}] {typeSpec} {nullSpec}{comma}");
            }

            var constraintEntries = new List<string>();
            foreach (var kvp in indexes)
            {
                var idx = kvp.Value;
                var colList = string.Join(",\r\n", idx.Columns.Select(c => $"\t[{Identifier.EscapeDelimited(c.ColumnName, Platform.SqlServer)}] " + (c.IsDescending ? "DESC" : "ASC")));

                if (idx.IsPrimaryKey)
                    constraintEntries.Add($"\tPRIMARY KEY {idx.TypeDesc} \r\n(\r\n{colList}\r\n)");
                else
                {
                    var unique = idx.IsUnique ? "UNIQUE " : "";
                    constraintEntries.Add($"\t{unique}{idx.TypeDesc} \r\n(\r\n{colList}\r\n)");
                }
            }

            foreach (var cc in checkConstraints)
                constraintEntries.Add($"\tCHECK {cc.Definition}");

            for (var i = 0; i < constraintEntries.Count; i++)
            {
                var suffix = i < constraintEntries.Count - 1 ? "," : "";
                lines.Add(constraintEntries[i] + suffix);
            }

            lines.Add(")");

            var createScript = string.Join("\r\n", lines);

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

        command.CommandText = @"
SELECT s.name AS SchemaName, sq.name AS SequenceName, TYPE_NAME(sq.system_type_id) AS DataType,
       sq.start_value, sq.increment, sq.minimum_value, sq.maximum_value, sq.is_cycling
  FROM sys.sequences sq
  JOIN sys.schemas s ON sq.schema_id = s.schema_id
 ORDER BY s.name, sq.name";

        // start_value/increment/minimum_value/maximum_value are sql_variant (a sequence can be any exact
        // numeric type) — read via ToString() rather than a numeric conversion so a DECIMAL/NUMERIC-typed
        // sequence's value round-trips as a literal instead of risking overflow or truncation.
        var sequences = new List<(string Schema, string Name, string DataType, string StartValue, string Increment, string MinValue, string MaxValue, bool IsCycling)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                if (!ShouldExtractFromSchema(schema)) continue;
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower()) && !_objectsToCast.Contains($"{schema}.{name}".ToLower())) continue;
                sequences.Add((schema, name, reader.GetString(2),
                    reader.GetValue(3).ToString(), reader.GetValue(4).ToString(),
                    reader.GetValue(5).ToString(), reader.GetValue(6).ToString(),
                    reader.GetBoolean(7)));
            }
        }

        foreach (var (schema, name, dataType, startValue, increment, minValue, maxValue, isCycling) in sequences)
        {
            var createScript =
                $"CREATE SEQUENCE [{Identifier.EscapeDelimited(schema, Platform.SqlServer)}].[{Identifier.EscapeDelimited(name, Platform.SqlServer)}]\r\n" +
                $"\tAS {dataType}\r\n" +
                $"\tSTART WITH {startValue}\r\n" +
                $"\tINCREMENT BY {increment}\r\n" +
                $"\tMINVALUE {minValue}\r\n" +
                $"\tMAXVALUE {maxValue}\r\n" +
                $"\t{(isCycling ? "CYCLE" : "NO CYCLE")}";

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

            if (_scriptDynamicDependencyRemovalForFunctions)
            {
                var dependencyBlock =
                    $"\r\nDECLARE @v_SearchTerm VARCHAR(2000) = '%{name}%'\r\n" +
                    "DECLARE @v_SQL VARCHAR(MAX) = (SELECT STRING_AGG(Task, ';' + CHAR(13) + CHAR(10)) \r\n" +
                    "                                 FROM (SELECT 'ALTER TABLE [' + OBJECT_SCHEMA_NAME(cc.parent_object_id) + '].[' + OBJECT_NAME(cc.parent_object_id) + '] DROP CONSTRAINT IF EXISTS [' + OBJECT_NAME(cc.[name]) + ']' AS Task\r\n" +
                    "                                         FROM sys.check_constraints cc\r\n" +
                    "                                         WHERE cc.[definition] LIKE @v_SearchTerm\r\n" +
                    "                                            OR EXISTS (SELECT *\r\n" +
                    "                                                         FROM sys.computed_columns cc2\r\n" +
                    "                                                         WHERE cc2.[definition] LIKE @v_SearchTerm\r\n" +
                    "                                                           AND cc2.[object_id] = cc.parent_object_id\r\n" +
                    "                                                           AND cc2.column_id = cc.parent_column_id)\r\n" +
                    "                                       UNION ALL\r\n" +
                    "                                       SELECT 'ALTER TABLE [' + OBJECT_SCHEMA_NAME(dc.parent_object_id) + '].[' + OBJECT_NAME(dc.parent_object_id) + '] DROP CONSTRAINT IF EXISTS [' + OBJECT_NAME(dc.[name]) + ']'\r\n" +
                    "                                         FROM sys.default_constraints dc\r\n" +
                    "                                         WHERE dc.[definition] LIKE @v_SearchTerm\r\n" +
                    "                                            OR EXISTS (SELECT *\r\n" +
                    "                                                         FROM sys.computed_columns cc\r\n" +
                    "                                                         WHERE cc.[definition] LIKE @v_SearchTerm\r\n" +
                    "                                                           AND cc.[object_id] = dc.parent_object_id\r\n" +
                    "                                                           AND cc.column_id = dc.parent_column_id)\r\n" +
                    "                                       UNION ALL\r\n" +
                    "                                       SELECT 'ALTER TABLE [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT IF EXISTS [' + OBJECT_NAME(fk.[name]) + ']'\r\n" +
                    "                                         FROM sys.foreign_keys fk\r\n" +
                    "                                         WHERE EXISTS (SELECT *\r\n" +
                    "                                                         FROM sys.computed_columns cc\r\n" +
                    "                                                         JOIN sys.foreign_key_columns fc ON fk.[object_id] = fk.[object_id]\r\n" +
                    "                                                                                        AND ((fc.parent_object_id = cc.[object_id] AND fc.parent_column_id = cc.column_id)\r\n" +
                    "                                                                                          OR (fc.referenced_object_id = cc.[object_id] AND fc.referenced_column_id = cc.column_id))\r\n" +
                    "                                                         WHERE cc.[definition] LIKE @v_SearchTerm)\r\n" +
                    "                                       UNION ALL\r\n" +
                    "                                       SELECT 'DROP INDEX IF EXISTS [' + si.[name] + '] ON [' + OBJECT_SCHEMA_NAME(si.[object_id]) + '].[' + OBJECT_NAME(si.[object_id]) + ']'\r\n" +
                    "                                         FROM sys.indexes si\r\n" +
                    "                                         WHERE si.filter_definition LIKE @v_SearchTerm\r\n" +
                    "                                            OR EXISTS (SELECT *\r\n" +
                    "                                                         FROM sys.computed_columns cc\r\n" +
                    "                                                         JOIN sys.index_columns ic ON ic.[object_id] = si.[object_id]\r\n" +
                    "                                                                                  AND ic.index_id = si.index_id\r\n" +
                    "                                                                                  AND ic.column_id = cc.column_id\r\n" +
                    "                                                         WHERE cc.[definition] LIKE @v_SearchTerm\r\n" +
                    "                                                           AND cc.[object_id] = si.[object_id])\r\n" +
                    "                                       UNION ALL\r\n" +
                    "                                       SELECT 'ALTER TABLE [' + OBJECT_SCHEMA_NAME(cc.[object_id]) + '].[' + OBJECT_NAME(cc.[object_id]) + '] DROP COLUMN IF EXISTS [' + cc.[name] + ']'\r\n" +
                    "                                         FROM sys.computed_columns cc\r\n" +
                    "                                         WHERE cc.[definition] LIKE @v_SearchTerm) x) + ';'\r\n" +
                    "EXEC(@v_SQL) -- Remove any dependencies before updating the function\r\n" +
                    "GO\r\n";

                var firstGoEnd = sql.IndexOf("GO\r\n\r\n") + 4;
                sql = sql.Substring(0, firstGoEnd) + dependencyBlock + sql.Substring(firstGoEnd);
            }

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
   AND o.name NOT LIKE 'MSSQL[_]DroppedLedgerView[_]%'" + LedgerViewFilter() + @"
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

    private void ScriptSqlServerFullTextCatalogs(IDbCommand command)
    {
        _progressLog.Info("Casting FullText Catalog Scripts");
        var castPath = GetCastPath(ScriptObjectType.FullTextCatalogs, "FullTextCatalogs");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);

        command.CommandText = @"
SELECT name
  FROM sys.fulltext_catalogs
 ORDER BY name";

        var catalogs = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(name.ToLower())) continue;
                catalogs.Add(name);
            }
        }

        foreach (var name in catalogs)
        {
            var script = $"IF NOT EXISTS (SELECT * FROM sysfulltextcatalogs ftc WHERE ftc.name = N'{EscapeSql(name)}')\r\n" +
                         $"CREATE FULLTEXT CATALOG [{Identifier.EscapeDelimited(name, Platform.SqlServer)}] ";

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
SELECT sm.definition, sm.uses_ansi_nulls, sm.uses_quoted_identifier
  FROM sys.sql_modules sm
  JOIN sys.triggers tr ON sm.object_id = tr.object_id
 WHERE tr.parent_class = 0 AND tr.name = '{EscapeSql(triggerName)}'";

            string definition = null;
            bool usesAnsiNulls = true;
            bool usesQuotedIdentifier = true;

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        _progressLog.Warn($"  WARNING: {triggerName} is encrypted, skipping");
                        continue;
                    }
                    definition = reader.GetString(0);
                    usesAnsiNulls = reader.GetBoolean(1);
                    usesQuotedIdentifier = reader.GetBoolean(2);
                }
            }

            if (definition == null) continue;

            definition = definition.Trim();

            if (!definition.Contains("\r\n"))
                definition = definition.Replace("\n", "\r\n");

            var createMatch = Regex.Match(definition, @"(?<!\w)CREATE(\s+)TRIGGER\b", RegexOptions.IgnoreCase);
            if (createMatch.Success)
                definition = definition.Substring(0, createMatch.Index) + "CREATE OR ALTER" + createMatch.Value.Substring("CREATE".Length) + definition.Substring(createMatch.Index + createMatch.Length);

            var escapedName = Regex.Escape(triggerName);
            var namePattern = $@"(?<=TRIGGER\s+)\[?{escapedName}\]?";
            definition = Regex.Replace(definition, namePattern, $"[{Identifier.EscapeDelimited(triggerName, Platform.SqlServer)}]", RegexOptions.IgnoreCase);

            definition = Regex.Replace(definition, @"(?<=\bAS[ \t]*\r\n)([ \t]*)(?=\S)", "$1\r\n");

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

    internal static string ConvertToCreateOrAlter(string definition, string schemaName, string objectName)
    {
        var createMatch = Regex.Match(definition,
            @"(?<!\w)CREATE(\s+)(PROCEDURE|FUNCTION|VIEW|TRIGGER)\b",
            RegexOptions.IgnoreCase);
        var result = createMatch.Success
            ? definition.Substring(0, createMatch.Index) + "CREATE OR ALTER" + createMatch.Value.Substring("CREATE".Length) + definition.Substring(createMatch.Index + createMatch.Length)
            : definition;

        var escapedSchema = Regex.Escape(schemaName);
        var escapedName = Regex.Escape(objectName);
        var namePattern = $@"\[?{escapedSchema}\]?\.\[?{escapedName}\]?";
        var match = Regex.Match(result, namePattern, RegexOptions.IgnoreCase);
        if (match.Success)
            result = result.Substring(0, match.Index) + $"[{Identifier.EscapeDelimited(schemaName, Platform.SqlServer)}].[{Identifier.EscapeDelimited(objectName, Platform.SqlServer)}]" + result.Substring(match.Index + match.Length);

        return result;
    }

    private string ScriptSqlServerProgrammableObject(IDbCommand command, string schemaName, string objectName,
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

        definition = ConvertToCreateOrAlter(definition, schemaName, objectName);

        definition = Regex.Replace(definition, @"(?<=\bAS[ \t]*\r\n)([ \t]*)(?=\S)", "$1\r\n");

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
                continue;
            }
            var viewObj = JsonConvert.DeserializeObject<SqlServerIndexedView>(viewJson);
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
            JsonHelper.Write(viewFile, viewObj);
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
        }
        finally
        {
            connection.Close();
        }
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
        command.CommandText = @"
SELECT t.schemaname, t.tablename, c.relkind, c.relispartition
  FROM pg_tables t
  JOIN pg_class c ON c.relname = t.tablename
                 AND c.relnamespace = (SELECT n.oid FROM pg_namespace n WHERE n.nspname = t.schemaname)
                 AND c.relpersistence = 'p'
  WHERE t.schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
  ORDER BY t.schemaname, t.tablename;
";

        _progressLog.Info("Casting Table Structures");
        var tables = new List<(string Schema, string Table, string RelKind, bool IsPartitionChild)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var sch = reader["schemaname"].ToString();
                if (!ShouldExtractFromSchema(sch)) continue;
                tables.Add((sch, reader["tablename"].ToString(), reader["relkind"].ToString(), Convert.ToBoolean(reader["relispartition"])));
            }
        }

        var castPath = Path.Combine(_templatePath, "Tables");
        DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
        var resolver = new TableFileResolver(castPath, _platform, _isSchemaTemplate, IsVariantActive);

        foreach (var (schema, table, relKind, isPartitionChild) in tables)
        {
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains($"{schema}.{table}".ToLower()) && !_objectsToCast.Contains(table.ToLower())) continue;

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
                JsonHelper.Write(tableFile, tableObj);
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
        command.CommandText = @"
SELECT n.nspname AS SchemaName, t.typname AS TypeName
  FROM pg_type t
  JOIN pg_namespace n ON n.oid = t.typnamespace
  WHERE t.typtype = 'd'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
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
                continue;
            }

            var typeObj = JsonConvert.DeserializeObject<PostgreSqlDomainType>(typeJson);
            if (_isSchemaTemplate) typeObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            JsonHelper.Write(file, typeObj);
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
        command.CommandText = @"
SELECT n.nspname AS SchemaName, t.typname AS TypeName
  FROM pg_type t
  JOIN pg_namespace n ON n.oid = t.typnamespace
  WHERE t.typtype = 'e'
    AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%'
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
                continue;
            }

            var typeObj = JsonConvert.DeserializeObject<PostgreSqlEnumType>(typeJson);
            if (_isSchemaTemplate) typeObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            JsonHelper.Write(file, typeObj);
            _stats.EnumTypes++;
        }
    }

    private void CastPostgreSqlCompositeTypes(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Composite Types' AS Folder,
       ns.nspname || '.' || t.typname AS FullName,
       '
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = ''' || ns.nspname || ''' AND t.typname = ''' || t.typname || ''') THEN
        CREATE TYPE ""' || ns.nspname || '"".""' || t.typname || '""  AS (' || STRING_AGG(a.attname || ' ' || FORMAT_TYPE(a.atttypid, a.atttypmod), ', ' ORDER BY a.attnum) || ');
    END IF;
END
$$;' AS Code
  FROM pg_type t
  JOIN pg_class c ON t.typrelid = c.oid
  JOIN pg_attribute a ON c.oid = a.attrelid
  JOIN pg_namespace ns ON t.typnamespace = ns.oid
  WHERE t.typtype = 'c'
    AND a.attnum > 0
    AND pg_type_is_visible(t.oid)
    AND ns.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND ns.nspname NOT LIKE 'pg_temp_%'
    AND ns.nspname NOT LIKE 'pg_toast_temp_%'
    AND NOT EXISTS (SELECT * FROM pg_Tables tbl WHERE tbl.schemaname = ns.nspname AND tbl.tablename = t.typname)
    AND NOT EXISTS (SELECT * FROM pg_views v WHERE v.schemaname = ns.nspname AND v.viewname = t.typname)
  GROUP BY ns.nspname, t.typname;
";
        PerformPostgreSqlCasting(command, "Composite Types");
    }

    private void CastPostgreSqlCollations(IDbCommand command)
    {
        // QUOTE_IDENT/QUOTE_LITERAL build the Code text rather than hand-doubled quote literals — each
        // returns its value already correctly escaped, so no nested-quote arithmetic is needed here.
        // Best-effort: libc-provider collations round-trip via LC_COLLATE/LC_CTYPE. ICU-provider
        // collations (collprovider = 'i') fall back to collcollate as the LOCALE value, which covers
        // pg_import_system_collations-derived ICU rows but not every hand-authored ICU nuance.
        command.CommandText = @"
SELECT 'Collations' AS Folder,
       n.nspname || '.' || c.collname AS FullName,
       'CREATE COLLATION IF NOT EXISTS ' || QUOTE_IDENT(n.nspname) || '.' || QUOTE_IDENT(c.collname) || ' (' ||
       CASE WHEN c.collprovider = 'i' THEN 'PROVIDER = icu, LOCALE = ' || QUOTE_LITERAL(COALESCE(c.collcollate, c.collname))
            ELSE 'LC_COLLATE = ' || QUOTE_LITERAL(c.collcollate) || ', LC_CTYPE = ' || QUOTE_LITERAL(c.collctype)
       END ||
       CASE WHEN c.collisdeterministic = false THEN ', DETERMINISTIC = false' ELSE '' END ||
       ');' AS Code
  FROM pg_collation c
  JOIN pg_namespace n ON c.collnamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%';
";
        PerformPostgreSqlCasting(command, "Collations");
    }

    private void CastPostgreSqlFunctions(IDbCommand command)
    {
        command.CommandText = @"
SELECT CASE p.prokind
            WHEN 'f' THEN CASE WHEN prorettype = (SELECT oid FROM pg_type WHERE typname = 'trigger') THEN 'Trigger ' ELSE '' END || 'Functions'
            WHEN 'w' THEN 'Window Functions'
            END AS Folder,
       n.nspname || '.' || p.proname AS FullName,
       PG_GET_FUNCTIONDEF(p.oid) AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%'
  WHERE p.prokind IN ('f', 'w');
";
        PerformPostgreSqlCasting(command, "Functions");
    }

    private void CastPostgreSqlAggregates(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Aggregates' AS Folder,
       n.nspname || '.' || p.proname AS FullName,
       'CREATE OR REPLACE AGGREGATE ' || n.nspname || '.' || p.proname || '(' || FORMAT_TYPE(a.aggtranstype, null) || ') '
       || '(sfunc = ' || a.aggtransfn
       || ', stype = ' || FORMAT_TYPE(a.aggtranstype, null)
       || CASE WHEN op.oprname IS NULL THEN '' ELSE ', sortop = ' || op.oprname END
       || CASE WHEN a.agginitval IS NULL THEN '' ELSE ', initcond = ' || a.agginitval END
       || ')' AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
  JOIN pg_aggregate a ON a.aggfnoid = p.oid
  LEFT JOIN pg_operator op ON op.oid = a.aggsortop
  WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND n.nspname NOT LIKE 'pg_temp_%'
    AND n.nspname NOT LIKE 'pg_toast_temp_%';
";
        PerformPostgreSqlCasting(command, "Aggregates");
    }

    private void CastPostgreSqlProcedures(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Procedures' AS Folder,
       n.nspname || '.' || p.proname AS FullName,
       PG_GET_FUNCTIONDEF(p.oid) AS Code
  FROM pg_proc p
  JOIN pg_namespace n ON p.pronamespace = n.oid
                     AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
                     AND n.nspname NOT LIKE 'pg_temp_%'
                     AND n.nspname NOT LIKE 'pg_toast_temp_%'
  WHERE p.prokind = 'p';
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
                      AND d.deptype IN ('i', 'a'))
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
                continue;
            }

            var seqObj = JsonConvert.DeserializeObject<PostgreSqlSequence>(seqJson);
            if (_isSchemaTemplate) seqObj.Schema = null;
            var file = ResolveOutputPath(castPath, EncodeObjectFileName(schema, name, ".json"));
            JsonHelper.Write(file, seqObj);
            _stats.Sequences++;
        }
    }

    private void CastPostgreSqlPublications(IDbCommand command)
    {
        // Best-effort: covers publish=insert/update/delete/truncate (all PG10+, below the PG12 floor)
        // and FOR ALL TABLES / FOR TABLE. PG13's publish_via_partition_root is not emitted — a target
        // above the floor without partitioned-root publishing loses only that one clause on re-extract.
        // CREATE PUBLICATION has no IF NOT EXISTS form, so the guarded create goes through a DO $$ ... $$
        // block (like Composite/Enum Types above). QUOTE_IDENT/QUOTE_LITERAL supply every interpolated
        // value pre-escaped, and every literal chunk below opens and closes with exactly one quote each —
        // no hand-doubled nested quoting, unlike the older ""schema""."" pattern elsewhere in this file.
        command.CommandText = @"
SELECT 'Publications' AS Folder,
       p.pubname AS FullName,
       E'\nDO $$\nBEGIN\n    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = ' || QUOTE_LITERAL(p.pubname) ||
       E') THEN\n        CREATE PUBLICATION ' || QUOTE_IDENT(p.pubname) || ' ' ||
       CASE WHEN p.puballtables THEN 'FOR ALL TABLES '
            ELSE 'FOR TABLE ' || COALESCE(STRING_AGG(DISTINCT QUOTE_IDENT(pt.schemaname) || '.' || QUOTE_IDENT(pt.tablename), ', '), '') || ' '
       END ||
       'WITH (publish = ' || QUOTE_LITERAL(CONCAT_WS(',',
            CASE WHEN p.pubinsert THEN 'insert' END,
            CASE WHEN p.pubupdate THEN 'update' END,
            CASE WHEN p.pubdelete THEN 'delete' END,
            CASE WHEN p.pubtruncate THEN 'truncate' END)) ||
       E');\n    END IF;\nEND\n$$;' AS Code
  FROM pg_publication p
  LEFT JOIN pg_publication_tables pt ON pt.pubname = p.pubname
 WHERE p.pubname NOT LIKE 'pg\_%'
 GROUP BY p.pubname, p.puballtables, p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate;
";
        PerformPostgreSqlCasting(command, "Publications");
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
    AND r.rulename != '_RETURN';
";
        PerformPostgreSqlCasting(command, "Rules");
    }

    private void CastPostgreSqlTriggers(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Triggers' AS Folder,
       ns.nspname || '.' || tbl.relname || '.' || t.tgname AS FullName,
       REPLACE(PG_GET_TRIGGERDEF(t.oid), 'CREATE TRIGGER', 'CREATE OR REPLACE TRIGGER') AS Code
  FROM pg_trigger t
  JOIN pg_class tbl ON t.tgrelid = tbl.oid
  JOIN pg_namespace ns ON tbl.relnamespace = ns.oid
  WHERE NOT t.tgisinternal
    AND ns.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND ns.nspname NOT LIKE 'pg_temp_%'
    AND ns.nspname NOT LIKE 'pg_toast_temp_%';
";
        PerformPostgreSqlCasting(command, "Triggers");
    }

    private void CastPostgreSqlViews(IDbCommand command)
    {
        command.CommandText = @"
SELECT 'Views' AS Folder,
       schemaname || '.' || viewname AS FullName,
       'CREATE OR REPLACE VIEW ""' || schemaname || '"".""' || viewname || '"" AS' || E'\n' || definition AS Code
  FROM pg_views
  WHERE schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND schemaname NOT LIKE 'pg_temp_%'
    AND schemaname NOT LIKE 'pg_toast_temp_%';
";
        PerformPostgreSqlCasting(command, "Views");
    }

    private void CastPostgreSqlMaterializedViews(IDbCommand command)
    {
        command.CommandText = @"
SELECT mv.schemaname, mv.matviewname
  FROM pg_matviews mv
  WHERE mv.schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
    AND mv.schemaname NOT LIKE 'pg_temp_%'
    AND mv.schemaname NOT LIKE 'pg_toast_temp_%'
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
                continue;
            }
            var viewObj = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(viewJson);
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
            JsonHelper.Write(viewFile, viewObj);
            _stats.MaterializedViews++;
        }
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
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(fullName.ToLower()) && !_objectsToCast.Contains(Identifier.SplitQualifiedName(fullName, _platform).Name.ToLower())) continue;

                // Schema-template mode: skip objects outside the source schema; emit unqualified filenames.
                // Delimiter-aware split so a dotted delimited schema name ([my.schema]) is not mis-split (#272).
                var (qualifiedSchema, objectName) = Identifier.SplitQualifiedName(fullName, _platform);
                if (_isSchemaTemplate && qualifiedSchema != null && !ShouldExtractFromSchema(qualifiedSchema)) continue;

                var outputName = _isSchemaTemplate && qualifiedSchema != null
                    ? EncodeFileName(objectName, ".sql")
                    : EncodeFullName(fullName, ".sql");
                var fileName = ResolveOutputPath(castPath, outputName);
                var folderName = reader["Folder"].ToString();
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
                command.CommandText = "SET sql_mode='PIPES_AS_CONCAT';";
                command.ExecuteNonQuery();

                _progressLog.Info("Kindling The Forge");
                ForgeKindler.KindleTheForge(command, _platform, allowReadOnlyTarget: true);

                if (_includeTables) ExtractMySqlTableDefinitions(command, targetSchema);
                if (_includeUserDefinedFunctions) ScriptMySqlFunctions(command, targetSchema);
                if (_includeViews) ScriptMySqlViews(command, targetSchema);
                if (_includeStoredProcedures) ScriptMySqlProcedures(command, targetSchema);
                if (_includeTableTriggers) ScriptMySqlTriggers(command, targetSchema);
                if (_includeEvents) ScriptMySqlEvents(command, targetSchema);
                if (_includeSequences) ScriptMariaDbSequences(command, targetSchema);
            }
            finally
            {
                connection.Close();
            }
        }
        catch (MySqlException ex)
        {
            _progressLog.Error($"MySQL Error: {ex.Message}");
            throw new Exception($"Database connection or query failed: {ex.Message}", ex);
        }
    }

    private void ScriptMySqlFunctions(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Function Scripts");
        // EXTERNAL_LANGUAGE is NULL on MariaDB for SQL routines (MySQL reports 'SQL'), and one NULL
        // operand nulls the whole || chain, yielding an empty script file. ORDINAL_POSITION 0 is the
        // function's return value, excluded explicitly rather than relying on GROUP_CONCAT skipping
        // its NULL PARAMETER_NAME — which is why the parameter columns must NOT be COALESCEd.
        command.CommandText = $@"
SELECT 'Functions' AS Folder,
       ROUTINE_NAME AS FullName,
       'DROP FUNCTION IF EXISTS `' || ROUTINE_NAME || '`;\nDELIMITER //\n' ||
       'CREATE FUNCTION `' || ROUTINE_NAME || '` (' ||
       COALESCE((SELECT GROUP_CONCAT(PARAMETER_NAME || ' ' || DTD_IDENTIFIER ORDER BY ORDINAL_POSITION SEPARATOR ',')
                   FROM INFORMATION_SCHEMA.PARAMETERS p
                   WHERE p.SPECIFIC_NAME = r.ROUTINE_NAME
                     AND p.ROUTINE_TYPE = r.ROUTINE_TYPE
                     AND p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA
                     AND p.ORDINAL_POSITION > 0), '') || ')\n  RETURNS ' || DTD_IDENTIFIER ||
       '\n' || '  LANGUAGE ' || COALESCE(EXTERNAL_LANGUAGE, 'SQL') ||
       '\n' || CASE WHEN IS_DETERMINISTIC = 'Yes' THEN '  DETERMINISTIC' ELSE '  NOT DETERMINISTIC' END ||
       CASE WHEN NULLIF(SQL_DATA_ACCESS, '') IS NOT NULL THEN '\n  ' || SQL_DATA_ACCESS ELSE '' END ||
       '\n' || '  SQL SECURITY ' || SECURITY_TYPE ||
       '\n' || ROUTINE_DEFINITION || ' //\nDELIMITER ;' AS Code
  FROM INFORMATION_SCHEMA.ROUTINES r
  WHERE ROUTINE_SCHEMA = '{EscapeSql(targetSchema)}'
    AND ROUTINE_TYPE = 'FUNCTION'
    AND ROUTINE_NAME NOT LIKE 'SchemaSmith\_%'
";
        _stats.Functions = PerformMySqlCasting(command, "Functions");
    }

    private void ScriptMySqlViews(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting View Scripts");
        command.CommandText = $@"
SELECT 'Views' AS Folder,
       TABLE_NAME AS FullName,
       'DROP VIEW IF EXISTS `' || TABLE_NAME || '`;\nCREATE VIEW `' || TABLE_NAME || '` AS\n' || VIEW_DEFINITION AS Code
  FROM INFORMATION_SCHEMA.VIEWS
  WHERE TABLE_SCHEMA = '{EscapeSql(targetSchema)}'
    AND TABLE_NAME NOT LIKE 'SchemaSmith\_%'
";
        _stats.Views = PerformMySqlCasting(command, "Views");
    }

    private void ScriptMySqlProcedures(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Stored Procedure Scripts");
        // See ScriptMySqlFunctions: EXTERNAL_LANGUAGE is NULL on MariaDB and would null the whole
        // || chain, writing a 0-byte script.
        command.CommandText = $@"
SELECT 'Procedures' AS Folder,
       ROUTINE_NAME AS FullName,
       'DROP PROCEDURE IF EXISTS `' || ROUTINE_NAME || '`;\nDELIMITER //\n' ||
       'CREATE PROCEDURE `' || ROUTINE_NAME || '` (' ||
       COALESCE((SELECT GROUP_CONCAT(PARAMETER_MODE || ' ' || PARAMETER_NAME || ' ' || DTD_IDENTIFIER ORDER BY ORDINAL_POSITION SEPARATOR ',')
                   FROM INFORMATION_SCHEMA.PARAMETERS p
                   WHERE p.SPECIFIC_NAME = r.ROUTINE_NAME
                     AND p.ROUTINE_TYPE = r.ROUTINE_TYPE
                     AND p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA
                     AND p.ORDINAL_POSITION > 0), '') || ')' ||
       '\n' || '  LANGUAGE ' || COALESCE(EXTERNAL_LANGUAGE, 'SQL') ||
       '\n' || CASE WHEN IS_DETERMINISTIC = 'Yes' THEN '  DETERMINISTIC' ELSE '  NOT DETERMINISTIC' END ||
       CASE WHEN NULLIF(SQL_DATA_ACCESS, '') IS NOT NULL THEN '\n  ' || SQL_DATA_ACCESS ELSE '' END ||
       '\n' || '  SQL SECURITY ' || SECURITY_TYPE ||
       '\n' || ROUTINE_DEFINITION || ' //\nDELIMITER ;' AS Code
  FROM INFORMATION_SCHEMA.ROUTINES r
  WHERE ROUTINE_SCHEMA = '{EscapeSql(targetSchema)}'
    AND ROUTINE_TYPE = 'PROCEDURE'
    AND ROUTINE_NAME NOT LIKE 'SchemaSmith\_%'
";
        _stats.Procedures = PerformMySqlCasting(command, "Procedures");
    }

    private void ScriptMySqlTriggers(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Trigger Scripts");
        command.CommandText = $@"
SELECT 'Triggers' AS Folder,
       TRIGGER_NAME AS FullName,
       'DROP TRIGGER IF EXISTS `' || TRIGGER_NAME || '`;\nDELIMITER //\nCREATE TRIGGER `' || TRIGGER_NAME || '`\n  ' || ACTION_TIMING || ' ' || EVENT_MANIPULATION || '\n  ON `' || EVENT_OBJECT_TABLE || '` ' ||
       '\n  FOR EACH ' || ACTION_ORIENTATION || ' \nBEGIN\n' || ACTION_STATEMENT || ';\nEND //\nDELIMITER ;' AS Code
  FROM INFORMATION_SCHEMA.TRIGGERS
  WHERE TRIGGER_SCHEMA = '{EscapeSql(targetSchema)}'
    AND TRIGGER_NAME NOT LIKE 'SchemaSmith\_%'
";
        _stats.Triggers = PerformMySqlCasting(command, "Triggers");
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
                continue;
            }

            var eventObj = JsonConvert.DeserializeObject<MySqlEvent>(eventJson);
            var file = ResolveOutputPath(castPath, EncodeObjectFileName("", name, ".json"));
            JsonHelper.Write(file, eventObj);
            _stats.Events++;
        }
    }

    /// <summary>
    /// MariaDb-only: MySQL has no SEQUENCE object, so this is reached only when the caller's
    /// _includeSequences flag is true, which LoadShouldCastSettings gates to Platform.MariaDb.
    /// Two-step like ExtractMySqlTableDefinitions above (list, then per-item) rather than the
    /// generic PerformMySqlCasting single-query shape — SHOW CREATE SEQUENCE needs a per-sequence
    /// round trip, and MariaDB's own DDL-generation is the most reliable source for sequence options
    /// (increment/min/max/cache/cycle) rather than hand-reconstructing them from INFORMATION_SCHEMA.
    /// </summary>
    private void ScriptMariaDbSequences(IDbCommand command, string targetSchema)
    {
        _progressLog.Info("Casting Sequence Scripts");
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

            // SHOW CREATE SEQUENCE emits a plain CREATE SEQUENCE; rewrap as CREATE OR REPLACE so the
            // extracted script is idempotent on re-run, matching every other scripted-object folder here.
            var script = createStatement.Replace("CREATE SEQUENCE", "CREATE OR REPLACE SEQUENCE");

            var fileName = ResolveOutputPath(castPath, EncodeFullName(name, ".sql"));
            script = RewriteSqlBodyForSchemaTemplate(script, fileName);
            _progressLog.Info($"  Casting {fileName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            _stats.Sequences++;
        }
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

            var currentTable = 0;
            // MySQL stored procedures take no default parameter values, so the ordering choice travels
            // as a session variable rather than an argument -- see the proc for why. Set once for the
            // connection, not per table: it is session state, and a SET per table would add a round
            // trip to every table in the schema for a value that never changes.
            commandJson.CommandText = $"SET @SchemaSmith_ObjectOrder = '{_objectOrder}'";
            commandJson.ExecuteNonQuery();

            foreach (var (schema, table) in tables)
            {
                if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(table.ToLower()) && !_objectsToCast.Contains($"{schema}.{table}".ToLower())) continue;

                currentTable++;
                _progressLog.Info($"  [{currentTable}/{filteredCount}] Extracting {table}...");

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

                    JsonHelper.Write(filename, tableObj);
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
        }
        finally
        {
            connectionJson.Close();
        }
    }

    private int PerformMySqlCasting(IDbCommand command, string castType)
    {
        var count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var castPath = Path.Combine(_templatePath, reader["Folder"].ToString());
            DirectoryWrapper.GetFromFactory().CreateDirectory(castPath);
            var fullName = reader["FullName"].ToString();
            if (_objectsToCast.Length > 0 && !_objectsToCast.Contains(fullName.ToLower()) && !_objectsToCast.Contains(Identifier.SplitQualifiedName(fullName, _platform).Name.ToLower())) continue;

            var fileName = ResolveOutputPath(castPath, EncodeFullName(fullName, ".sql"));
            if (ShouldSkipKnownBadScript(fileName)) { count++; continue; }
            var script = string.Join("\r\n", reader["Code"].ToString());
            _progressLog.Info($"  Casting {fullName}");
            FileWrapper.GetFromFactory().WriteAllText(fileName, script);
            var folderName = reader["Folder"].ToString();
            var objectType = ScriptFolderTypeInference.InferFromFolderName(folderName);
            ValidateAndHandleScript(command.Connection, fileName, script, objectType);
            count++;
        }
        return count;
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
                        ? ExtractTableModelXml(commandJson, tableSchema, tableName)
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
       cc.parent_column_id
  FROM sys.check_constraints cc WITH (NOLOCK)
 WHERE cc.parent_object_id = OBJECT_ID('{EscapeSql(tableSchema)}.{EscapeSql(tableName)}')
 ORDER BY cc.name";

                        var allConstraints = new List<CheckConstraint>();
                        using (var ccReader = commandJson.ExecuteReader())
                        {
                            while (ccReader.Read())
                                allConstraints.Add(new CheckConstraint { Name = $"{ccReader["Name"]}", Expression = $"{ccReader["Expression"]}" });
                        }
                        PromoteCheckConstraintsToTableLevel(sqlTable, allConstraints);
                    }

                    var oldTableFile = ResolveOutputPath(tableDir, EncodeObjectFileName(tableSchema, tableObj.OldName.Trim('"'), ".json"));
                    if (FileWrapper.GetFromFactory().Exists(filename) || FileWrapper.GetFromFactory().Exists(oldTableFile))
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
                    JsonHelper.Write(filename, tableObj);
                    _stats.Tables++;
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
    private static string ExtractTableModelXml(IDbCommand command, string tableSchema, string tableName)
    {
        command.CommandText = $"EXEC SchemaSmith.GenerateTableXml @p_Schema = '{EscapeSql(tableSchema)}', @p_Table = '{EscapeSql(tableName)}'";
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
                break;

            case Platform.MySQL:
                LogIfPositive("  Views:      ", _stats.Views);
                LogIfPositive("  Functions:  ", _stats.Functions);
                LogIfPositive("  Procedures: ", _stats.Procedures);
                LogIfPositive("  Triggers:   ", _stats.Triggers);
                LogIfPositive("  Events:     ", _stats.Events);
                LogIfPositive("  Sequences:  ", _stats.Sequences);
                break;
        }

        _progressLog.Info($"  Elapsed:    {_stopwatch.Elapsed.TotalSeconds:F1} seconds");
        _progressLog.Info("");
        _progressLog.Info(_stats.TableErrors > 0
            ? "Casting Completed with Errors"
            : "Casting Completed Successfully");
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
        public int FullTextCatalogs { get; set; }
        public int FullTextStopLists { get; set; }
        public int XmlSchemaCollections { get; set; }
        public int Synonyms { get; set; }
        public int Collations { get; set; }
        public int Publications { get; set; }
    }
}
