// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using log4net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Checkpointing;
using Schema.Delivery;
using Schema.Isolators;
using Schema.Utility;
using Schema.Configuration;

namespace SchemaQuench;

/// <summary>
/// Quenches a single database for a template. Platform-aware: dispatches connection setup,
/// identifier quoting, and SQL commands based on Product.Platform.
/// </summary>
public class DatabaseQuench
{
    public bool QuenchSuccessful { get; private set; }

    /// <summary>
    /// True when <see cref="Execute"/> short-circuited via the benign
    /// <see cref="SchemaPresence.MissingSkipped"/> path — distinguishes a skipped iteration from a
    /// real success for the deployment summary report (#243), since both leave
    /// <see cref="QuenchSuccessful"/> true. Default false.
    /// </summary>
    public bool WasSkipped { get; private set; }

    /// <summary>
    /// Set to a non-null <see cref="FailureRecord"/> when <see cref="Execute"/> fails, so the
    /// dispatching <c>ProductQuench.RunOneWorkUnit</c> can collect this tenant's failure (with its
    /// captured context tail) into the end-of-run roll-up. Null on success.
    /// </summary>
    public FailureRecord LastFailure { get; private set; }

    /// <summary>
    /// Override-origin signal. True when this iteration's schema came from a
    /// <c>TemplateTargets:&lt;Template&gt;:Schemas</c> override. Drives the
    /// skip-missing branch in <see cref="EnsureSchemaExists(System.Data.IDbCommand)"/>:
    /// override-sourced units honor the work-unit-level <see cref="ProvisionSchemaIfMissing"/>
    /// flag, with missing schemas SKIPPED (no error) when that flag is false. Discovery-sourced
    /// units (default <c>false</c>) preserve today's strict <c>Template.CreateSchemaIfMissing</c>
    /// behavior — a missing schema fails the iteration with the three-onboarding-paths message.
    /// </summary>
    public bool SchemaFromOverride { get; init; }

    /// <summary>
    /// Work-unit-level provisioning flag, mirrored from
    /// <c>TemplateTargets.&lt;Template&gt;.CreateIfMissing</c>. When true on an
    /// override-sourced unit, a missing schema is provisioned via
    /// <see cref="SchemaProvisioner.EnsureSchemaExists"/>; when false, a missing schema is
    /// SKIPPED with an info log. On discovery-sourced units this flag is ignored —
    /// <see cref="Template.CreateSchemaIfMissing"/> remains the authority.
    /// </summary>
    public bool ProvisionSchemaIfMissing { get; init; }

    /// <summary>
    /// Shared run-level timing collector, set via the object initializer at the
    /// <c>ProductQuench</c> construction site. Null on any DatabaseQuench built without one
    /// (e.g. existing unit tests) — every timing call site null-guards with <c>RunTiming?.Record</c>
    /// so timing is purely additive instrumentation, never a behavior dependency.
    /// </summary>
    public RunTiming RunTiming { get; init; }

    /// <summary>
    /// Shared run-level migration-script capture, set via the object initializer at the
    /// <c>ProductQuench</c> construction site (#243 Deployment Summary Report, E4b). Null on any
    /// DatabaseQuench built without one (e.g. existing unit tests) — the hook call site
    /// null-guards with <c>MigrationScripts?.Record</c> so capture is purely additive
    /// instrumentation, never a behavior dependency for script execution or checkpoint tracking.
    /// </summary>
    public MigrationScriptCapture MigrationScripts { get; init; }

    /// <summary>
    /// Shared run-level WhatIf capture, set via the object initializer at the
    /// <c>ProductQuench</c> construction site (#243 Deployment Summary Report, E4c). Null on any
    /// DatabaseQuench built without one (e.g. existing unit tests) — the hook call sites in the
    /// <c>WhatIfLog*</c> methods null-guard with <c>WhatIf?.Record</c> so capture is purely
    /// additive instrumentation alongside the existing progress-log entries, never a behavior
    /// dependency.
    /// </summary>
    public WhatIfCapture WhatIf { get; init; }

    /// <summary>
    /// Shared run-level object-change audit capture, set via the object initializer at the
    /// <c>ProductQuench</c> construction site (#243 E5). Null on any DatabaseQuench built without one
    /// (e.g. existing unit tests) — all hook call sites null-guard so capture is purely additive
    /// instrumentation, never a behavior dependency. The 4 table procs write session-scoped rows
    /// that <see cref="ChangeAuditReader"/> drains in <c>Execute()</c>'s finally; object scripts are
    /// recorded here as Action "ran".
    /// </summary>
    public ChangeAuditCapture ChangeAudit { get; init; }

    /// <summary>
    /// No-drop protection tier (#270 Slice E): when true, the ModifiedTableQuench proc records the
    /// tables it would have dropped by absence (but is suppressing) to the ChangeAudit seam as
    /// <c>wouldDrop</c> rows, so the run can surface a PreventDropSummary manifest. Set alongside the
    /// forced-false drop flags in protected mode.
    /// </summary>
    public bool CaptureWouldDrop { get; init; }

    /// <summary>
    /// The upper-tier <see cref="RebuildPolicy"/> for this work unit — environment, product and template
    /// already collapsed to ONE whole policy by <c>ProductQuench.ResolveCascadedPolicy</c>. Forwarded to
    /// each engine's <c>ModifiedTableQuench</c>, which applies it only to tables that declared no policy
    /// of their own (a table that declared one takes ITS policy entire — the resolution is
    /// most-specific-wins on the whole object, never a per-field blend).
    ///
    /// Set through the object initializer, like <see cref="CaptureWouldDrop"/>, rather than as another
    /// positional constructor parameter: the four constructor overloads already carry nineteen of those,
    /// and a null here is exactly what "no tier declared a policy" means — so a DatabaseQuench built
    /// without one (every existing test) behaves as it did and can never elect a rebuild.
    /// </summary>
    public RebuildPolicy CascadedRebuildPolicy { get; init; }

    /// <summary>
    /// Whether an application-time period present on the table but absent from the package is dropped
    /// (MariaDB). Defaults to FALSE unlike every sibling drop flag, because a package that predates
    /// periods -- or was extracted below 11.4, where the catalog cannot report them -- carries none even
    /// when the table has one, and dropping on that absence removes a declaration the package never had
    /// the chance to make.
    /// </summary>
    public bool DropPeriodsRemovedFromProduct { get; init; }

    // #323 opt-in. SQL Server only -- SCHEMABINDING has no equivalent on the other engines, so the
    // parameter is appended to the SQL Server call alone rather than added to every proc signature.
    public bool DropSchemaBoundDependents { get; init; }

    /// <summary>
    /// Drop-by-absence for DECLARED scheduled events. Defaults false, and the default matters more here
    /// than for most drop flags: events were scripted objects that were never removed by absence, so
    /// turning this on by default would start deleting events on the first deploy after upgrading.
    /// </summary>
    public bool DropRemovedEvents { get; init; }

    /// <summary>NEVER when no tier declared a policy — the domain object's own default.</summary>
    /// <summary>
    /// MariaDB only. <c>KEEP</c> opts into altering a system-versioned table; the engine then applies the
    /// DDL to the stored history as well, rewriting it to a shape it never had. Anything else (including
    /// unset) leaves the engine default, which refuses such a change -- so the conservative answer needs
    /// no configuration and the destructive one has to be asked for.
    /// </summary>
    private string SystemVersioningAlterHistory =>
        EscapeSqlLiteral((FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.SystemVersioningAlterHistory]
                          ?? "").Trim().ToUpperInvariant());

    private string RebuildPolicyMode =>
        EscapeSqlLiteral((CascadedRebuildPolicy?.Mode ?? "NEVER").Trim().ToUpperInvariant());

    /// <summary>
    /// The SQL literal NULL when unset, which is what each proc's "no threshold" branch tests for. Never
    /// substituted with a number: a THRESHOLD policy with no Threshold must elect nothing, not rebuild at
    /// some invented count.
    /// </summary>
    private string RebuildPolicyThreshold =>
        CascadedRebuildPolicy?.Threshold?.ToString(CultureInfo.InvariantCulture) ?? "NULL";

    private string RebuildPolicyOnOrderMismatch =>
        FormatBooleanFlag(CascadedRebuildPolicy?.OnOrderMismatch == true);

    private readonly ILog _progressLog = LogFactory.GetLogger("ProgressLog");
    private readonly ILog _errorLog = LogFactory.GetLogger("ErrorLog");

    private readonly string _server;
    private readonly Product _product;
    private readonly Template _template;
    private readonly string _databaseName;
    private readonly string _schemaName;
    private readonly bool _suppressKindling;
    // SQL Server model-ingest encoding for this database, resolved from the detected compatibility level +
    // Target:CompatEncoding during Execute (below the OPENJSON compat cliff, or CompatEncoding=legacy -> Xml).
    // Defaults to Json; stays Json for PostgreSQL/MySQL and when kindling is suppressed (helpers are then
    // presumed already kindled with the JSON encoding against a compat-130+ database).
    private IngestEncoding _ingestEncoding = IngestEncoding.Json;
    // SQL Server: the detected server major version (10=2008 … 16=2022; 0 until detected / non-SQL-Server).
    // Baked into SchemaSmith.fn_ServerMajorVersion at kindle time so the version-gated helpers work on a
    // genuine pre-2016 binary where SESSION_CONTEXT (the former transport) does not exist.
    private int _sqlServerMajorVersion;
    private readonly string _whatIfOnly;
    private readonly bool _runScriptsTwice;
    private readonly string _dropRemovedTables;
    private readonly string _dropRemovedColumns;
    private readonly string _dropRemovedForeignKeys;
    private readonly string _dropRemovedCheckConstraints;
    private readonly string _dropRemovedExcludeConstraints;
    private readonly string _dropRemovedStatistics;
    private readonly string _dropRemovedIndexes;
    private readonly bool _updateTables;
    private readonly bool _deliverData;
    private readonly ICheckpointing _checkpointing;
    private readonly string _dropUnknownIndexes;
    private readonly bool _trackRunOnceMigrations;
    private readonly bool _pruneObsoleteMigrationTracking;
    private readonly bool _forceReKindle;

    private string _debugFileLocation = "";
    private Exception _infoMessageException;
    private StatusMessageMonitor _statusMonitor;
    private readonly object _lockObject = new();

    // Per-tenant failure-capture buffer (lazily built on the first logged line so it binds the
    // stable LogPrefix/template scope). Every tenant log line funnels through the Safe* wrappers,
    // so appending there captures the lead-up context for a failure with engine parity for free.
    private FailureContext _failureContext;
    private FailureContext FailureCtx => _failureContext ??= new FailureContext(
        $"Template:{_template?.Name}", LogPrefix,
        FailureContext.ResolveCapacity(FactoryContainer.Resolve<IConfigurationRoot>()));
    private int _postgreSqlServerVersionNum; // 0 until detected; only meaningful when Platform == PostgreSQL
    private int _mySqlServerVersionNum; // 0 until detected (major*100+minor); only meaningful for MySQL/MariaDb
    private int _sqlServerCompatibilityLevel; // 0 until detected; SQL-Server-only; gates JSON data delivery below 130 (B1 slice 2)
    private string _unsupportedFeaturePolicy; // Target:UnsupportedFeaturePolicy (warn | fail, default warn); general all-engine policy, not just the MySQL data-delivery gate

    // A1: the per-target version script tokens ({{ServerMajorVersion}} / {{CompatibilityLevel}}),
    // built from the detected TargetVersionInfo in Phase B (post-connection) and applied wherever
    // script tokens resolve — folder/component/sentinel ShouldApplyExpression and script bodies. Null
    // until PrepareVersionScriptTokens runs (test-only entry points that bypass Execute).
    private List<KeyValuePair<string, string>> _versionScriptTokens;

    // Per-iteration content built by PrepareIterationContent at the start of Execute(). For schema-
    // template iterations the script collections are cloned (isolating {{SchemaName}}-substituted
    // batches from sibling iterations that share the same in-memory Template) and the table / view
    // JSON carries the substituted schema. For regular templates the collections alias _template's
    // own collections (preserving the cross-iteration HasBeenQuenched semantics the engine relies on)
    // and the schema strings stay null so the accessor properties below fall back to _template.<field>
    // — no substitution, no behavior change. That fall-back also lets test-only entry points that call
    // the Quench* methods directly (bypassing Execute → PrepareIterationContent) keep working.
    private readonly IterationContent _iteration = new();

    // Relative paths of scripts in folders skipped by a ShouldApplyExpression this iteration. A
    // gated-off folder's script is still declared in the package (just not applicable to this
    // target), so its run-once migration tracking row must NOT be pruned as obsolete — mirrors how
    // an active Target filter narrows pruning scope and how a gated-out object isn't dropped.
    private readonly HashSet<string> _gatedOffMigrationPaths = new();

    private sealed class IterationContent
    {
        public List<SqlScript> BeforeScripts { get; set; }
        public List<SqlScript> ObjectScripts { get; set; }
        public List<SqlScript> AfterTablesObjectScripts { get; set; }
        public List<SqlScript> BetweenTablesAndKeysScripts { get; set; }
        public List<SqlScript> AfterTableScripts { get; set; }
        public List<SqlScript> TableDataScripts { get; set; }
        public List<SqlScript> AfterScripts { get; set; }
        public string BaselineValidationScript { get; set; }
        public string VersionStampScript { get; set; }
        public string TableSchema { get; set; }
        public string MaterializedViewSchema { get; set; }
        public string IndexedViewSchema { get; set; }
        public string EventSchema { get; set; }
        public string DomainTypeSchema { get; set; }
        public string EnumTypeSchema { get; set; }
        public string SequenceSchema { get; set; }
    }
    // Visible for testing — per-iteration slot scripts after folder gating.
    internal List<SqlScript> IterationBeforeScripts => _iteration.BeforeScripts;
    internal List<SqlScript> IterationObjectScripts => _iteration.ObjectScripts;
    internal List<SqlScript> IterationAfterTablesObjectScripts => _iteration.AfterTablesObjectScripts;

    /// <summary>
    /// Evaluates each template folder's <c>ShouldApplyExpression</c> against this iteration's target
    /// and drops the scripts of any folder whose expression is false from the iteration's slot lists.
    /// No-op (and no queries) when no folder carries an expression, so the common case is unchanged
    /// bit-for-bit. Evaluation errors propagate — a broken gate fails the iteration rather than
    /// silently skipping a folder.
    /// </summary>
    internal void ApplyFolderGates(IDbCommand command)
    {
        var gated = _template.ScriptFolders.Where(f => !string.IsNullOrWhiteSpace(f.ShouldApplyExpression)).ToList();
        if (gated.Count == 0) return;

        var skip = new HashSet<TemplateFolder>();
        foreach (var folder in gated)
        {
            var expression = ResolveFolderGateExpression(folder.ShouldApplyExpression);
            try
            {
                if (FolderGate.ShouldApply(command, expression)) continue;
            }
            catch (Exception e)
            {
                var message = $"Folder '{folder.FolderPath}' ShouldApplyExpression failed on {DbScope}: {e.Message}";
                SafeProgressLog($"  {message}");
                _errorLog.Error(message, e);
                throw;
            }

            skip.Add(folder);
            foreach (var script in folder.Scripts)
                _gatedOffMigrationPaths.Add(GetRelativeScriptPath(script.LogPath));
            SafeProgressLog($"  Skipping folder '{folder.FolderPath}' on {DbScope} — ShouldApplyExpression evaluated false");
        }

        if (skip.Count > 0) RebuildIterationScripts(skip);
    }

    /// <summary>
    /// A tracking row is obsolete when its script is no longer among the current slot's scripts AND
    /// it was not skipped by a folder gate this iteration. Gated-off scripts are still declared in
    /// the package, so their run-once tracking rows are protected from pruning.
    /// </summary>
    internal bool IsObsoleteTrackingEntry(string trackedRelativePath, List<SqlScript> currentScripts) =>
        currentScripts.All(s => GetRelativeScriptPath(s.LogPath) != trackedRelativePath)
        && !_gatedOffMigrationPaths.Contains(trackedRelativePath);

    private string ResolveFolderGateExpression(string expression)
    {
        // Version tokens apply to ALL templates (regular + schema) — gating a folder on the target
        // version is the primary use case; {{SchemaName}} only exists on schema-template iterations.
        // TokenHelper.AssembleGateTokens is the single source of gate vocabulary — DataDeliveryProcessor's
        // gate resolver builds its token list the same way (N2).
        var tokens = TokenHelper.AssembleGateTokens(_schemaName, _versionScriptTokens);
        return tokens.Count == 0 ? expression : SqlScript.TokenReplace(expression, tokens, _product.Platform);
    }

    private void RebuildIterationScripts(HashSet<TemplateFolder> skip)
    {
        bool Survives(TemplateFolder f) => !skip.Contains(f);
        List<SqlScript> Scripts(IEnumerable<TemplateFolder> folders) => folders.Where(Survives).SelectMany(f => f.Scripts).ToList();

        if (string.IsNullOrEmpty(_schemaName))
        {
            // Regular template: rebuild as freshly-allocated lists that still hold the SAME SqlScript
            // references (filter, never clone). List identity is not preserved — but it never was, since
            // the _template.*Scripts accessors allocate a new list per call too — and reference identity
            // IS, so the cross-iteration HasBeenQuenched dedup the engine relies on keeps working.
            _iteration.BeforeScripts = Scripts(_template.BeforeFolders);
            _iteration.ObjectScripts = Scripts(_template.ObjectFolders);
            _iteration.AfterTablesObjectScripts = Scripts(_template.AfterTablesObjectFolders);
            _iteration.BetweenTablesAndKeysScripts = Scripts(_template.BetweenTablesAndKeysFolders);
            _iteration.AfterTableScripts = Scripts(_template.AfterTableFolders);
            _iteration.TableDataScripts = Scripts(_template.TableDataFolders);
            _iteration.AfterScripts = Scripts(_template.AfterFolders);
            return;
        }

        var schemaNameTokens = new List<KeyValuePair<string, string>> { new("SchemaName", _schemaName) };
        _iteration.BeforeScripts = CloneAndSubstitute(Scripts(_template.BeforeFolders), schemaNameTokens);
        _iteration.ObjectScripts = CloneAndSubstitute(Scripts(_template.ObjectFolders), schemaNameTokens);
        _iteration.AfterTablesObjectScripts = CloneAndSubstitute(Scripts(_template.AfterTablesObjectFolders), schemaNameTokens);
        _iteration.BetweenTablesAndKeysScripts = CloneAndSubstitute(Scripts(_template.BetweenTablesAndKeysFolders), schemaNameTokens);
        _iteration.AfterTableScripts = CloneAndSubstitute(Scripts(_template.AfterTableFolders), schemaNameTokens);
        _iteration.TableDataScripts = CloneAndSubstitute(Scripts(_template.TableDataFolders), schemaNameTokens);
        _iteration.AfterScripts = CloneAndSubstitute(Scripts(_template.AfterFolders), schemaNameTokens);
    }

    internal string IterationTableSchema => _iteration.TableSchema ?? _template.TableSchema ?? "";
    internal string IterationMaterializedViewSchema => _iteration.MaterializedViewSchema ?? _template.MaterializedViewSchema ?? "";

    internal string IterationEventSchema => _iteration.EventSchema ?? _template.EventSchema ?? "";

    internal string IterationDomainTypeSchema => _iteration.DomainTypeSchema ?? _template.DomainTypeSchema ?? "";

    internal string IterationEnumTypeSchema => _iteration.EnumTypeSchema ?? _template.EnumTypeSchema ?? "";

    internal string IterationSequenceSchema => _iteration.SequenceSchema ?? _template.SequenceSchema ?? "";
    // I10: Mirror the iteration-schema pattern for indexed views. QuenchIndexedViews used to
    // rebuild the JSON inline per call; routing through this field puts the substitution alongside
    // the table / materialized-view substitution in PrepareIterationContent. Per-call ShouldApply
    // filtering still happens inside QuenchIndexedViews (the filter is per-view and can't be done
    // once at iteration-prepare time without losing the filter on regular templates that bypass
    // PrepareIterationContent through the constructor → QuenchIndexedViews test entry points).
    internal string IterationIndexedViewSchema => _iteration.IndexedViewSchema ?? _template.IndexedViewSchema ?? "";

    // XML transports for the legacy (Xml) ingest encoding: convert the iteration's JSON model array to the
    // ingest XML the below-cliff parse/quench procs shred. An empty/absent schema maps to an empty array so
    // ToIngestXml produces a well-formed empty root (mirrors the JSON path tolerating an empty @TableDefinitions).
    private string IterationTableXml =>
        ModelXmlSerializer.ToIngestXml(string.IsNullOrWhiteSpace(IterationTableSchema) ? "[]" : IterationTableSchema, "Tables", "Table");
    private string IterationIndexedViewXml =>
        ModelXmlSerializer.ToIngestXml(string.IsNullOrWhiteSpace(IterationIndexedViewSchema) ? "[]" : IterationIndexedViewSchema, "IndexedViews", "IndexedView");

    public DatabaseQuench(string server, Product product, Template template, string databaseName,
        string schemaName, bool suppressKindling, string whatIfOnly, bool runScriptsTwice, string dropRemovedTables,
        string dropRemovedColumns, string dropRemovedForeignKeys, string dropRemovedCheckConstraints, string dropRemovedExcludeConstraints, string dropRemovedStatistics, string dropRemovedIndexes, bool dropUnknownIndexes, bool updateTables, bool deliverData, ICheckpointing checkpointing,
        bool trackRunOnceMigrations = true, bool pruneObsoleteMigrationTracking = true, bool forceReKindle = false)
    {
        _server = server;
        _product = product;
        _template = template;
        _databaseName = databaseName;
        _schemaName = schemaName ?? "";
        _suppressKindling = suppressKindling;
        _whatIfOnly = whatIfOnly;
        _runScriptsTwice = runScriptsTwice;
        _dropRemovedTables = dropRemovedTables;
        _dropRemovedColumns = dropRemovedColumns;
        _dropRemovedForeignKeys = dropRemovedForeignKeys;
        _dropRemovedCheckConstraints = dropRemovedCheckConstraints;
        _dropRemovedExcludeConstraints = dropRemovedExcludeConstraints;
        _dropRemovedStatistics = dropRemovedStatistics;
        _dropRemovedIndexes = dropRemovedIndexes;
        _updateTables = updateTables;
        _deliverData = deliverData;
        _checkpointing = checkpointing;
        _dropUnknownIndexes = FormatBooleanFlag(dropUnknownIndexes);
        _trackRunOnceMigrations = trackRunOnceMigrations;
        _pruneObsoleteMigrationTracking = pruneObsoleteMigrationTracking;
        _forceReKindle = forceReKindle;
    }

    // Convenience overload matching the pre-schema-templates positional signature so existing
    // callers (mostly tests) that have no schema concept compile without modification. Forwards
    // to the canonical constructor with empty schemaName.
    public DatabaseQuench(string server, Product product, Template template, string databaseName,
        bool suppressKindling, string whatIfOnly, bool runScriptsTwice, string dropRemovedTables,
        string dropRemovedColumns, string dropRemovedForeignKeys, string dropRemovedCheckConstraints, string dropRemovedExcludeConstraints, string dropRemovedStatistics, string dropRemovedIndexes, bool dropUnknownIndexes, bool updateTables, bool deliverData, ICheckpointing checkpointing,
        bool trackRunOnceMigrations = true, bool pruneObsoleteMigrationTracking = true, bool forceReKindle = false)
        : this(server, product, template, databaseName, "", suppressKindling, whatIfOnly, runScriptsTwice,
            dropRemovedTables, dropRemovedColumns, dropRemovedForeignKeys, dropRemovedCheckConstraints, dropRemovedExcludeConstraints, dropRemovedStatistics, dropRemovedIndexes, dropUnknownIndexes, updateTables, deliverData, checkpointing,
            trackRunOnceMigrations, pruneObsoleteMigrationTracking, forceReKindle)
    {
    }

    // Internal constructor for testing — allows direct injection of all parameters
    internal DatabaseQuench(string server, Product product, Template template, string databaseName,
        string schemaName, bool suppressKindling, string whatIfOnly, bool runScriptsTwice, string dropRemovedTables,
        string dropRemovedColumns, string dropRemovedForeignKeys, string dropRemovedCheckConstraints, string dropRemovedExcludeConstraints, string dropRemovedStatistics, string dropRemovedIndexes, string dropUnknownIndexes, bool updateTables, bool deliverData, ICheckpointing checkpointing,
        bool trackRunOnceMigrations = true, bool pruneObsoleteMigrationTracking = true, bool forceReKindle = false)
    {
        _server = server;
        _product = product;
        _template = template;
        _databaseName = databaseName;
        _schemaName = schemaName ?? "";
        _suppressKindling = suppressKindling;
        _whatIfOnly = whatIfOnly;
        _runScriptsTwice = runScriptsTwice;
        _dropRemovedTables = dropRemovedTables;
        _dropRemovedColumns = dropRemovedColumns;
        _dropRemovedForeignKeys = dropRemovedForeignKeys;
        _dropRemovedCheckConstraints = dropRemovedCheckConstraints;
        _dropRemovedExcludeConstraints = dropRemovedExcludeConstraints;
        _dropRemovedStatistics = dropRemovedStatistics;
        _dropRemovedIndexes = dropRemovedIndexes;
        _dropUnknownIndexes = dropUnknownIndexes;
        _updateTables = updateTables;
        _deliverData = deliverData;
        _checkpointing = checkpointing;
        _trackRunOnceMigrations = trackRunOnceMigrations;
        _pruneObsoleteMigrationTracking = pruneObsoleteMigrationTracking;
        _forceReKindle = forceReKindle;
    }

    // Convenience overload for tests pre-dating the schemaName parameter.
    internal DatabaseQuench(string server, Product product, Template template, string databaseName,
        bool suppressKindling, string whatIfOnly, bool runScriptsTwice, string dropRemovedTables,
        string dropRemovedColumns, string dropRemovedForeignKeys, string dropRemovedCheckConstraints, string dropRemovedExcludeConstraints, string dropRemovedStatistics, string dropRemovedIndexes, string dropUnknownIndexes, bool updateTables, bool deliverData, ICheckpointing checkpointing,
        bool trackRunOnceMigrations = true, bool pruneObsoleteMigrationTracking = true, bool forceReKindle = false)
        : this(server, product, template, databaseName, "", suppressKindling, whatIfOnly, runScriptsTwice,
            dropRemovedTables, dropRemovedColumns, dropRemovedForeignKeys, dropRemovedCheckConstraints, dropRemovedExcludeConstraints, dropRemovedStatistics, dropRemovedIndexes, dropUnknownIndexes, updateTables, deliverData, checkpointing,
            trackRunOnceMigrations, pruneObsoleteMigrationTracking, forceReKindle)
    {
    }

    internal Platform Platform => _product.Platform;
    internal string ProductName => _product.Name;

    /// <summary>
    /// The iteration schema for this database quench. Empty string for regular (non-schema) templates,
    /// which surfaces through <see cref="TrackingScope.SchemaName"/> consistently with the persisted
    /// tracking-table convention (slice 2). Exposed for tests; production code reads through DbScope.
    /// </summary>
    internal string SchemaName => _schemaName ?? "";

    private TrackingScope DbScope => new TrackingScope
    {
        ProductName = _product.Name,
        TemplateName = _template.Name,
        Server = _server,
        DatabaseName = _databaseName,
        SchemaName = _schemaName ?? ""
    };

    public void Execute()
    {
        SafeProgressLog("Begin Quench");

        var checkpointSummary = _checkpointing?.GetDatabaseCheckpointSummary(DbScope) ?? DatabaseCheckpointSummary.Empty;
        if (checkpointSummary.HasAnyCompleted)
            SafeProgressLog($"  [{_databaseName}] Resuming from checkpoint (Completed Steps: {checkpointSummary.CompletedSteps}, Completed Scripts: {checkpointSummary.TotalCompletedScripts})");

        // Schema templates clone their script collections per iteration so {{SchemaName}}-substituted
        // batches don't pollute sibling iterations of the same template that share the in-memory
        // Template instance. Regular templates point straight at _template's collections (no clone) —
        // preserves today's behavior bit-for-bit on the regular-template path.
        PrepareIterationContent();

        try
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 0;

            // SkipIfReadOnly: the target still resolved and counted toward RequireAtLeastOneTarget,
            // so the template validates normally — it just does not apply here. A read-only target
            // (Availability Group secondary, hot standby, replica) cannot take DDL, and skipping is
            // the intended outcome rather than a failure. Checked before the extra connections are
            // opened so a skipped unit costs one connection, not four.
            if (_template.SkipIfReadOnly && ReadOnlyTargetDetector.IsReadOnly(command, _product.Platform))
            {
                _progressLog.Info($"[{_server}].[{_databaseName}] is read-only; skipping template '{_template.Name}' (SkipIfReadOnly)");
                QuenchSuccessful = true;
                WasSkipped = true;
                return;
            }

            // SQL Server and PostgreSQL use multiple connections for parallel operations
            IDbConnection tableConnection = null;
            IDbCommand tableCommand = null;
            IDbConnection objectsConnection = null;
            IDbCommand objectsCommand = null;
            IDbConnection silentConnection = null;
            IDbCommand silentCommand = null;

            if (_product.Platform.GetBasePlatform() != Platform.MySQL)
            {
                tableConnection = GetConnection();
                tableCommand = tableConnection.CreateCommand();
                tableCommand.CommandTimeout = 0;
                objectsConnection = GetConnection(fireInfoMessageEventOnUserErrors: false);
                objectsCommand = objectsConnection.CreateCommand();
                objectsCommand.CommandTimeout = 0;
                silentConnection = GetConnection(ignoreInfoMessages: true);
                silentCommand = silentConnection.CreateCommand();
                silentCommand.CommandTimeout = 0;
            }

            try
            {
                // Schema-template existence check: run before slot 1 (kindling) so a missing
                // schema fails fast with a clear error before any DDL is attempted. MySQL schema
                // templates are rejected at load time (no namespace-inside-database concept);
                // the empty-schemaName guard handles MySQL and regular templates both.
                // Skip-missing short-circuit: when an override-sourced iteration's schema is
                // missing and CreateIfMissing is false, EnsureSchemaExists logs the skip and
                // returns MissingSkipped. Mark the unit successful and bail — the dispatcher
                // records a benign skip rather than running deployment work against a
                // non-existent target.
                if (EnsureSchemaExists(command) == SchemaPresence.MissingSkipped)
                {
                    QuenchSuccessful = true;
                    WasSkipped = true;
                    return;
                }

                var effectiveTableCmd = tableCommand ?? command;
                var effectiveObjectsCmd = objectsCommand ?? command;
                var effectiveSilentCmd = silentCommand ?? command;

                // Detect the target version ONCE, up front (the connection is open) and BEFORE folder gates,
                // since a folder ShouldApplyExpression may reference the version tokens (A1). One unified
                // detection for all engines: ServerComparable is set on all four; CompatibilityLevel is
                // SQL-Server-only. For SQL Server this also enforces the compat floor and selects the model-
                // ingest encoding (compat >= 130 -> JSON/OPENJSON, below -> XML) and bakes the major version
                // into SchemaSmith.fn_ServerMajorVersion at kindle time.
                using (var versionCmd = connection.CreateCommand())
                {
                    var versionInfo = TargetVersionDetector.Detect(versionCmd, _product.Platform, _databaseName);
                    // Captured for every engine (CompatibilityLevel is null => 0 on non-SQL-Server). SQL Server's
                    // JSON data delivery gates on compat < 130 (B1 slice 2); the policy governs that and the
                    // MySQL < 8.0 data-delivery gate. Resolved up front so both the deliver and WhatIf contexts
                    // below see them regardless of the per-engine switch.
                    _sqlServerCompatibilityLevel = versionInfo.CompatibilityLevel ?? 0;
                    _unsupportedFeaturePolicy = FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.UnsupportedFeaturePolicy];
                    switch (_product.Platform.GetBasePlatform())
                    {
                        case Platform.PostgreSQL:
                            _postgreSqlServerVersionNum = versionInfo.ServerComparable;
                            break;
                        // MySQL/MariaDb: the version drives the data-delivery version-adaptive JSON-array shred
                        // (JSON_TABLE on MySQL 8.0+/MariaDB 10.6+, a recursive CTE on MariaDB 10.2-10.5, gated
                        // manual scripts below MySQL 8.0); the policy governs the below-floor gate.
                        case Platform.MySQL:
                            _mySqlServerVersionNum = versionInfo.ServerComparable;
                            break;
                        case Platform.SqlServer when !_suppressKindling:
                            SafeProgressLog($"  [{_databaseName}] detected SQL Server version {VersionHelper.DisplayVersion(versionInfo)}" +
                                            (versionInfo.CompatibilityLevel is { } lvl ? $" (compatibility level {lvl})" : ""));
                            PreFlightVersionGuard.CheckOrThrow(versionInfo, _server, _databaseName);
                            _sqlServerMajorVersion = versionInfo.ServerComparable;
                            var compatEncodingOverride = FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.CompatEncoding];
                            _ingestEncoding = CompatEncoding.Select(compatEncodingOverride, versionInfo.CompatibilityLevel, versionInfo.ServerComparable);
                            break;
                    }

                    // A1: expose the detected version as {{ServerMajorVersion}} / {{CompatibilityLevel}} script
                    // tokens. Built BEFORE folder gating (folder ShouldApplyExpressions may reference them);
                    // applied to script bodies + model payloads AFTER (a gated-off folder rebuilds the slot lists).
                    PrepareVersionScriptTokens(versionInfo.ServerComparable, versionInfo.CompatibilityLevel);
                }

                // Folder-level ShouldApplyExpression (#260): drop gated-off folders' scripts from
                // this iteration before any slot runs. Evaluated per target; read-only under WhatIf.
                ApplyFolderGates(command);
                ApplyVersionScriptTokens();

                // Step: Kindle the forge
                // Intentionally NOT wrapped in `_checkpointing.Track` — mirrors the
                // MissingTablesAndColumns un-wrap below. KindleForge is cheap and
                // self-verifying (ForgeKindler compares the in-DB KindleStamp and no-ops when
                // current), so it must always run. A checkpoint-skip here would mean a target
                // database reset out-of-band (helpers/stamp dropped) but resumed against a
                // stale checkpoint never gets its helpers reinstalled, surfacing later as
                // "SchemaSmith_ParseTableJson does not exist" instead of a clear kindle.
                if (!_suppressKindling)
                {
                    SafeProgressLog("  Kindling the forge");
                    // SQL Server bakes the detected server version + resolved unsupported-feature policy into the
                    // helper functions at kindle time (dropping the 2016+ SESSION_CONTEXT transport). Both are
                    // no-ops for PostgreSQL/MySQL (their scripts carry neither token; PG uses a runtime GUC).
                    var kindlePolicy = string.Equals(FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.UnsupportedFeaturePolicy],
                        "fail", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
                    ForgeKindler.KindleTheForge(effectiveSilentCmd, _product.Platform, _forceReKindle, _ingestEncoding,
                        _sqlServerMajorVersion, kindlePolicy);
                }

                // Step: Validate baseline. Resolved against per-iteration tokens (BaselineValidationScript
                // may reference {{SchemaName}} for schema templates).
                if (!string.IsNullOrWhiteSpace(_iteration.BaselineValidationScript))
                {
                    var validateBaselineSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "ValidateBaseline", () =>
                    {
                        SafeProgressLog("  Validate Baseline");
                        command.CommandText = _iteration.BaselineValidationScript;
                        try
                        {
                            if (!Convert.ToBoolean(command.ExecuteScalar()))
                                throw new Exception("Invalid baseline for this release");
                        }
                        catch (Exception ex)
                        {
                            WriteValidationFailureArtifact(command, "BaselineValidation", ex);
                            throw;
                        }
                    });
                    validateBaselineSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "ValidateBaseline", validateBaselineSw.ElapsedMilliseconds, 0);
                }

                // Step: Object scripts without unresolved tokens
                var nonTokenScripts = _iteration.ObjectScripts.Where(s => s.Batches.All(b => !b.Contains("{{") && !b.Contains("}}"))).ToList();
                if (!IsWhatIf)
                {
                    SafeProgressLog("  Quenching object scripts without unresolved tokens");
                    QuenchDatabaseObjectsWithCheckpoint(effectiveObjectsCmd, nonTokenScripts, false, DatabaseScriptSlot.Object);
                }
                else
                {
                    SafeProgressLog("  [WhatIf] Object scripts without unresolved tokens:");
                    WhatIfLogScripts(nonTokenScripts, DatabaseScriptSlot.Object);
                }

                // Step: Domain types (PostgreSQL only). Before tables for the same reason enum types are:
                // a column can be OF a domain, so the domain has to exist before the table that uses it.
                if (_product.Platform == Platform.PostgreSQL && _template.DomainTypes.Count > 0)
                {
                    var domainTypeSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "DomainTypeQuench", () => QuenchDomainTypes(command));
                    domainTypeSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "DomainTypeQuench", domainTypeSw.ElapsedMilliseconds, 0);
                }

                // Step: Enum types (PostgreSQL only). BEFORE tables, deliberately: a column can be OF an
                // enum type, so the type has to exist before the table that uses it -- and a value the
                // package adds has to be present before a default or check constraint can reference it.
                if (_product.Platform == Platform.PostgreSQL && _template.EnumTypes.Count > 0)
                {
                    var enumTypeSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "EnumTypeQuench", () => QuenchEnumTypes(command));
                    enumTypeSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "EnumTypeQuench", enumTypeSw.ElapsedMilliseconds, 0);
                }

                // Step: Sequences (PostgreSQL only). Also before tables -- a column DEFAULT can call
                // nextval() on one.
                if (_product.Platform == Platform.PostgreSQL && _template.Sequences.Count > 0)
                {
                    var sequenceSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "SequenceQuench", () => QuenchSequences(command));
                    sequenceSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "SequenceQuench", sequenceSw.ElapsedMilliseconds, 0);
                }

                // Step: Missing tables and columns
                // Intentionally NOT wrapped in `_checkpointing.Track` — this step parses the
                // table JSON into session-scoped temp tables (`#Tables` on SQL Server,
                // `temp_tables` on PG, `_SchemaSmith_Tables` on MySQL) before delegating to
                // `MissingTableAndColumnQuench`. The temp tables are consumed by the next two
                // tracked steps (`ModifiedTables`, `IndexesAndConstraints`); they DON'T survive
                // across connections, so on resume the next session has no temp state.
                // The action itself is database-idempotent (the engine procs add missing
                // tables/columns and no-op when they already exist), so always-running it on
                // resume is safe and cheap. Skip-on-resume would fire `Invalid object name
                // '#Tables'` (SQL Server) / `relation "temp_tables" does not exist` (PG) on
                // the next tracked step. MySQL's `QuenchModifiedTables` and
                // `QuenchIndexesAndConstraints` already defend against the missing-temp-tables
                // case via `MySqlTempTablesExist` + `ParseMySqlTableJson` re-parse; lifting the
                // Track wrapper here is the equivalent defense for SQL Server / PG.
                if (!_template.IndexOnlyTableQuenches && _updateTables)
                {
                    QuenchMissingTablesAndColumns(effectiveTableCmd);
                }

                if (!IsWhatIf)
                {
                    SafeProgressLog("  Quenching object scripts without query tokens");
                    QuenchDatabaseObjectsWithCheckpoint(effectiveObjectsCmd,
                        _iteration.ObjectScripts.Where(s => s.Batches.All(b => !b.Contains("{{") && !b.Contains("}}"))).ToList(),
                        false, DatabaseScriptSlot.Object);

                    ResolveAndApplyQueryTokens(effectiveSilentCmd);

                    SafeProgressLog("  Quenching before database scripts");
                    QuenchTemplateScriptsWithCheckpoint(command, "Before", _iteration.BeforeScripts, DatabaseScriptSlot.Before);
                }
                else
                {
                    SafeProgressLog("  [WhatIf] Object scripts without query tokens:");
                    WhatIfLogScripts(_iteration.ObjectScripts.Where(s => s.Batches.All(b => !b.Contains("{{") && !b.Contains("}}"))).ToList(), DatabaseScriptSlot.Object);

                    if (_template.QueryTokens.Count > 0)
                        SafeProgressLog("  [WhatIf] Would resolve template query tokens");

                    SafeProgressLog("  [WhatIf] Before database scripts:");
                    WhatIfLogTemplateScripts(command, "Before", _iteration.BeforeScripts, DatabaseScriptSlot.Before);
                }

                // Step: Modified tables
                if (!_template.IndexOnlyTableQuenches && _updateTables)
                {
                    var modifiedTablesSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "ModifiedTables", () => QuenchModifiedTables(effectiveTableCmd));
                    modifiedTablesSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "ModifiedTables", modifiedTablesSw.ElapsedMilliseconds, 0);
                }

                if (!IsWhatIf)
                {
                    SafeProgressLog("  Quenching object scripts");
                    QuenchDatabaseObjectsWithCheckpoint(effectiveObjectsCmd, _iteration.AfterTablesObjectScripts, false, DatabaseScriptSlot.AfterTablesObject);

                    SafeProgressLog("  Quenching between table and keys scripts");
                    QuenchTemplateScriptsWithCheckpoint(command, "Between Table And Keys", _iteration.BetweenTablesAndKeysScripts, DatabaseScriptSlot.BetweenTablesAndKeys);
                }
                else
                {
                    SafeProgressLog("  [WhatIf] Object scripts (after tables):");
                    WhatIfLogScripts(_iteration.AfterTablesObjectScripts, DatabaseScriptSlot.AfterTablesObject);

                    SafeProgressLog("  [WhatIf] Between table and keys scripts:");
                    WhatIfLogTemplateScripts(command, "Between Table And Keys", _iteration.BetweenTablesAndKeysScripts, DatabaseScriptSlot.BetweenTablesAndKeys);
                }

                // Step: Indexes and constraints
                if (_updateTables)
                {
                    var indexesAndConstraintsSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "IndexesAndConstraints", () => QuenchIndexesAndConstraints(effectiveTableCmd));
                    indexesAndConstraintsSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "IndexesAndConstraints", indexesAndConstraintsSw.ElapsedMilliseconds, 0);
                }

                // MySQL: cleanup temp tables after index quench
                if (_product.Platform.GetBasePlatform() == Platform.MySQL)
                    CleanupMySqlTempTables(command);

                if (!IsWhatIf)
                {
                    SafeProgressLog("  Quenching after table scripts");
                    QuenchTemplateScriptsWithCheckpoint(command, "After Table", _iteration.AfterTableScripts, DatabaseScriptSlot.AfterTable);

                    if (_iteration.ObjectScripts.Union(_iteration.AfterTablesObjectScripts).Any(s => !s.HasBeenQuenched))
                    {
                        SafeProgressLog("  Quenching object scripts");
                        QuenchDatabaseObjectsWithCheckpoint(effectiveObjectsCmd, _iteration.AfterTablesObjectScripts.ToList(), true, DatabaseScriptSlot.AfterTablesObject);
                    }

                    if (_deliverData)
                    {
                        // Data delivery — Pro determines behavior based on license.
                        var tableDataDeliverySw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "TableDataDelivery", () =>
                        {
                            // Register platform-specific script helper if not already registered
                            if (FactoryContainer.Resolve<IMergeScriptHelper>() is not MergeScriptHelperAdapter adapter || adapter.Platform != _product.Platform)
                                FactoryContainer.Register<IMergeScriptHelper>(new MergeScriptHelperAdapter(_product.Platform));

                            DataDeliveryProcessor.GetFromFactory().DeliverTables(new DataDeliveryContext
                            {
                                Tables = _template.Tables.Cast<IDeliverableTable>().ToList(),
                                Command = effectiveSilentCmd,
                                // MariaDb is delivered as its MySQL base — the delivery subsystem dispatches on
                                // this platform string and has no MariaDb-specific behavior (the enum-typed
                                // ScriptHelper still carries the true platform for SQL generation).
                                Platform = _product.Platform.GetBasePlatform().ToString(),
                                DatabaseName = _databaseName,
                                SchemaName = _schemaName,
                                // N2: same vocabulary as the folder gate's ResolveFolderGateExpression, so a
                                // DataDelivery.ShouldApplyExpression can gate on {{CompatibilityLevel}} /
                                // {{ServerMajorVersion}} instead of only {{SchemaName}}.
                                VersionTokens = _versionScriptTokens,
                                TemplateRootPath = Path.GetDirectoryName(_template.FilePath) ?? "",
                                ScriptHelper = FactoryContainer.Resolve<IMergeScriptHelper>(),
                                ReadFileContent = path => ProductFileWrapper.GetFromFactory().ReadAllText(path),
                                ExecuteScript = (name, script) => { effectiveSilentCmd.CommandText = script; effectiveSilentCmd.ExecuteNonQuery(); },
                                ProgressLog = SafeProgressLog,
                                ProgressLogError = SafeProgressLogError,
                                WhatIf = IsWhatIf,
                                PostgreSqlServerVersionNum = _postgreSqlServerVersionNum,
                                MySqlServerVersionNum = _mySqlServerVersionNum,
                                SqlServerCompatibilityLevel = _sqlServerCompatibilityLevel,
                                UnsupportedFeaturePolicy = _unsupportedFeaturePolicy,
                                WriteResolvedSqlArtifact = (label, sql) =>
                                {
                                    try
                                    {
                                        var path = ResolvedSqlArtifactWriter.WriteFailureArtifact(
                                            ResolveArtifactDirectory(), ScrubArtifactsEnabled, SensitiveTokenValues(),
                                            $"Failed data delivery: {_server}.{_databaseName}" +
                                            $"{(string.IsNullOrEmpty(_schemaName) ? "" : $" [Schema: {_schemaName}]")} [{label}]",
                                            new List<string> { sql }, failingBatchIndex: 0, label,
                                            safeLabel => GetDebugFileName($"Failed DataDelivery {safeLabel}"));
                                        SafeProgressLogError($"    Resolved SQL written to: {path}");
                                    }
                                    catch (Exception artifactEx)
                                    {
                                        SafeProgressLog($"    Could not write resolved-SQL artifact for data delivery '{label}': {artifactEx.Message}");
                                    }
                                }
                            });
                        });
                        tableDataDeliverySw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "TableDataDelivery", tableDataDeliverySw.ElapsedMilliseconds, 0);

                        if (_iteration.ObjectScripts.Union(_iteration.TableDataScripts).Any(s => !s.HasBeenQuenched))
                        {
                            SafeProgressLog("  Quenching table data scripts");
                            QuenchDatabaseObjectsWithCheckpoint(effectiveObjectsCmd, _iteration.TableDataScripts.ToList(), true, DatabaseScriptSlot.TableData);
                        }
                    }

                    // Foreign keys after data delivery (all platforms)
                    if (!_template.IndexOnlyTableQuenches && _updateTables)
                    {
                        var foreignKeysSw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "ForeignKeys", () =>
                        {
                            QuenchForeignKeys(effectiveTableCmd);
                            if (_product.Platform.GetBasePlatform() == Platform.MySQL)
                                CleanupMySqlTempTables(command);
                        });
                        foreignKeysSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "ForeignKeys", foreignKeysSw.ElapsedMilliseconds, 0);
                    }

                    // Step: Materialized views (PostgreSQL only)
                    if (_product.Platform == Platform.PostgreSQL && _template.MaterializedViews.Count > 0)
                    {
                        var materializedViewQuenchSw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "MaterializedViewQuench", () => QuenchMaterializedViews(effectiveTableCmd));
                        materializedViewQuenchSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "MaterializedViewQuench", materializedViewQuenchSw.ElapsedMilliseconds, 0);
                    }

                    // Step: Scheduled events (MySQL/MariaDB only). Runs after tables so an event whose
                    // body references a table the same deploy creates does not fail on first run.
                    if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Events.Count > 0)
                    {
                        var eventQuenchSw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "EventQuench", () => QuenchEvents(effectiveTableCmd));
                        eventQuenchSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "EventQuench", eventQuenchSw.ElapsedMilliseconds, 0);
                    }

                    // Step: Indexed views (SQL Server only)
                    if (_product.Platform == Platform.SqlServer && _template.IndexedViews.Count > 0)
                    {
                        var indexedViewQuenchSw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "IndexedViewQuench", () => QuenchIndexedViews(effectiveTableCmd));
                        indexedViewQuenchSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "IndexedViewQuench", indexedViewQuenchSw.ElapsedMilliseconds, 0);
                    }

                    SafeProgressLog("  Quenching after database scripts");
                    QuenchTemplateScriptsWithCheckpoint(command, "After", _iteration.AfterScripts, DatabaseScriptSlot.After);

                    if (!string.IsNullOrWhiteSpace(_iteration.VersionStampScript))
                    {
                        var versionStampSw = Stopwatch.StartNew();
                        _checkpointing.Track(DbScope, "VersionStamp", () =>
                        {
                            SafeProgressLog("  Stamp version");
                            command.CommandText = _iteration.VersionStampScript;
                            try
                            {
                                ExecuteNonQueryHandlingMessages(command);
                            }
                            catch (Exception ex)
                            {
                                WriteValidationFailureArtifact(command, "VersionStamp", ex);
                                throw;
                            }
                        });
                        versionStampSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "VersionStamp", versionStampSw.ElapsedMilliseconds, 0);
                    }
                }
                else
                {
                    SafeProgressLog("  [WhatIf] After table scripts:");
                    WhatIfLogTemplateScripts(command, "After Table", _iteration.AfterTableScripts, DatabaseScriptSlot.AfterTable);

                    SafeProgressLog("  [WhatIf] Object scripts (final pass):");
                    WhatIfLogScripts(_iteration.AfterTablesObjectScripts.ToList(), DatabaseScriptSlot.AfterTablesObject);

                    if (_deliverData)
                    {
                        SafeProgressLog("  [WhatIf] Table data delivery:");
                        WhatIfLogTableDataScripts(_iteration.TableDataScripts.ToList());

                        if (FactoryContainer.Resolve<IMergeScriptHelper>() is not MergeScriptHelperAdapter whatIfAdapter || whatIfAdapter.Platform != _product.Platform)
                            FactoryContainer.Register<IMergeScriptHelper>(new MergeScriptHelperAdapter(_product.Platform));

                        DataDeliveryProcessor.GetFromFactory().DeliverTables(new DataDeliveryContext
                        {
                            Tables = _template.Tables.Cast<IDeliverableTable>().ToList(),
                            Command = command,
                            Platform = _product.Platform.ToString(),
                            DatabaseName = _databaseName,
                            SchemaName = _schemaName,
                            VersionTokens = _versionScriptTokens,
                            PostgreSqlServerVersionNum = _postgreSqlServerVersionNum,
                            MySqlServerVersionNum = _mySqlServerVersionNum,
                            SqlServerCompatibilityLevel = _sqlServerCompatibilityLevel,
                            UnsupportedFeaturePolicy = _unsupportedFeaturePolicy,
                            TemplateRootPath = Path.GetDirectoryName(_template.FilePath) ?? "",
                            ScriptHelper = FactoryContainer.Resolve<IMergeScriptHelper>(),
                            ReadFileContent = path => ProductFileWrapper.GetFromFactory().ReadAllText(path),
                            ExecuteScript = (_, _) => { },
                            ProgressLog = SafeProgressLog,
                            ProgressLogError = SafeProgressLogError,
                            WhatIf = true
                        });
                    }

                    // WhatIf: Materialized views (PostgreSQL only)
                    if (_product.Platform == Platform.PostgreSQL && _template.MaterializedViews.Count > 0)
                    {
                        SafeProgressLog("  [WhatIf] Would quench materialized views");
                    }

                    // WhatIf: Indexed views (SQL Server only)
                    if (_product.Platform == Platform.SqlServer && _template.IndexedViews.Count > 0)
                    {
                        SafeProgressLog($"  [WhatIf] Would quench {_template.IndexedViews.Count} indexed view(s)");
                    }

                    SafeProgressLog("  [WhatIf] After database scripts:");
                    WhatIfLogTemplateScripts(command, "After", _iteration.AfterScripts, DatabaseScriptSlot.After);

                    if (!string.IsNullOrWhiteSpace(_iteration.VersionStampScript))
                        SafeProgressLog("  [WhatIf] Would stamp version");
                }
            }
            finally
            {
                _statusMonitor?.Dispose();
                _statusMonitor = null;
                DrainChangeAudit(tableCommand ?? command);
                connection.Close();
                tableConnection?.Close();
                objectsConnection?.Close();
                silentConnection?.Close();
            }

            SafeProgressLog("Successfully Quenched");
            QuenchSuccessful = true;
        }
        catch (Exception e)
        {
            // A target-server drop mid-deploy (restart / crash / OOM) surfaces here as a raw
            // provider disconnect (e.g. SocketException) that is indistinguishable from a schema
            // error without classification. Lead with a purpose-built message; the full stack is
            // preserved in the error log. Initial-connect failures are handled earlier by the
            // pre-flight connection test, so at this point a lost connection is a mid-run drop.
            if (ConnectionLostClassifier.IsConnectionLost(e))
            {
                var phase = _template?.Name is { } tn ? $"Template:{tn}" : null;
                var lost = ConnectionLostMessage.Build(_server, phase);
                _errorLog.Error($"Lost connection during {phase ?? "deployment"}", e);
                SafeProgressLogError(lost);
                LastFailure = FailureCtx.ToRecord(lost, null);
                SafeProgressLogError($"*** FAILED [Template:{_template?.Name}] ***");
                return;
            }

            SafeProgressLogError($"FAILED to quench:\r\n{e.Message}");
            if (!string.IsNullOrWhiteSpace(_debugFileLocation))
                SafeProgressLogError($"Resolved SQL written to: {_debugFileLocation}");

            // #338 refinement: for a user-script failure, surface the specific per-script error +
            // artifact on the roll-up's Error:/Debug SQL: lines (parity with mechanical failures)
            // instead of the generic "Unable to quench all scripts" wrapper + n/a.
            if (e is ScriptQuenchException { Failures.Count: > 0 } scriptFailure)
            {
                var first = scriptFailure.Failures[0];
                var extra = scriptFailure.Failures.Count > 1 ? $" (+{scriptFailure.Failures.Count - 1} more)" : "";
                LastFailure = FailureCtx.ToRecord($"Unable to quench '{first.LogPath}': {first.Error}{extra}",
                    string.IsNullOrWhiteSpace(first.ArtifactPath) ? null : first.ArtifactPath);
            }
            else
            {
                LastFailure = FailureCtx.ToRecord(e.Message,
                    string.IsNullOrWhiteSpace(_debugFileLocation) ? null : _debugFileLocation);
            }
            // Terse greppable scope marker; the error text is on the inline "FAILED to quench" line
            // above and in the end-of-run roll-up, so it is not restated here (avoids duplicating
            // error content into the progress stream, which exact-count log assertions rely on).
            SafeProgressLogError($"*** FAILED [Template:{_template?.Name}] ***");
        }
    }

    #region Per-Iteration Content (Schema Templates)

    /// <summary>
    /// Populates the per-iteration script collections and validation-script strings.
    /// <para>
    /// For <b>regular templates</b>: the iteration fields point directly at the
    /// shared <c>_template.&lt;slot&gt;Scripts</c> collections — no clone, no
    /// substitution, behaviour identical to the pre-schema-templates engine.
    /// </para>
    /// <para>
    /// For <b>schema templates</b>: every script collection is deep-cloned so that
    /// the per-iteration <c>{{SchemaName}}</c> substitution applied below cannot
    /// pollute sibling iterations that share the same in-memory <see cref="Template"/>.
    /// <c>{{SchemaName}}</c> is then substituted into every batch of every cloned
    /// script and into the <see cref="Template.BaselineValidationScript"/> /
    /// <see cref="Template.VersionStampScript"/> strings. Iteration-scoped query
    /// tokens are NOT resolved here — they need a live connection and run inside
    /// <see cref="ResolveAndApplyQueryTokens"/>.
    /// </para>
    /// </summary>
    internal void PrepareIterationContent()
    {
        if (string.IsNullOrEmpty(_schemaName))
        {
            // Regular templates intentionally use the existing computed-script accessors (which
            // allocate a fresh List<SqlScript> per access but share the SqlScript references).
            // This preserves today's shared-state behavior across parallel iterations — multiple
            // regular-template DatabaseQuench instances on the same Template observe each other's
            // mutations to SqlScript.HasBeenQuenched, which is how the engine avoids re-running an
            // already-applied script on a sibling DB inside the same template. A future refactor
            // that switches to per-iteration clones for regular templates would silently change
            // observable script-mutation semantics — don't "fix" the aliasing without explicitly
            // re-validating the regular-template parallel-DB scenarios.
            _iteration.BeforeScripts = _template.BeforeScripts;
            _iteration.ObjectScripts = _template.ObjectScripts;
            _iteration.AfterTablesObjectScripts = _template.AfterTablesObjectScripts;
            _iteration.BetweenTablesAndKeysScripts = _template.BetweenTablesAndKeysScripts;
            _iteration.AfterTableScripts = _template.AfterTableScripts;
            _iteration.TableDataScripts = _template.TableDataScripts;
            _iteration.AfterScripts = _template.AfterScripts;
            _iteration.BaselineValidationScript = _template.BaselineValidationScript;
            _iteration.VersionStampScript = _template.VersionStampScript;
            // _iteration.TableSchema / _iteration.MaterializedViewSchema deliberately left null —
            // the IterationTableSchema / IterationMaterializedViewSchema properties fall back to
            // _template.<field>, so regular templates and bypass-Execute test entry points both
            // observe the existing string verbatim.
            return;
        }

        // Schema-template iteration: deep-clone every script collection so {{SchemaName}}
        // substitution (and later, query-token substitution) operates on this iteration's
        // own copies and never mutates the shared Template instance.
        var schemaNameTokens = new List<KeyValuePair<string, string>>
        {
            new("SchemaName", _schemaName)
        };

        _iteration.BeforeScripts = CloneAndSubstitute(_template.BeforeScripts, schemaNameTokens);
        _iteration.ObjectScripts = CloneAndSubstitute(_template.ObjectScripts, schemaNameTokens);
        _iteration.AfterTablesObjectScripts = CloneAndSubstitute(_template.AfterTablesObjectScripts, schemaNameTokens);
        _iteration.BetweenTablesAndKeysScripts = CloneAndSubstitute(_template.BetweenTablesAndKeysScripts, schemaNameTokens);
        _iteration.AfterTableScripts = CloneAndSubstitute(_template.AfterTableScripts, schemaNameTokens);
        _iteration.TableDataScripts = CloneAndSubstitute(_template.TableDataScripts, schemaNameTokens);
        _iteration.AfterScripts = CloneAndSubstitute(_template.AfterScripts, schemaNameTokens);

        _iteration.BaselineValidationScript = SqlScript.TokenReplace(
            _template.BaselineValidationScript ?? "", schemaNameTokens, _product.Platform);
        _iteration.VersionStampScript = SqlScript.TokenReplace(
            _template.VersionStampScript ?? "", schemaNameTokens, _product.Platform);

        // Engine-generated DDL (MissingTableAndColumnQuench, IndexOnlyQuench, MaterializedViewQuench)
        // consumes the serialized table-definition JSON literally. Slice 1's SchemaDefaultResolver
        // defaults schema-template tables / views to "{{SchemaName}}", which means the JSON carries
        // the token verbatim — substitute here so each iteration sees a fully-qualified DDL payload.
        _iteration.TableSchema = (_template.TableSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.MaterializedViewSchema = (_template.MaterializedViewSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.IndexedViewSchema = (_template.IndexedViewSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.EventSchema = (_template.EventSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.EnumTypeSchema = (_template.EnumTypeSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.SequenceSchema = (_template.SequenceSchema ?? "").Replace("{{SchemaName}}", _schemaName);
    }

    private static List<SqlScript> CloneAndSubstitute(
        List<SqlScript> source, List<KeyValuePair<string, string>> tokens)
    {
        var cloned = source.Select(s => s.Clone()).ToList();
        foreach (var script in cloned)
            script.ReplaceQueryTokens(tokens);
        return cloned;
    }

    /// <summary>
    /// A1: build the per-target version script tokens from the detected version. Called in Phase B
    /// (post-connection) before folder gates run, since folder <c>ShouldApplyExpression</c>s may
    /// reference them. <paramref name="compatibilityLevel"/> is SQL-Server-only; off SQL Server it is
    /// null and <c>{{CompatibilityLevel}}</c> falls back to the server version so one expression shape
    /// stays portable across per-platform packages. Exposed internal for unit tests that bypass Execute.
    /// </summary>
    internal void PrepareVersionScriptTokens(int serverMajorVersion, int? compatibilityLevel)
    {
        _versionScriptTokens =
        [
            new("ServerMajorVersion", serverMajorVersion.ToString(CultureInfo.InvariantCulture)),
            new("CompatibilityLevel", (compatibilityLevel ?? serverMajorVersion).ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// A1: substitute the version script tokens into this iteration's script bodies and model payloads.
    /// Runs AFTER folder gating (a gated-off folder triggers RebuildIterationScripts, which reassigns the
    /// slot lists) so the substitution isn't wiped. Script bodies are cloned only when a batch actually
    /// contains a version token, so the tokenless common case keeps aliasing the shared SqlScript instances
    /// (preserving the regular-template cross-DB HasBeenQuenched dedup bit-for-bit).
    /// </summary>
    internal void ApplyVersionScriptTokens()
    {
        if (_versionScriptTokens == null || _versionScriptTokens.Count == 0) return;

        _iteration.BeforeScripts = SubstituteVersionTokens(_iteration.BeforeScripts);
        _iteration.ObjectScripts = SubstituteVersionTokens(_iteration.ObjectScripts);
        _iteration.AfterTablesObjectScripts = SubstituteVersionTokens(_iteration.AfterTablesObjectScripts);
        _iteration.BetweenTablesAndKeysScripts = SubstituteVersionTokens(_iteration.BetweenTablesAndKeysScripts);
        _iteration.AfterTableScripts = SubstituteVersionTokens(_iteration.AfterTableScripts);
        _iteration.TableDataScripts = SubstituteVersionTokens(_iteration.TableDataScripts);
        _iteration.AfterScripts = SubstituteVersionTokens(_iteration.AfterScripts);

        _iteration.BaselineValidationScript = SubstituteVersionTokens(_iteration.BaselineValidationScript ?? _template.BaselineValidationScript);
        _iteration.VersionStampScript = SubstituteVersionTokens(_iteration.VersionStampScript ?? _template.VersionStampScript);

        // Model payloads carry component-level ShouldApplyExpression / Default / CheckExpression.
        _iteration.TableSchema = SubstituteVersionTokens(IterationTableSchema);
        _iteration.IndexedViewSchema = SubstituteVersionTokens(IterationIndexedViewSchema);
        _iteration.MaterializedViewSchema = SubstituteVersionTokens(IterationMaterializedViewSchema);
        _iteration.EventSchema = SubstituteVersionTokens(IterationEventSchema);
        _iteration.DomainTypeSchema = SubstituteVersionTokens(IterationDomainTypeSchema);
        _iteration.EnumTypeSchema = SubstituteVersionTokens(IterationEnumTypeSchema);
        _iteration.SequenceSchema = SubstituteVersionTokens(IterationSequenceSchema);
    }

    private List<SqlScript> SubstituteVersionTokens(List<SqlScript> scripts)
    {
        if (scripts == null || !scripts.Any(s => s.Batches.Any(ContainsVersionToken)))
            return scripts; // no version token present — keep the shared references (aliasing preserved)
        var cloned = scripts.Select(s => s.Clone()).ToList();
        foreach (var script in cloned)
            script.ReplaceQueryTokens(_versionScriptTokens);
        return cloned;
    }

    private string SubstituteVersionTokens(string payload) =>
        string.IsNullOrEmpty(payload) || !ContainsVersionToken(payload)
            ? payload
            : SqlScript.TokenReplace(payload, _versionScriptTokens, _product.Platform);

    private bool ContainsVersionToken(string text) =>
        !string.IsNullOrEmpty(text) &&
        _versionScriptTokens.Any(t => text.IndexOf($"{{{{{t.Key}}}}}", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>
    /// Outcome of <see cref="EnsureSchemaExists(System.Data.IDbCommand)"/>. <see cref="Execute"/>
    /// short-circuits the iteration as a benign skip on <see cref="MissingSkipped"/>; every other
    /// value means "schema situation is fine, proceed with the iteration".
    /// </summary>
    private enum SchemaPresence
    {
        NotApplicable,  // regular template or MySQL — no schema iteration
        AlreadyExists,  // schema was already present on the target
        Provisioned,    // schema was created this run (override-provision or CreateSchemaIfMissing)
        MissingSkipped  // override-sourced, missing, CreateIfMissing:false — skip the iteration
    }

    /// <summary>
    /// Schema-template existence check (design §5.3 step 3). Called before slot 1 (kindling)
    /// on every schema-template iteration. For regular templates and MySQL, <see cref="_schemaName"/>
    /// is empty and this method returns immediately. For schema templates:
    /// <list type="bullet">
    ///   <item><description>Schema exists → return (no-op).</description></item>
    ///   <item><description>Schema missing + <c>CreateSchemaIfMissing: true</c> → emit
    ///   <c>CREATE SCHEMA</c> and proceed.</description></item>
    ///   <item><description>Schema missing + <c>CreateSchemaIfMissing: false</c> (default) →
    ///   throw with a clear error pointing at the three onboarding paths.</description></item>
    /// </list>
    /// Schema names are safe to interpolate directly: <see cref="SchemaDiscovery.Discover"/>
    /// already rejects any name containing bracket / quote / brace characters (slice-3 audit I6),
    /// so <c>[{_schemaName}]</c> / <c>"{_schemaName}"</c> interpolation cannot be injected.
    /// </summary>
    private SchemaPresence EnsureSchemaExists(IDbCommand command)
    {
        if (string.IsNullOrEmpty(_schemaName)) return SchemaPresence.NotApplicable;  // regular template or MySQL — no schema iteration

        if (SchemaExists(command))
            return SchemaPresence.AlreadyExists;

        // Override-sourced units honor the work-unit-level provisioning flag. CreateIfMissing: true
        // → provision via SchemaProvisioner (idempotent IF-NOT-EXISTS DDL). CreateIfMissing: false
        // (default) → SKIP the iteration silently with an info log: return MissingSkipped so Execute()
        // short-circuits cleanly and the dispatcher records a benign skip rather than treating an
        // explicitly-declared "deploy to this list if present" as a hard failure. Discovery-sourced
        // units bypass this branch and keep the strict CreateSchemaIfMissing contract below.
        if (SchemaFromOverride)
        {
            if (ProvisionSchemaIfMissing)
            {
                ProvisionSchemaViaProvisioner(command);
                return SchemaPresence.Provisioned;
            }
            SafeProgressLog(
                $"  Schema '{_schemaName}' does not exist in database '{_databaseName}' and " +
                $"TemplateTargets CreateIfMissing is false — skipping this iteration.");
            return SchemaPresence.MissingSkipped;
        }

        if (!_template.CreateSchemaIfMissing)
            throw new Exception(
                $"Schema '{_schemaName}' does not exist in database '{_databaseName}'. " +
                "Onboarding options: (1) Set CreateSchemaIfMissing: true on the template " +
                "(accepts typo risk in discovery output). (2) Pre-create the schema manually. " +
                "(3) Call a Shared-template helper procedure (e.g., dbo.OnboardTenant) " +
                "that runs CREATE SCHEMA + INSERT atomically — see the TenantCRM demo.");

        ProvisionSchemaViaProvisioner(command);
        return SchemaPresence.Provisioned;
    }

    /// <summary>
    /// Delegates idempotent CREATE SCHEMA DDL to <see cref="SchemaProvisioner"/>. Used by both
    /// the TemplateTargets override path (<c>SchemaFromOverride</c> + <c>ProvisionSchemaIfMissing</c>)
    /// and the legacy <c>Template.CreateSchemaIfMissing: true</c> path so both share one DDL surface
    /// and one WhatIf branch. Under WhatIf the DDL renders through the progress log without touching
    /// the database.
    /// </summary>
    private void ProvisionSchemaViaProvisioner(IDbCommand command)
    {
        command.Parameters.Clear();
        try
        {
            new SchemaProvisioner().EnsureSchemaExists(command, _schemaName, _product.Platform,
                IsWhatIf, SafeProgressLog);
        }
        catch (Exception ex)
        {
            SafeProgressLogError(
                $"  CREATE SCHEMA failed for [{_schemaName}] (CreateIfMissing: true) " +
                $"— possible permission or race condition: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Returns true if <see cref="_schemaName"/> already exists on the target platform.
    /// Uses a parameter to pass the schema name so no string interpolation into the WHERE clause.
    /// </summary>
    private bool SchemaExists(IDbCommand command)
    {
        command.Parameters.Clear();
        command.CommandText = _product.Platform == Platform.SqlServer
            ? "SELECT COUNT(*) FROM sys.schemas WHERE name = @name"
            : "SELECT COUNT(*) FROM pg_namespace WHERE nspname = @name";
        var param = command.CreateParameter();
        param.ParameterName = "@name";
        param.Value = _schemaName;
        param.DbType = DbType.String;
        command.Parameters.Add(param);
        var result = Convert.ToInt32(command.ExecuteScalar());
        command.Parameters.Clear();
        return result > 0;
    }

    /// <summary>
    /// Resolves the template's <c>&lt;*Query*&gt;</c> tokens against the live silent command and
    /// applies the resolved values to every script in this iteration. <para>
    /// Regular templates keep today's behavior — the <c>_template.QueryTokens</c> dict is mutated
    /// in place and the substitution targets the shared script objects. (Idempotent because
    /// SqlScript.TokenReplace only touches placeholders that still exist in the batch text, so a
    /// second sibling DB's pass over already-resolved batches is a no-op.)
    /// </para>
    /// <para>
    /// Schema templates take a different path: query tokens are resolved into a per-iteration
    /// dictionary (NOT the shared <c>_template.QueryTokens</c>), with iteration-scoped tokens
    /// (those whose body references <c>{{SchemaName}}</c> directly or transitively per
    /// <see cref="Template.IsIterationScoped"/>) having <c>{{SchemaName}}</c> substituted in
    /// their bodies first. The resolved per-iteration map is then applied to the cloned
    /// scripts owned by this iteration.
    /// </para>
    /// </summary>
    private void ResolveAndApplyQueryTokens(IDbCommand silentCmd)
    {
        if (_template.QueryTokens.Count == 0) return;

        if (string.IsNullOrEmpty(_schemaName))
        {
            // Regular-template path: today's behavior — mutate the shared dict + scripts.
            SafeProgressLog("  Resolving template query tokens");
            TokenHelper.ResolveQueryTokens(_template.QueryTokens, _template.NonQueryTokens.ToList(),
                silentCmd, Path.GetDirectoryName(_template.FilePath), _product.Platform);
            foreach (var script in _template.ScriptFolders.SelectMany(f => f.Scripts))
                script.ReplaceQueryTokens(_template.QueryTokens.ToList());
            return;
        }

        // Schema-template path: resolve into a per-iteration copy so the next iteration of
        // the same template starts from the template's pristine <*Query*> bodies.
        SafeProgressLog("  Resolving template query tokens (per-iteration)");

        // 1. Build the per-iteration NonQueryTokens list — any iteration-scoped non-query token
        //    body (one containing {{SchemaName}} directly or transitively) needs the substitution
        //    applied BEFORE it's fed into ResolveQueryTokens (where it's inlined into <*Query*>
        //    bodies). Other non-query tokens pass through unchanged.
        var iterationNonQueryTokens = _template.NonQueryTokens
            .Select(kv =>
            {
                if (_template.IsIterationScoped(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                {
                    return new KeyValuePair<string, string>(kv.Key,
                        kv.Value.Replace("{{SchemaName}}", _schemaName, StringComparison.OrdinalIgnoreCase));
                }
                return kv;
            })
            .ToList();

        // 2. Build the per-iteration QueryTokens dict, substituting {{SchemaName}} into the
        //    bodies of iteration-scoped tokens BEFORE the query runs. Per-DB query tokens
        //    pass through unchanged — their resolved value does not depend on the iteration
        //    schema, so it can be cached across iterations (step 3 below).
        var iterationQueryTokens = new Dictionary<string, string>(_template.QueryTokens.Count);
        foreach (var kv in _template.QueryTokens)
        {
            var body = kv.Value;
            if (_template.IsIterationScoped(kv.Key) && !string.IsNullOrEmpty(body))
                body = body.Replace("{{SchemaName}}", _schemaName, StringComparison.OrdinalIgnoreCase);
            iterationQueryTokens[kv.Key] = body;
        }

        // 3. Per-DB query token cache (post-slice-8 perf, Commit B): consult the Template's
        //    per-(server, database) cache and pre-fill any per-DB token whose value has already
        //    been resolved against this target database in a prior iteration. We pull the
        //    cached entries OUT of iterationQueryTokens before handing it to ResolveQueryTokens
        //    because that resolver runs the connection round-trip for every token in the dict;
        //    leaving cached tokens in would defeat the cache. After resolution, any per-DB
        //    token that we just resolved (i.e., not already in the cache) is deposited back so
        //    sibling iterations can reuse it.
        //
        //    The cache reduces query-token connection round-trips from O(tokens × tenants) to
        //    O(tokens + tenants) for the per-DB subset. Iteration-scoped tokens still resolve
        //    every iteration as before — their resolved value differs per iteration by design.
        var perDbCache = _template.GetOrCreatePerDbTokenCache(_server, _databaseName);
        var cachedResolvedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (perDbCache != null)
        {
            // Snapshot the keys before iterating — we mutate iterationQueryTokens inside the loop.
            foreach (var tokenName in iterationQueryTokens.Keys.ToList())
            {
                if (!_template.IsPerDb(tokenName)) continue;
                if (!perDbCache.TryGetValue(tokenName, out var cachedValue)) continue;
                cachedResolvedValues[tokenName] = cachedValue;
                iterationQueryTokens.Remove(tokenName);
            }
        }

        // 4. Resolve all REMAINING query tokens against the live connection. ResolveQueryTokens
        //    mutates the supplied dictionary in place, so we hand it our iteration-scoped
        //    copy and leave _template.QueryTokens untouched for the next iteration.
        TokenHelper.ResolveQueryTokens(iterationQueryTokens, iterationNonQueryTokens,
            silentCmd, Path.GetDirectoryName(_template.FilePath), _product.Platform);

        // 5. Deposit newly-resolved per-DB token values into the cache so sibling iterations
        //    against the same (server, database) skip their round-trips.
        if (perDbCache != null)
        {
            foreach (var kv in iterationQueryTokens)
            {
                if (_template.IsPerDb(kv.Key))
                    perDbCache[kv.Key] = kv.Value;
            }
        }

        // 6. Merge cached + freshly-resolved values into a single list for downstream substitution.
        foreach (var kv in cachedResolvedValues)
            iterationQueryTokens[kv.Key] = kv.Value;

        // 7. Apply resolved values to this iteration's cloned scripts.
        var tokenList = iterationQueryTokens.ToList();
        ApplyToIteration(_iteration.BeforeScripts, tokenList);
        ApplyToIteration(_iteration.ObjectScripts, tokenList);
        ApplyToIteration(_iteration.AfterTablesObjectScripts, tokenList);
        ApplyToIteration(_iteration.BetweenTablesAndKeysScripts, tokenList);
        ApplyToIteration(_iteration.AfterTableScripts, tokenList);
        ApplyToIteration(_iteration.TableDataScripts, tokenList);
        ApplyToIteration(_iteration.AfterScripts, tokenList);
    }

    private static void ApplyToIteration(
        List<SqlScript> scripts, List<KeyValuePair<string, string>> tokens)
    {
        if (scripts == null || scripts.Count == 0 || tokens.Count == 0) return;
        foreach (var script in scripts)
            script.ReplaceQueryTokens(tokens);
    }

    #endregion

    #region Platform Dispatch Helpers

    /// <summary>
    /// Returns true if the current run is WhatIf-only.
    /// SqlServer/MySQL use "1"; PostgreSQL uses "true".
    /// </summary>
    internal bool IsWhatIf => _product.Platform == Platform.PostgreSQL
        ? _whatIfOnly == "true"
        : _whatIfOnly == "1";

    /// <summary>
    /// Formats a boolean value for platform-specific SQL parameters.
    /// </summary>
    internal string FormatBooleanFlag(bool value) => _product.Platform switch
    {
        Platform.PostgreSQL => value ? "true" : "false",
        _ => value ? "1" : "0"
    };

    /// <summary>
    /// Quotes a database name for USE statement per platform.
    /// </summary>
    internal static string QuoteUseDatabase(string dbName, Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"USE [{Identifier.EscapeDelimited(dbName, platform)}]",
        Platform.PostgreSQL => dbName, // PostgreSQL uses ChangeDatabase API
        Platform.MySQL => $"USE `{Identifier.EscapeDelimited(dbName, platform)}`",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };

    private string QuoteUseDatabase(string dbName) => QuoteUseDatabase(dbName, _product.Platform);

    /// <summary>
    /// Quotes an identifier per platform.
    /// </summary>
    internal static string QuoteIdentifier(string name, Platform platform) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"[{Identifier.EscapeDelimited(name, platform)}]",
        Platform.PostgreSQL => $"\"{Identifier.EscapeDelimited(name, platform)}\"",
        Platform.MySQL => $"`{Identifier.EscapeDelimited(name, platform)}`",
        _ => name
    };

    internal static string EscapeSqlLiteral(string value) => value?.Replace("'", "''") ?? "";

    /// <summary>
    /// Gets the delete SQL for CompletedMigrationScripts per platform. Strict equality on
    /// BOTH template_name and schema_name so a prune in one (template, schema) iteration
    /// can never delete rows owned by a different iteration — including legacy blank-template
    /// rows that may be shared across multiple templates in the same product. (Reads are
    /// permissive on template_name to pick up those legacy rows; writes/deletes are strict.)
    /// </summary>
    internal string GetDeleteCompletedScriptSql(string productName, string slot, string obsoleteScript, string templateName, string schemaName) => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"DELETE SchemaSmith.CompletedMigrationScripts WHERE [ProductName] = '{EscapeSqlLiteral(productName)}' AND [QuenchSlot] = '{EscapeSqlLiteral(slot)}' AND [ScriptPath] = '{EscapeSqlLiteral(obsoleteScript)}' AND [template_name] = '{EscapeSqlLiteral(templateName)}' AND [schema_name] = '{EscapeSqlLiteral(schemaName)}'",
        Platform.PostgreSQL => $"DELETE FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{EscapeSqlLiteral(productName)}' AND \"QuenchSlot\" = '{EscapeSqlLiteral(slot)}' AND \"ScriptPath\" = '{EscapeSqlLiteral(obsoleteScript)}' AND template_name = '{EscapeSqlLiteral(templateName)}' AND schema_name = '{EscapeSqlLiteral(schemaName)}'",
        Platform.MySQL => $"DELETE FROM `SchemaSmith_CompletedMigrationScripts` WHERE `ProductName` = '{EscapeSqlLiteral(productName)}' AND `QuenchSlot` = '{EscapeSqlLiteral(slot)}' AND `ScriptPath` = '{EscapeSqlLiteral(obsoleteScript)}' AND `template_name` = '{EscapeSqlLiteral(templateName)}' AND `schema_name` = '{EscapeSqlLiteral(schemaName)}'",
        _ => throw new ArgumentOutOfRangeException()
    };

    /// <summary>
    /// Gets the SELECT SQL for completed migration scripts per platform. Scoped strictly to the
    /// active (template, schema) — per-template ownership is the design intent, and a run-once
    /// script is complete for the template that ran it, not for every template sharing its name.
    /// </summary>
    internal string GetSelectCompletedScriptsSql(string productName, string slot, string templateName, string schemaName) => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"SELECT [ScriptPath] FROM SchemaSmith.CompletedMigrationScripts WITH (NOLOCK) WHERE [ProductName] = '{EscapeSqlLiteral(productName)}' AND [QuenchSlot] = '{EscapeSqlLiteral(slot)}' AND [template_name] = '{EscapeSqlLiteral(templateName)}' AND [schema_name] = '{EscapeSqlLiteral(schemaName)}'",
        Platform.PostgreSQL => $"SELECT \"ScriptPath\" FROM \"SchemaSmith\".\"CompletedMigrationScripts\" WHERE \"ProductName\" = '{EscapeSqlLiteral(productName)}' AND \"QuenchSlot\" = '{EscapeSqlLiteral(slot)}' AND template_name = '{EscapeSqlLiteral(templateName)}' AND schema_name = '{EscapeSqlLiteral(schemaName)}'",
        Platform.MySQL => $"SELECT `ScriptPath` FROM `SchemaSmith_CompletedMigrationScripts` WHERE `ProductName` = '{EscapeSqlLiteral(productName)}' AND `QuenchSlot` = '{EscapeSqlLiteral(slot)}' AND `template_name` = '{EscapeSqlLiteral(templateName)}' AND `schema_name` = '{EscapeSqlLiteral(schemaName)}'",
        _ => throw new ArgumentOutOfRangeException()
    };

    /// <summary>
    /// Gets the INSERT SQL for completed migration scripts per platform. Always writes the
    /// actual template_name + schema_name values from the active scope (legacy blank rows
    /// only arrive from pre-extension databases; new writes always have real values).
    /// </summary>
    internal string GetInsertCompletedScriptSql(string scriptPath, string productName, string slot, string templateName, string schemaName) => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath], [ProductName], [QuenchSlot], [template_name], [schema_name]) VALUES('{EscapeSqlLiteral(scriptPath)}', '{EscapeSqlLiteral(productName)}', '{EscapeSqlLiteral(slot)}', '{EscapeSqlLiteral(templateName)}', '{EscapeSqlLiteral(schemaName)}')",
        Platform.PostgreSQL => $"INSERT INTO \"SchemaSmith\".\"CompletedMigrationScripts\" (\"ScriptPath\", \"ProductName\", \"QuenchSlot\", template_name, schema_name) VALUES('{EscapeSqlLiteral(scriptPath)}', '{EscapeSqlLiteral(productName)}', '{EscapeSqlLiteral(slot)}', '{EscapeSqlLiteral(templateName)}', '{EscapeSqlLiteral(schemaName)}')",
        Platform.MySQL => $"INSERT INTO `SchemaSmith_CompletedMigrationScripts` (`ScriptPath`, `ProductName`, `QuenchSlot`, `template_name`, `schema_name`) VALUES('{EscapeSqlLiteral(scriptPath)}', '{EscapeSqlLiteral(productName)}', '{EscapeSqlLiteral(slot)}', '{EscapeSqlLiteral(templateName)}', '{EscapeSqlLiteral(schemaName)}')",
        _ => throw new ArgumentOutOfRangeException()
    };

    internal string ResolveArtifactDirectory()
    {
        var configured = FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.ArtifactPath];
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    internal bool ScrubArtifactsEnabled =>
        FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.ScrubArtifacts]?.ToLower() == "true";

    internal IReadOnlyList<KeyValuePair<string, string>> SensitiveTokenValues()
    {
        var options = LogHygieneOptions.FromConfiguration(FactoryContainer.ResolveOrCreate<IConfigurationRoot>());
        return _product.ScriptTokens
            .Concat(_template.ScriptTokens)
            .Where(kv => LogScrubber.ShouldScrubName(kv.Key, options))
            .ToList();
    }

    #endregion

    #region Platform-Specific Table Quench SQL

    private void QuenchMissingTablesAndColumns(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Tables.Count == 0)
            return;

        SafeProgressLog("  Quenching missing tables and columns");

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
            {
                var updateFillFactor = _template.UpdateFillFactor ? "1" : "0";
                tableCommand.CommandText = _ingestEncoding == IngestEncoding.Xml
                    ? $@"
DECLARE @TableDefinitions XML = '{EscapeSqlLiteral(IterationTableXml)}',
        @UpdateFillFactor BIT = {updateFillFactor}
{ForgeKindler.GetParseTableXmlScript(Platform.SqlServer)}
EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingTableAndColumnQuench @WhatIf = {_whatIfOnly}"
                    : $@"
DECLARE @TableDefinitions VARCHAR(MAX)= '{EscapeSqlLiteral(IterationTableSchema)}',
        @UpdateFillFactor BIT = {updateFillFactor}
{ForgeKindler.GetParseTableJsonScript(Platform.SqlServer)}
EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingTableAndColumnQuench @WhatIf = {_whatIfOnly}";
                break;
            }
            case Platform.PostgreSQL:
            {
                tableCommand.CommandText = $@"
DO $$
DECLARE
  p_UpdateFillFactor BOOL = {_template.UpdateFillFactor.ToString().ToLower()};
  table_json JSON = '{EscapeSqlLiteral(IterationTableSchema)}';
  sql_script TEXT = '';
BEGIN
{ForgeKindler.GetParseTableJsonScript(Platform.PostgreSQL)}
END $$ LANGUAGE plpgsql;

CALL ""SchemaSmith"".""MissingTableAndColumnQuench""(p_WhatIf := {_whatIfOnly})";
                break;
            }
            case Platform.MySQL:
            {
                ParseMySqlTableJson(tableCommand);
                var whatIf = _whatIfOnly == "1" ? 1 : 0;
                tableCommand.CommandText = $"CALL SchemaSmith_MissingTableAndColumnQuench('{EscapeSqlLiteral(_databaseName)}', {whatIf})";
                break;
            }
        }

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Missing Tables And Columns"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    internal void QuenchModifiedTables(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Tables.Count == 0)
            return;

        SafeProgressLog("  Quenching modified tables");

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                tableCommand.CommandText = $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ModifiedTableQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @DropUnknownIndexes = {_dropUnknownIndexes}, @WhatIf = {_whatIfOnly}, @DropTablesRemovedFromProduct = {_dropRemovedTables}, @DropColumnsRemovedFromProduct = {_dropRemovedColumns}, @DropForeignKeysRemovedFromProduct = {_dropRemovedForeignKeys}, @DropCheckConstraintsRemovedFromProduct = {_dropRemovedCheckConstraints}, @DropExcludeConstraintsRemovedFromProduct = {_dropRemovedExcludeConstraints}, @DropStatisticsRemovedFromProduct = {_dropRemovedStatistics}, @DropIndexesRemovedFromProduct = {_dropRemovedIndexes}, @CaptureWouldDrop = {FormatBooleanFlag(CaptureWouldDrop)}, @RebuildPolicyMode = '{RebuildPolicyMode}', @RebuildPolicyThreshold = {RebuildPolicyThreshold}, @RebuildPolicyOnOrderMismatch = {RebuildPolicyOnOrderMismatch}, @DropSchemaBoundDependents = {(DropSchemaBoundDependents ? 1 : 0)}";
                break;
            case Platform.PostgreSQL:
                tableCommand.CommandText = $@"
CALL ""SchemaSmith"".""ValidateTableOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
CALL ""SchemaSmith"".""ModifiedTableQuench""(p_DropUnknownIndexes := {_dropUnknownIndexes}, p_WhatIf := {_whatIfOnly}, p_DropTablesRemovedFromProduct := {_dropRemovedTables}, p_DropColumnsRemovedFromProduct := {_dropRemovedColumns}, p_DropForeignKeysRemovedFromProduct := {_dropRemovedForeignKeys}, p_DropCheckConstraintsRemovedFromProduct := {_dropRemovedCheckConstraints}, p_DropExcludeConstraintsRemovedFromProduct := {_dropRemovedExcludeConstraints}, p_DropStatisticsRemovedFromProduct := {_dropRemovedStatistics}, p_DropIndexesRemovedFromProduct := {_dropRemovedIndexes}, p_CaptureWouldDrop := {FormatBooleanFlag(CaptureWouldDrop)}, p_RebuildPolicyMode := '{RebuildPolicyMode}', p_RebuildPolicyThreshold := {RebuildPolicyThreshold}, p_RebuildPolicyOnOrderMismatch := {RebuildPolicyOnOrderMismatch});";
                break;
            case Platform.MySQL:
            {
                if (!MySqlTempTablesExist(tableCommand))
                    ParseMySqlTableJson(tableCommand);
                // The #270 index no-drop-protection capture moved into ModifiedTableQuench with STEP 8,
                // and it gates on the @ss_capture_would_drop SESSION variable rather than a parameter
                // (it was written for a proc that had none). This branch never set it, so after the move
                // it would read whatever the pooled connection happened to carry -- NULL on a fresh one,
                // or a stale value from a previous template. Set it here as QuenchIndexesAndConstraints
                // and QuenchForeignKeys already do. Must precede the CommandText assignment below,
                // because SetMySqlCaptureFlag reuses the same command.
                SetMySqlCaptureFlag(tableCommand);
                var whatIf = _whatIfOnly == "1" ? 1 : 0;
                var dropRemoved = _dropRemovedTables == "1" ? 1 : 0;
                var dropRemovedCols = _dropRemovedColumns == "1" ? 1 : 0;
                var dropRemovedChecks = _dropRemovedCheckConstraints == "1" ? 1 : 0;
                var dropRemovedExcludes = _dropRemovedExcludeConstraints == "1" ? 1 : 0;
                var dropRemovedStats = _dropRemovedStatistics == "1" ? 1 : 0;
                var captureWouldDrop = CaptureWouldDrop ? 1 : 0;
                var dropUnknown = _dropUnknownIndexes == "1" ? 1 : 0;
                var dropRemovedIndexes = _dropRemovedIndexes == "1" ? 1 : 0;
                SetMySqlRebuildPolicy(tableCommand);
                tableCommand.CommandText = $"CALL SchemaSmith_ModifiedTableQuench('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}', {whatIf}, {dropRemoved}, {dropRemovedCols}, {dropRemovedChecks}, {dropRemovedExcludes}, {dropRemovedStats}, {captureWouldDrop}, {dropUnknown}, {dropRemovedIndexes})";
                break;
            }
        }

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Modified Tables"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    internal void QuenchIndexesAndConstraints(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Tables.Count == 0)
            return;

        SafeProgressLog($"  Quenching indexes{(_template.IndexOnlyTableQuenches ? "" : " and constraints")}");

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
            {
                var updateFillFactor = _template.UpdateFillFactor ? "1" : "0";
                var indexOnlyTableDefs = _ingestEncoding == IngestEncoding.Xml ? IterationTableXml : IterationTableSchema;
                tableCommand.CommandText = _template.IndexOnlyTableQuenches
                    ? $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.IndexOnlyQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @TableDefinitions = '{EscapeSqlLiteral(indexOnlyTableDefs)}', @DropUnknownIndexes = {_dropUnknownIndexes}, @DropIndexesRemovedFromProduct = {_dropRemovedIndexes}, @UpdateFillFactor = {updateFillFactor}, @WhatIf = {_whatIfOnly}, @CaptureWouldDrop = {FormatBooleanFlag(CaptureWouldDrop)}"
                    : $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingIndexesAndConstraintsQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @WhatIf = {_whatIfOnly}";
                break;
            }
            case Platform.PostgreSQL:
                tableCommand.CommandText = _template.IndexOnlyTableQuenches
                    ? $@"
CALL ""SchemaSmith"".""IndexOnlyQuench""(p_TableDefinitions := '{EscapeSqlLiteral(IterationTableSchema)}', p_DropUnknownIndexes := {_dropUnknownIndexes}, p_DropIndexesRemovedFromProduct := {_dropRemovedIndexes}, p_WhatIf := {_whatIfOnly}, p_UpdateFillFactor := {_template.UpdateFillFactor.ToString().ToLower()}, p_CaptureWouldDrop := {FormatBooleanFlag(CaptureWouldDrop)});
CALL ""SchemaSmith"".""ReplicaIdentityQuench""(p_WhatIf := {_whatIfOnly});
CALL ""SchemaSmith"".""FixupIndexOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
"
                    : $@"
CALL ""SchemaSmith"".""MissingIndexesAndConstraintsQuench""(p_WhatIf := {_whatIfOnly});
CALL ""SchemaSmith"".""ReplicaIdentityQuench""(p_WhatIf := {_whatIfOnly});
CALL ""SchemaSmith"".""FixupTableOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
CALL ""SchemaSmith"".""FixupIndexOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
";
                break;
            case Platform.MySQL:
            {
                if (!MySqlTempTablesExist(tableCommand))
                    ParseMySqlTableJson(tableCommand);
                SetMySqlCaptureFlag(tableCommand);
                var whatIf = _whatIfOnly == "1" ? 1 : 0;
                var dropUnknown = _dropUnknownIndexes == "1" ? 1 : 0;
                var dropRemovedChecks = _dropRemovedCheckConstraints == "1" ? 1 : 0;
                // On MySQL/MariaDB the index REMOVAL happens inside ModifiedTableQuench, matching SQL
                // Server and PostgreSQL; MissingIndexesAndConstraintsQuench is add-only for indexes and
                // no longer takes the two index-drop flags. IndexOnlyQuench still owns both of them --
                // do not infer from a signature that an engine cannot do the thing.
                var dropRemovedIndexes = _dropRemovedIndexes == "1" ? 1 : 0;
                tableCommand.CommandText = _template.IndexOnlyTableQuenches
                    ? $"CALL SchemaSmith_IndexOnlyQuench('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}', {whatIf}, {dropUnknown}, {dropRemovedIndexes})"
                    : $"CALL SchemaSmith_MissingIndexesAndConstraintsQuench('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}', {whatIf}, {dropRemovedChecks})";
                break;
            }
        }

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Indexes"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    // MySQL threads the no-drop-protection capture signal via a connection session variable rather
    // than a proc parameter — its FK / index / check quench procs have too many direct call sites to
    // add a parameter cleanly. Set it explicitly (0/1) before each such proc so a pooled connection
    // never carries a stale value; the procs read `COALESCE(@ss_capture_would_drop, 0)`.
    private void SetMySqlCaptureFlag(IDbCommand tableCommand)
    {
        tableCommand.CommandText = $"SET @ss_capture_would_drop = {(CaptureWouldDrop ? 1 : 0)}";
        tableCommand.ExecuteNonQuery();
    }

    // The resolved upper-tier RebuildPolicy reaches MySQL's ModifiedTableQuench the same way the no-drop
    // capture signal does, and for the same reason: MySQL has no default parameter values, so adding one
    // is a breaking change for every direct call site (~30 of them here). Set all three explicitly before
    // each call so a pooled connection never carries a previous template's policy into this one — a stale
    // ALWAYS would rebuild tables that never asked for it. The proc reads them through COALESCE, so an
    // unset variable means NEVER.
    private void SetMySqlRebuildPolicy(IDbCommand tableCommand)
    {
        tableCommand.CommandText = $"SET @ss_rebuild_policy_mode = '{RebuildPolicyMode}', "
                                   + $"@ss_rebuild_policy_threshold = {RebuildPolicyThreshold}, "
                                   + $"@ss_rebuild_policy_on_order_mismatch = {RebuildPolicyOnOrderMismatch}, "
                                   + $"@ss_system_versioning_alter_history = '{SystemVersioningAlterHistory}', "
                                   + $"@ss_drop_periods_removed = {(DropPeriodsRemovedFromProduct ? 1 : 0)}";
        tableCommand.ExecuteNonQuery();
    }

    internal void QuenchForeignKeys(IDbCommand tableCommand)
    {
        if (_template.Tables.Count == 0)
            return;

        SafeProgressLog("  Quenching foreign keys");

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                tableCommand.CommandText = $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ForeignKeyQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @WhatIf = {_whatIfOnly}";
                break;
            case Platform.PostgreSQL:
                tableCommand.CommandText = $@"CALL ""SchemaSmith"".""ForeignKeyQuench""(p_WhatIf := {_whatIfOnly});";
                break;
            case Platform.MySQL:
            {
                if (!MySqlTempTablesExist(tableCommand))
                    ParseMySqlTableJson(tableCommand);
                SetMySqlCaptureFlag(tableCommand);
                var whatIf = _whatIfOnly == "1" ? 1 : 0;
                var dropUnknown = _dropUnknownIndexes == "1" ? 1 : 0;
                var dropRemovedFks = _dropRemovedForeignKeys == "1" ? 1 : 0;
                tableCommand.CommandText = $"CALL SchemaSmith_ForeignKeyQuench('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}', {whatIf}, {dropUnknown}, {dropRemovedFks})";
                break;
            }
        }

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Foreign Keys"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    // Materialized-view DDL against sibling tenant schemas in the SAME database races on the
    // PostgreSQL relation cache under parallel schema-template fan-out ("could not open relation with
    // OID"), and under enough contention the race can break the connection — which the
    // transient-contention retry can't recover, because the materialized-view procs depend on
    // session-scoped temp tables and the connection can't be reopened without losing them. Serialize
    // the materialized-view phase per target database (keyed on server + database) so no two
    // iterations run materialized-view DDL against the same database at once; iterations against
    // different databases, and every other quench phase, stay fully parallel. The retry stays as a
    // backstop for any residual transient contention.
    private static readonly ConcurrentDictionary<string, object> MaterializedViewPhaseLocks = new();

    private object MaterializedViewPhaseLock() =>
        MaterializedViewPhaseLocks.GetOrAdd($"{_server}\0{_databaseName}", _ => new object());

    internal void QuenchMaterializedViews(IDbCommand tableCommand)
    {
        SafeProgressLog("  Quenching materialized views");

        var updateFillFactor = _template.UpdateFillFactor.ToString().ToLower();
        tableCommand.CommandText = $@"CALL ""SchemaSmith"".""MaterializedViewQuench""('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(IterationMaterializedViewSchema)}', {_whatIfOnly}, {updateFillFactor}, '{EscapeSqlLiteral(_template.Name)}', '{EscapeSqlLiteral(_schemaName)}');";

        lock (MaterializedViewPhaseLock())
        {
            _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Materialized Views"), tableCommand.CommandText);
            ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
            _debugFileLocation = "";
        }
    }

    /// <summary>
    /// Converges DECLARED scheduled events (MySQL/MariaDB). Scripted events in the same Events/ folder
    /// still run through the Objects slot untouched, so this is purely additive for existing packages.
    /// <para><b>This is the only quench that executes DDL from C# rather than inside the procedure, and
    /// it is not a style choice.</b> MySQL cannot PREPARE event DDL at all — both CREATE EVENT and DROP
    /// EVENT fail with 1295, "This command is not supported in the prepared statement protocol yet" —
    /// so a stored procedure physically cannot create an event there. MariaDB can, but writing to the
    /// lower common denominator keeps ONE implementation for both engines.</para>
    /// <para>All the decision-making still lives in SQL: the procedure compares, decides, and returns an
    /// ORDERED list of statements. This method is a dumb executor. The ownership and audit writes are
    /// part of that list, so if a CREATE fails execution stops and no ownership row is left claiming an
    /// event that does not exist.</para>
    /// </summary>
    internal void QuenchEvents(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() != Platform.MySQL) return;
        var events = IterationEventSchema;
        // An empty/absent list is overwhelmingly the common case -- skip the round trip UNLESS we are
        // dropping events by absence, in which case a product that used to own events and now declares
        // none still needs the drop-by-absence pass to run against an empty declared set. Fall through
        // with a canonical "[]" so EventQuench drops the previously-owned events rather than silently
        // leaving them behind.
        if (string.IsNullOrWhiteSpace(events) || events.Trim() == "[]")
        {
            if (!DropRemovedEvents) return;
            events = "[]";
        }

        SafeProgressLog("  Quenching scheduled events");
        var whatIf = _whatIfOnly == "1" ? 1 : 0;
        var dropRemoved = DropRemovedEvents ? 1 : 0;
        tableCommand.CommandText =
            $"CALL SchemaSmith_EventQuench('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}', "
            + $"'{EscapeSqlLiteral(events)}', {whatIf}, {dropRemoved}, '{EscapeSqlLiteral(_template.Name)}')";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Events"), tableCommand.CommandText);

        // Read the whole list BEFORE executing any of it: the reader holds the connection, and the
        // statements below run on that same connection.
        var statements = new List<string>();
        using (var reader = tableCommand.ExecuteReader())
        {
            while (reader.Read())
                if (!reader.IsDBNull(0)) statements.Add(reader.GetString(0));
        }

        foreach (var statement in statements)
        {
            tableCommand.CommandText = statement;
            ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        }
        _debugFileLocation = "";
    }

    /// <summary>
    /// Converges DECLARED enum types (PostgreSQL). Runs BEFORE tables: a column can be of an enum type,
    /// so the type has to exist first, and a value the package adds has to be there before a column
    /// default or check references it.
    /// </summary>
    /// <summary>
    /// Converges DECLARED domain types (PostgreSQL). Runs before tables: a column can be OF a domain.
    /// <para>Everything but the base type converges in place, without dropping the domain or touching a
    /// dependent column. A base-type change is refused by name inside the procedure — there is no
    /// <c>ALTER DOMAIN … TYPE</c>, so delivering it would mean dropping every column that uses it.</para>
    /// </summary>
    internal void QuenchDomainTypes(IDbCommand tableCommand)
    {
        if (_product.Platform != Platform.PostgreSQL) return;
        var types = IterationDomainTypeSchema;
        if (string.IsNullOrWhiteSpace(types) || types.Trim() == "[]") return;

        SafeProgressLog("  Quenching domain types");
        tableCommand.CommandText =
            $@"CALL ""SchemaSmith"".""DomainTypeQuench""('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(types)}', {_whatIfOnly});";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Domain Types"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    internal void QuenchEnumTypes(IDbCommand tableCommand)
    {
        if (_product.Platform != Platform.PostgreSQL) return;
        var types = IterationEnumTypeSchema;
        if (string.IsNullOrWhiteSpace(types) || types.Trim() == "[]") return;

        SafeProgressLog("  Quenching enum types");
        tableCommand.CommandText =
            $@"CALL ""SchemaSmith"".""EnumTypeQuench""('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(types)}', {_whatIfOnly});";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Enum Types"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    /// <summary>
    /// Converges DECLARED sequences (PostgreSQL). Runs beside enum types, before tables: a column DEFAULT
    /// can call nextval() on one, so the sequence has to exist before the table that references it.
    /// </summary>
    internal void QuenchSequences(IDbCommand tableCommand)
    {
        if (_product.Platform != Platform.PostgreSQL) return;
        var sequences = IterationSequenceSchema;
        if (string.IsNullOrWhiteSpace(sequences) || sequences.Trim() == "[]") return;

        SafeProgressLog("  Quenching sequences");
        tableCommand.CommandText =
            $@"CALL ""SchemaSmith"".""SequenceQuench""('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(sequences)}', {_whatIfOnly});";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Sequences"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    internal void QuenchIndexedViews(IDbCommand tableCommand)
    {
        // Note: Cross-product conflict detection (view owned by a different product) is handled
        // inside IndexedViewQuench.sql via THROW, mirroring PostgreSQL's ValidateMaterializedViewOwnership.
        // The standalone ValidateIndexedViewOwnership.sql and FixupIndexedViewOwnership.sql procedures
        // serve a different purpose: they find/adopt *untagged* indexed views (created outside SchemaSmith).
        // They are deployed to the database by ForgeKindler for manual/diagnostic use but are not called
        // during the quench flow — untagged views are simply left alone (the quench creates new views
        // rather than adopting existing ones).
        SafeProgressLog("  Quenching indexed views");

        // Validate required clustered index before attempting quench
        foreach (var iv in _template.IndexedViews)
        {
            if (!iv.Indexes.Any(i => i.Clustered && i.Unique))
                throw new Exception($"Indexed view {iv.Schema}.{iv.Name} requires a unique clustered index");
        }

        // Pass ALL indexed views to the proc; ShouldApplyExpression is evaluated per-target
        // server-side (mirroring PostgreSQL materialized views), so no C# pre-filtering.
        // Route through the iteration-aware schema string so {{SchemaName}} substitution
        // (schema templates) is already applied; for regular templates it's the full set verbatim.
        var viewSchema = _ingestEncoding == IngestEncoding.Xml ? IterationIndexedViewXml : IterationIndexedViewSchema;
        var updateFillFactor = _template.UpdateFillFactor.ToString().ToLower();
        // B5 fix: thread @TemplateName + @SchemaName so the existing-views lookup in the proc
        // is scoped to the iteration's schema. Regular templates pass @SchemaName = '' and the
        // proc falls through to today's all-schemas behavior.
        tableCommand.CommandText = $@"EXEC [SchemaSmith].[IndexedViewQuench] @ProductName = '{EscapeSqlLiteral(_product.Name)}', @IndexedViewSchema = '{EscapeSqlLiteral(viewSchema)}', @WhatIf = {_whatIfOnly}, @UpdateFillFactor = {updateFillFactor}, @TemplateName = N'{EscapeSqlLiteral(_template.Name)}', @SchemaName = N'{EscapeSqlLiteral(_schemaName)}';";

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Indexed Views"), tableCommand.CommandText);
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    #endregion

    #region MySQL Temp Tables

    private void ParseMySqlTableJson(IDbCommand command)
    {
        var tableJson = !string.IsNullOrEmpty(_template.TableSchema)
            ? _template.TableSchema
            : JsonHelper.SerializeAll(_template.Tables);
        command.CommandText = $"CALL SchemaSmith_ParseTableJson('{EscapeSqlLiteral(_databaseName)}', @tableJson)";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Parse Table Json"), command.CommandText.Replace("@tableJson", $"'{EscapeSqlLiteral(tableJson)}'"));
        AddJsonParameter(command, "@tableJson", tableJson);
        ExecuteNonQueryHandlingMessages(command);
        ClearParameters(command);
    }

    private static bool MySqlTempTablesExist(IDbCommand command)
    {
        try
        {
            command.CommandText = "SELECT COUNT(*) FROM _SchemaSmith_Tables";
            command.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupMySqlTempTables(IDbCommand command)
    {
        try
        {
            command.CommandText = @"
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Tables;
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Columns;
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Indexes;
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ForeignKeys;
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CheckConstraints;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void AddJsonParameter(IDbCommand command, string name, string json)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = json;
        param.DbType = DbType.String;
        command.Parameters.Add(param);
    }

    private static void ClearParameters(IDbCommand command)
    {
        command.Parameters.Clear();
    }

    #endregion

    #region Connection and Execution

    private IDbConnection GetConnection(bool fireInfoMessageEventOnUserErrors = true, bool ignoreInfoMessages = false)
    {
        var config = FactoryContainer.ResolveOrCreate<IConfigurationRoot>();
        var connectionStringOverride = CommandLineParser.ValueOfSwitch("ConnectionString", null);
        var connectionString = string.IsNullOrEmpty(connectionStringOverride)
            ? TargetConnectionString.Build(_product.Platform, _server, _databaseName, config)
            : ConnectionString.RetargetDatabase(connectionStringOverride, _databaseName, _product.Platform);
        var factory = DbConnectionFactory.ForPlatform(_product.Platform);
        var connection = factory.GetDbConnection(connectionString);

        // Platform-specific message handling
        if (!ignoreInfoMessages)
        {
            switch (_product.Platform.GetBasePlatform())
            {
                case Platform.SqlServer when connection is SqlConnection sqlConnection:
                    sqlConnection.InfoMessage += OnSqlServerInfoMessage;
                    sqlConnection.FireInfoMessageEventOnUserErrors = fireInfoMessageEventOnUserErrors;
                    break;
                case Platform.PostgreSQL when connection is NpgsqlConnection pgConnection:
                    pgConnection.Notice += OnPostgreSqlNotice;
                    break;
            }
        }

        connection.Open();

        // PostgreSQL: set the unsupported-feature policy on every convergence connection (built directly to
        // the target database, so no ChangeDatabase resets the session) via a GUC that works on every PG
        // version; version-gated emit sites read it via SchemaSmith.UnsupportedFeaturePolicy() to choose
        // degrade-with-warning (default) vs abort. SQL Server bakes the same policy into the helper function
        // at kindle time instead (dropping the 2016+ sp_set_session_context transport, unavailable on a
        // genuine old binary) — see the KindleTheForge call in Execute. MySQL/MariaDB use a session
        // variable that works on every version (mirroring @schemasmith_version_override).
        // The SQL helper defaults to 'warn', so only an explicit 'fail' matters.
        if (_product.Platform.GetBasePlatform() == Platform.PostgreSQL)
        {
            var policy = string.Equals(config[SettingsKeys.UnsupportedFeaturePolicy], "fail", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
            using var policyCmd = connection.CreateCommand();
            policyCmd.CommandText = $"SET schemasmith.unsupported_policy = '{policy}'";
            policyCmd.ExecuteNonQuery();
        }
        else if (_product.Platform.GetBasePlatform() == Platform.MySQL)
        {
            var policy = string.Equals(config[SettingsKeys.UnsupportedFeaturePolicy], "fail", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
            using var policyCmd = connection.CreateCommand();
            policyCmd.CommandText = $"SET @schemasmith_unsupported_policy = '{policy}'";
            policyCmd.ExecuteNonQuery();
        }

        // MySQL: start status message monitor
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _statusMonitor == null)
        {
            try
            {
                using var sessionCmd = connection.CreateCommand();
                sessionCmd.CommandText = "SELECT CONNECTION_ID()";
                var sessionId = Convert.ToInt64(sessionCmd.ExecuteScalar());
                _statusMonitor = new StatusMessageMonitor(connectionString, sessionId, msg => SafeProgressLog($"    {msg}"));
            }
            catch
            {
                // Status monitoring is best-effort
            }
        }

        return connection;
    }

    // Bounded retry policy for deadlock victims — defense-in-depth. Internal (not const) so tests
    // can shrink the delay/attempts. The PostgreSQL convergence-proc deadlock that originally
    // motivated this is now prevented at the SQL level (MissingIndexesAndConstraintsQuench reads
    // pg_catalog instead of materialising information_schema.columns); retry remains to cover
    // residual cross-iteration contention such as SQL Server IndexedViewQuench. The convergence
    // procs are idempotent (recompute desired-vs-existing every run), so a victim re-converges cleanly.
    internal int MaxDeadlockAttempts = 20;
    internal int DeadlockRetryBaseMs = 100;

    /// <param name="retryOnDeadlock">
    /// When <c>true</c>, a deadlock-victim failure is retried (bounded, with backoff). Only pass
    /// <c>true</c> for idempotent convergence procs (table/index/constraint/FK/view quenches) —
    /// never for run-once migrations or arbitrary user scripts, whose re-execution isn't safe.
    /// </param>
    internal void ExecuteNonQueryHandlingMessages(IDbCommand command, bool retryOnDeadlock = false)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                ExecuteNonQueryOnce(command);
                return;
            }
            catch (Exception ex) when (retryOnDeadlock
                                       && attempt < MaxDeadlockAttempts
                                       && DeadlockClassifier.IsRetryableContention(ex))
            {
                var delayMs = DeadlockBackoffMs(attempt);
                SafeProgressLog(
                    $"    Transient contention from a parallel iteration; retrying " +
                    $"(attempt {attempt + 1} of {MaxDeadlockAttempts}){(delayMs > 0 ? $" after {delayMs} ms" : "")}");
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
        }
    }

    // "Full jitter" exponential backoff (sleep uniformly in [0, capped-exponential]). If several
    // iterations do deadlock at once, full jitter de-synchronises them far better than
    // fixed-delay-plus-small-jitter, so retries don't re-collide in lockstep.
    private int DeadlockBackoffMs(int attempt)
    {
        if (DeadlockRetryBaseMs <= 0) return 0;
        var capped = Math.Min(DeadlockRetryBaseMs * (1 << Math.Min(attempt - 1, 5)), 2000);
        return Random.Shared.Next(0, capped + 1);
    }

    private void ExecuteNonQueryOnce(IDbCommand command)
    {
        _infoMessageException = null;

        if (_product.Platform.GetBasePlatform() == Platform.MySQL)
        {
            try
            {
                command.ExecuteNonQuery();
                _statusMonitor?.Flush();
            }
            catch
            {
                _statusMonitor?.Flush();
                throw;
            }
        }
        else
        {
            command.ExecuteNonQuery();
            if (_infoMessageException != null) throw _infoMessageException;
        }
    }

    private string LogSqlScript(string name, string sql)
    {
        try
        {
            var dir = ResolveArtifactDirectory();
            DirectoryWrapper.GetFromFactory().CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            var toWrite = ScrubArtifactsEnabled ? ResolvedSqlArtifactWriter.Scrub(sql, SensitiveTokenValues()) : sql;
            FileWrapper.GetFromFactory().WriteAllText(path, toWrite);
            return path;
        }
        catch (Exception ex)
        {
            SafeProgressLog($"    Could not write debug SQL artifact '{name}': {ex.Message}");
            return "";
        }
    }

    #endregion

    #region Script Quenching

    private void QuenchDatabaseObjectsWithCheckpoint(IDbCommand destCmd, List<SqlScript> templateObjects, bool showErrors, DatabaseScriptSlot slot)
    {
        var lastQuenchCount = 0;
        while (lastQuenchCount != templateObjects.Count(s => !s.HasBeenQuenched) && templateObjects.Any(s => !s.HasBeenQuenched))
        {
            lastQuenchCount = templateObjects.Count(s => !s.HasBeenQuenched);
            foreach (var script in templateObjects.Where(s => !s.HasBeenQuenched))
            {
                if (_checkpointing.HasCompletedScript(DbScope, slot.ToString(), script.LogPath))
                {
                    if (showErrors) SafeProgressLog($"    Skipping (previously quenched per checkpoint) {script.LogPath}");
                    script.HasBeenQuenched = true;
                    continue;
                }

                QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
                if (script.HasBeenQuenched)
                {
                    _checkpointing.MarkScriptCompleted(DbScope, slot.ToString(), script.LogPath);
                    if (slot is DatabaseScriptSlot.Object or DatabaseScriptSlot.AfterTablesObject)
                        ChangeAudit?.RecordRan(ObjectScriptClassifier.Classify(script.LogPath), script.LogPath);
                }
            }
        }

        _debugFileLocation = "";
        if (showErrors) LogScriptErrors(templateObjects);
    }

    /// <summary>
    /// Drains this work unit's session-scoped ChangeAudit rows into the run-level capture and marks
    /// the run instrumented (#243 E5). Runs on the connection the 4 table procs wrote on so the
    /// session filter is exact. Best-effort: an audit-read failure must never disrupt the run — a
    /// null return (engine not yet emitting, or the audit table absent) leaves the run honestly
    /// not-instrumented.
    /// </summary>
    private void DrainChangeAudit(IDbCommand auditCmd)
    {
        if (ChangeAudit == null || auditCmd == null) return;
        // Best-effort: ChangeAuditReader swallows DB errors (absent audit table, unreadable) and
        // returns null, leaving the run honestly not-instrumented; the record/mark steps can't throw.
        var rows = ChangeAuditReader.ReadAndDrain(_product.Platform, auditCmd);
        if (rows == null) return;
        foreach (var r in rows) ChangeAudit.Record(r.ObjectType, r.ObjectName, r.Action);
        ChangeAudit.MarkInstrumented();
    }

    internal void QuenchDatabaseObjects(IDbCommand destCmd, List<SqlScript> templateObjects, bool showErrors = true)
    {
        var lastQuenchCount = 0;
        while (lastQuenchCount != templateObjects.Count(s => !s.HasBeenQuenched) && templateObjects.Any(s => !s.HasBeenQuenched))
        {
            lastQuenchCount = templateObjects.Count(s => !s.HasBeenQuenched);
            foreach (var script in templateObjects.Where(s => !s.HasBeenQuenched))
                QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
        }

        _debugFileLocation = "";
        if (showErrors) LogScriptErrors(templateObjects);
    }

    internal void QuenchOneScript(IDbCommand destCmd, SqlScript script, bool runTwice, bool showErrors = true)
    {
        if (_product.Platform == Platform.SqlServer)
            _debugFileLocation = (destCmd.Connection as SqlConnection)?.FireInfoMessageEventOnUserErrors ?? false ? script.LogPath : "";
        else
            _debugFileLocation = showErrors ? script.LogPath : "";

        if (showErrors) SafeProgressLog($"    Quenching {script.LogPath}");
        var needDBReset = false;
        try
        {
            script.CheckForUnresolvedTokens(_databaseName, "", _progressLog.Warn);
            for (var i = 1; i <= (runTwice ? 2 : 1); i++)
                foreach (var batch in script.Batches)
                {
                    needDBReset = needDBReset || batch.ContainsIgnoringCase("USE ");
                    destCmd.CommandText = batch;
                    _infoMessageException = null;
                    destCmd.ExecuteNonQuery();
                    if (_infoMessageException != null) throw _infoMessageException;
                }

            script.HasBeenQuenched = true;
            script.Error = null;
            if (!showErrors) SafeProgressLog($"    Quenched {script.LogPath}");
        }
        catch (Exception ex) when (SentinelClassifier.IsShouldNotApply(ex))
        {
            script.HasBeenQuenched = true;
            script.Error = null;
            script.Outcome = ScriptOutcome.Skipped;
            SafeProgressLog($"    Skipped (ShouldNotApply): {script.LogPath}");
        }
        catch (Exception ex)
        {
            script.Error = ex;
        }
        finally
        {
            if (needDBReset) ResetDb(destCmd);
        }
    }

    private void ResetDb(IDbCommand destCmd)
    {
        try
        {
            if (_product.Platform == Platform.PostgreSQL)
            {
                destCmd.Connection?.ChangeDatabase(_databaseName);
            }
            else
            {
                destCmd.CommandText = QuoteUseDatabase(_databaseName);
                destCmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // ignore error resetting db
        }
    }

    private void QuenchTemplateScriptsWithCheckpoint(IDbCommand destCmd, string slot, List<SqlScript> scripts, DatabaseScriptSlot checkpointSlot)
    {
        var alreadyRan = _trackRunOnceMigrations ? GetCompletedEntriesBySlot(destCmd, slot) : [];
        foreach (var script in scripts.Where(s => !s.HasBeenQuenched))
        {
            if (ShouldAlwaysRun(script.Name) || !alreadyRan.Contains(GetRelativeScriptPath(script.LogPath)))
            {
                if (_checkpointing.HasCompletedScript(DbScope, checkpointSlot.ToString(), script.LogPath))
                {
                    script.HasBeenQuenched = true;
                    SafeProgressLog($"    Skipping (previously quenched per checkpoint) {script.LogPath}");
                    continue;
                }

                QuenchOneScript(destCmd, script, _runScriptsTwice && ShouldAlwaysRun(script.Name));
                if (script.HasBeenQuenched)
                {
                    _checkpointing.MarkScriptCompleted(DbScope, checkpointSlot.ToString(), script.LogPath);
                    MigrationScripts?.Record(_server, _databaseName, _schemaName ?? "", _template.Name, checkpointSlot.ToString(), GetRelativeScriptPath(script.LogPath));
                    if (_trackRunOnceMigrations && !ShouldAlwaysRun(script.Name))
                        MarkScriptCompleted(destCmd, script.LogPath, slot);
                }
            }
            else
            {
                script.HasBeenQuenched = true;
                SafeProgressLog($"    Skipping (previously quenched) {script.LogPath}");
            }
        }

        if (_trackRunOnceMigrations && _pruneObsoleteMigrationTracking)
            RemoveObsoleteCompletedScriptEntries(destCmd, slot, scripts, alreadyRan);

        _debugFileLocation = "";
        LogScriptErrors(scripts);
    }

    private void RemoveObsoleteCompletedScriptEntries(IDbCommand destCmd, string slot, List<SqlScript> scripts, List<string> alreadyRan)
    {
        foreach (var obsoleteScript in alreadyRan.Where(a => IsObsoleteTrackingEntry(a, scripts)))
        {
            destCmd.CommandText = GetDeleteCompletedScriptSql(
                _product.Name, slot, obsoleteScript, _template.Name, DbScope.SchemaName ?? "");
            destCmd.ExecuteNonQuery();
        }
    }

    internal static bool ShouldAlwaysRun(string scriptName) => Path.GetFileNameWithoutExtension(scriptName).EndsWith("[ALWAYS]");

    private List<string> GetCompletedEntriesBySlot(IDbCommand destCmd, string slot)
    {
        var entries = new List<string>();
        try
        {
            destCmd.CommandText = GetSelectCompletedScriptsSql(
                _product.Name, slot, _template.Name, DbScope.SchemaName ?? "");
            using var reader = destCmd.ExecuteReader();
            while (reader.Read())
                entries.Add(reader.GetString(0));
        }
        catch
        {
            // Table may not exist yet (MySQL) or on first run
            return entries;
        }

        return entries;
    }

    private void MarkScriptCompleted(IDbCommand destCmd, string scriptPath, string slot)
    {
        try
        {
            destCmd.CommandText = GetInsertCompletedScriptSql(
                GetRelativeScriptPath(scriptPath), _product.Name, slot,
                _template.Name, DbScope.SchemaName ?? "");
            destCmd.ExecuteNonQuery();
        }
        catch
        {
            // Ignore errors if table doesn't exist or duplicate entry
        }
    }

    internal string GetRelativeScriptPath(string filePath)
    {
        var stripped = LongPathSupport.StripLongPathPrefix(filePath);
        var templateDir = string.IsNullOrEmpty(_template.FilePath)
            ? ""
            : Path.GetDirectoryName(_template.LogPath) ?? "";
        if (templateDir.Length > 0) stripped = stripped.Replace(templateDir, "");
        return stripped
            .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .TrimStart(Path.AltDirectorySeparatorChar);
    }

    #endregion

    #region Error Logging

    private void LogScriptErrors(List<SqlScript> scripts)
    {
        if (scripts.All(x => x.HasBeenQuenched)) return;

        var directory = ResolveArtifactDirectory();
        var failures = new List<ScriptFailure>();
        foreach (var sqlScript in scripts.Where(s => !s.HasBeenQuenched))
        {
            sqlScript.Outcome = ScriptOutcome.Failed;

            string artifactPath = null;
            try
            {
                var header = $"Failed: {_server}.{_databaseName}" +
                             $"{(string.IsNullOrEmpty(_schemaName) ? "" : $" [Schema: {_schemaName}]")}" +
                             $" [{sqlScript.LogPath}] — {sqlScript.Error?.Message}";
                var fileName = GetDebugFileName($"Failed {Path.GetFileNameWithoutExtension(sqlScript.Name)}");
                artifactPath = ResolvedSqlArtifactWriter.WriteFailureArtifact(directory, ScrubArtifactsEnabled,
                    SensitiveTokenValues(), header, sqlScript.Batches, FailingBatchIndex(sqlScript), fileName);
                SafeProgressLogError($"    Resolved SQL written to: {artifactPath}");
                SafeErrorLogError($"Unable to quench '{sqlScript.LogPath}': {sqlScript.Error?.Message} — resolved SQL: {artifactPath}");
            }
            catch (Exception artifactEx)
            {
                SafeProgressLog($"    Could not write resolved-SQL artifact for '{sqlScript.LogPath}': {artifactEx.Message}");
            }

            SafeProgressLogError($"Unable to quench '{sqlScript.LogPath}': {sqlScript.Error?.Message}");
            failures.Add(new ScriptFailure(sqlScript.LogPath, sqlScript.Error?.Message, artifactPath));
        }

        // #338 refinement: carry the per-script specifics so the failure roll-up surfaces the
        // specific error + artifact (parity with mechanical failures) instead of a generic message.
        throw new ScriptQuenchException(failures);
    }

    private static int FailingBatchIndex(SqlScript script) => script.Batches.Count - 1;

    /// <summary>
    /// Shared artifact-on-failure wrap for the two single-statement validation scripts
    /// (BaselineValidationScript, VersionStampScript). Mirrors <see cref="LogScriptErrors"/>'s
    /// header/artifact-write shape but operates on a single already-resolved <c>command.CommandText</c>
    /// rather than a <see cref="SqlScript"/> batch list — an artifact-write failure is swallowed (soft
    /// log only) so it never masks the original exception being rethrown by the caller.
    /// </summary>
    private void WriteValidationFailureArtifact(IDbCommand command, string label, Exception error)
    {
        try
        {
            var header = $"Failed: {_server}.{_databaseName}" +
                         $"{(string.IsNullOrEmpty(_schemaName) ? "" : $" [Schema: {_schemaName}]")}" +
                         $" [{label}] — {error.Message}";
            var fileName = GetDebugFileName($"Failed {label}");
            var path = ResolvedSqlArtifactWriter.WriteFailureArtifact(ResolveArtifactDirectory(), ScrubArtifactsEnabled,
                SensitiveTokenValues(), header, new[] { command.CommandText }, 0, fileName);
            SafeProgressLogError($"    Resolved SQL written to: {path}");
        }
        catch (Exception artifactEx)
        {
            SafeProgressLog($"    Could not write resolved-SQL artifact for '{label}': {artifactEx.Message}");
        }
    }

    /// <summary>
    /// Per-tenant log discipline (design §5.8): when this is a schema-template iteration, every log
    /// line carries a <c>[Schema: &lt;name&gt;]</c> prefix so a 100-iteration deploy log is still
    /// greppable per tenant. Empty schema name (regular template) skips the prefix entirely.
    /// <para>Visible to tests (matches the existing <c>internal Platform Platform</c> /
    /// <c>internal string ProductName</c> / <c>internal string SchemaName</c> pattern) so tests can
    /// assert the prefix shape without reflection.</para>
    /// </summary>
    internal string LogPrefix => string.IsNullOrEmpty(_schemaName)
        ? $"[{_server}].[{_databaseName}]"
        : $"[{_server}].[{_databaseName}] [Schema: {_schemaName}]";

    /// <summary>
    /// Builds the debug script filename used by <see cref="LogSqlScript"/>. For schema-template
    /// iterations the schema name is appended as a suffix so parallel iterations of the same
    /// database write to distinct files — without it, sibling iterations collide on a single
    /// path and hit a Win32 file-sharing violation that throws before the SQL batch executes
    /// (slice-3 audit bug B2). Regular templates leave <c>_schemaName</c> empty, so the
    /// suffix is omitted and the pre-slice-3 filename shape is preserved.
    /// </summary>
    internal string GetDebugFileName(string label)
    {
        var schemaSuffix = string.IsNullOrEmpty(_schemaName) ? "" : $".{_schemaName}";
        return $"SchemaQuench - {label} {_server}.{_databaseName}{schemaSuffix}.sql";
    }

    private void SafeProgressLog(string msg)
    {
        lock (_lockObject)
        {
            _progressLog.Info($"{LogPrefix} {msg}");
            FailureCtx.Log(msg);
        }
    }

    private void SafeProgressLogError(string msg)
    {
        lock (_lockObject)
        {
            _progressLog.Error($"{LogPrefix} {msg}");
            FailureCtx.Log(msg);
        }
    }

    private void SafeErrorLogError(string msg)
    {
        lock (_lockObject) _errorLog.Error($"{LogPrefix} {msg}");
    }

    #endregion

    #region Platform Message Handlers

    private void OnSqlServerInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        foreach (SqlError err in e.Errors)
        {
            if (err.Class > 10)
            {
                // Preserve the error number so deadlock detection (1205) keys on the code, not
                // the locale-dependent message. Errors surfaced here (severity ≤ 16) are never
                // thrown as SqlException on this connection (FireInfoMessageEventOnUserErrors).
                _infoMessageException = new SqlServerErrorException(err.Number, err.Message);

                // Deadlock victims (1205) are recoverable: ExecuteNonQueryHandlingMessages retries
                // the idempotent convergence proc. Don't hard-log them here — otherwise a deadlock
                // that the retry *recovers* still surfaces as a scary error (and unlike PostgreSQL,
                // where the deadlock is thrown and caught silently by the retry loop, SQL Server
                // delivers it through this InfoMessage handler before the retry loop sees it). If
                // retries exhaust, the propagated exception is logged by the caller's per-iteration
                // failure handling; the retry loop logs an Info note on each recoverable attempt.
                if (err.Number == 1205) continue;

                SafeProgressLogError(err.Message);
                if (!string.IsNullOrWhiteSpace(_debugFileLocation))
                {
                    SafeProgressLogError("");
                    SafeProgressLogError($"Resolved SQL written to: {_debugFileLocation}");
                }

                SafeErrorLogError("");
                SafeErrorLogError(err.Message);
                SafeErrorLogError($"  at Line: {err.LineNumber}");
                SafeErrorLogError("");
            }
            else if (_product != null)
            {
                var verboseLogging = FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.VerboseLogging]?.ToLower() == "true";
                if (verboseLogging || err.State == 100)
                    SafeProgressLog($"      {err.Message}");
            }
        }
    }

    private void OnPostgreSqlNotice(object sender, NpgsqlNoticeEventArgs e)
    {
        if (e.Notice.MessageText.EndsWith("does not exist, skipping") || e.Notice.MessageText.EndsWith("already exists, skipping")) return;
        SafeProgressLog($"      {e.Notice.MessageText}");
    }

    #endregion

    #region WhatIf Logging

    // Console verbosity for the WhatIf per-script lines (--WhatIfDetail). Default 'normal' preserves
    // the one-line-per-script output; 'concise' collapses each section to per-category counts. The
    // WhatIf?.Record(...) calls below are unconditional — the summary file is never affected by this.
    private static WhatIfDetail WhatIfConsoleDetail =>
        WhatIfConsoleFormatter.ParseDetail(CommandLineParser.ValueOfSwitch("WhatIfDetail", "normal"));

    // Callers pass overlapping lists across the two Object/AfterTablesObject WhatIf calls per
    // scope (mirroring the real path's two QuenchDatabaseObjectsWithCheckpoint passes), so this
    // gates on HasBeenQuenched exactly as the real path does — a script already logged here in an
    // earlier call is skipped, and each script is flagged once it's logged.
    internal void WhatIfLogScripts(List<SqlScript> scripts, DatabaseScriptSlot slot)
    {
        var entries = new List<WhatIfConsoleEntry>();
        foreach (var script in scripts.Where(s => !s.HasBeenQuenched))
        {
            entries.Add(new WhatIfConsoleEntry("apply", "APPLY", script.LogPath));
            WhatIf?.Record(WhatIfCategory.Apply, LogPrefix, script.LogPath);
            script.HasBeenQuenched = true;
        }
        foreach (var line in WhatIfConsoleFormatter.Render(entries, WhatIfConsoleDetail))
            SafeProgressLog(line);
    }

    private void WhatIfLogTableDataScripts(List<SqlScript> scripts)
    {
        var entries = new List<WhatIfConsoleEntry>();
        foreach (var script in scripts)
        {
            entries.Add(new WhatIfConsoleEntry("deliver", "DELIVER", Path.GetFileNameWithoutExtension(script.LogPath)));
            WhatIf?.Record(WhatIfCategory.Deliver, LogPrefix, script.LogPath);
        }
        foreach (var line in WhatIfConsoleFormatter.Render(entries, WhatIfConsoleDetail))
            SafeProgressLog(line);
    }

    private void WhatIfLogTemplateScripts(IDbCommand destCmd, string slot, List<SqlScript> scripts, DatabaseScriptSlot checkpointSlot)
    {
        var alreadyRan = _trackRunOnceMigrations ? GetCompletedEntriesBySlot(destCmd, slot) : [];
        var entries = new List<WhatIfConsoleEntry>();
        foreach (var script in scripts)
        {
            if (!ShouldAlwaysRun(script.Name) && alreadyRan.Contains(GetRelativeScriptPath(script.LogPath)))
            {
                entries.Add(new WhatIfConsoleEntry("skip", "SKIP (previously quenched)", script.LogPath));
                WhatIf?.Record(WhatIfCategory.Skip, LogPrefix, script.LogPath);
            }
            else
            {
                entries.Add(new WhatIfConsoleEntry("apply", "APPLY", script.LogPath));
                WhatIf?.Record(WhatIfCategory.Apply, LogPrefix, script.LogPath);
            }
        }
        foreach (var line in WhatIfConsoleFormatter.Render(entries, WhatIfConsoleDetail))
            SafeProgressLog(line);
    }

#endregion
}
