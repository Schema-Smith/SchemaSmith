// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

    // Where this unit's changes are made, carried on every row it records.
    private ChangeTarget AuditTarget => new(_server, _databaseName, string.IsNullOrEmpty(_schemaName) ? null : _schemaName);

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

    /// <summary>
    /// MariaDB only. <c>KEEP</c> opts into altering a system-versioned table; the engine then applies the
    /// DDL to the stored history as well, rewriting it to a shape it never had. Anything else (including
    /// unset) leaves the engine default, which refuses such a change -- so the conservative answer needs
    /// no configuration and the destructive one has to be asked for.
    /// </summary>
    private string SystemVersioningAlterHistory =>
        EscapeSqlLiteral((FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.SystemVersioningAlterHistory]
                          ?? "").Trim().ToUpperInvariant());

    // #417. NULL is the unmanaged contract, so an unset default is the SQL literal, never '' -- an empty string
    // names a filegroup that does not exist and would fail every CDC table in the template.
    private string TemplateCdcFilegroup =>
        string.IsNullOrWhiteSpace(_template?.CdcFilegroup) ? "NULL" : $"N'{EscapeSqlLiteral(_template.CdcFilegroup.Trim())}'";

    // #426. NULL keeps the unset contract (off for a new table, unchanged on rotation).
    private string TemplateCdcSupportsNetChanges =>
        _template?.CdcSupportsNetChanges is { } v ? (v ? "1" : "0") : "NULL";

    /// <summary>OFF, PREFER or REQUIRE; a table's own OnlineApply overrides it in the procedures.</summary>
    internal static string ResolveOnlineApply(string configured)
    {
        var value = (configured ?? "").Trim();
        if (value.Length == 0) return "OFF";
        return value.ToUpperInvariant() switch
        {
            "OFF" or "PREFER" or "REQUIRE" => value.ToUpperInvariant(),
            _ => throw new InvalidOperationException($"OnlineApply must be Off, Prefer or Require, not '{value}'.")
        };
    }

    /// <summary>The SQL literal NULL when unset: no resumable build.</summary>
    internal static string ResolveOnlineApplyMaxDuration(string configured)
    {
        var value = (configured ?? "").Trim();
        if (value.Length == 0) return "NULL";
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10080)
            return minutes.ToString(CultureInfo.InvariantCulture);
        throw new InvalidOperationException($"OnlineApplyMaxDuration must be a whole number of minutes from 1 to 10080, not '{value}'.");
    }

    private string OnlineApply =>
        ResolveOnlineApply(FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.OnlineApply]);

    private string OnlineApplyMaxDuration =>
        ResolveOnlineApplyMaxDuration(FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.OnlineApplyMaxDuration]);

    /// <summary>NEVER when no tier declared a policy — the domain object's own default.</summary>
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
    private bool _skipUnchangedObjects = true; // Target:SkipUnchangedObjects (default true)
    private readonly HashSet<string> _tablesChangedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tablesChangedGate = new();
    private bool _stampWriteFailureLogged;
    private string _unsupportedFeaturePolicy; // Target:UnsupportedFeaturePolicy (warn | fail, default warn); general all-engine policy, not just the MySQL data-delivery gate

    // A1: the per-target version script tokens ({{ServerMajorVersion}} / {{CompatibilityLevel}}),
    // built from the detected TargetVersionInfo in Phase B (post-connection) and applied wherever
    // script tokens resolve — folder/component/sentinel ShouldApplyExpression and script bodies. Null
    // until PrepareVersionScriptTokens runs (test-only entry points that bypass Execute).
    private List<KeyValuePair<string, string>> _versionScriptTokens;

    // Per-iteration content built by PrepareIterationContent at the start of Execute(). Every iteration
    // gets its own clones of the script collections, because a script's HasBeenQuenched and its
    // token-substituted batches belong to one database: shared across the databases a regular template
    // fans out to, a script applied in one was skipped in the rest, and the first database's query-token
    // values reached them all (#475). Schema-template iterations also carry the substituted schema in the
    // table / view JSON; for regular templates those strings stay null so the accessor properties below
    // fall back to _template.<field>, which also lets test-only entry points that call the Quench*
    // methods directly (bypassing Execute → PrepareIterationContent) keep working.
    private readonly IterationContent _iteration = new();

    // Relative paths of scripts in folders skipped by a ShouldApplyExpression this iteration. A
    // gated-off folder's script is still declared in the package (just not applicable to this
    // target), so its run-once migration tracking row must NOT be pruned as obsolete — mirrors how
    // an active Target filter narrows pruning scope and how a gated-out object isn't dropped.
    private readonly HashSet<string> _gatedOffMigrationPaths = new();

    private sealed class IterationContent
    {
        public List<SqlScript> SetupScripts { get; set; } = [];
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
    internal List<SqlScript> IterationSetupScripts => _iteration.SetupScripts;
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

        // Gating runs before any script does, so re-copying from the template loses no progress.
        var copy = IterationCopier(string.IsNullOrEmpty(_schemaName)
            ? []
            : new List<KeyValuePair<string, string>> { new("SchemaName", _schemaName) });
        _iteration.SetupScripts = copy(Scripts(_template.SetupFolders));
        _iteration.BeforeScripts = copy(Scripts(_template.BeforeFolders));
        _iteration.ObjectScripts = copy(Scripts(_template.ObjectFolders));
        _iteration.AfterTablesObjectScripts = copy(Scripts(_template.AfterTablesObjectFolders));
        _iteration.BetweenTablesAndKeysScripts = copy(Scripts(_template.BetweenTablesAndKeysFolders));
        _iteration.AfterTableScripts = copy(Scripts(_template.AfterTableFolders));
        _iteration.TableDataScripts = copy(Scripts(_template.TableDataFolders));
        _iteration.AfterScripts = copy(Scripts(_template.AfterFolders));
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
        // #576: the previous run's debug files for this target go, so only the current failure's are there.
        ResolvedSqlArtifactWriter.DeleteOwned(ResolveArtifactDirectory(), o =>
            o.Product == _product.Name && o.Server == _server && o.Database == _databaseName
            && o.Schema == (_schemaName ?? "") && o.Run != ResolvedSqlArtifactWriter.RunId);

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
                // The table work is what writes the audit; a run that updates no tables (the datafix profile) need not have
                // rights on SchemaSmith's own tables, so it neither reads them nor records the expression map.
                if (ChangeAudit != null && _updateTables) ChangeAuditReader.DiscardStale(_product.Platform, effectiveTableCmd);

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
                    _skipUnchangedObjects = !string.Equals(FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.SkipUnchangedObjects], "false", StringComparison.OrdinalIgnoreCase);
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
                            // Say which encoding was chosen and whether a setting forced it. SchemaTongs
                            // names the encoding per object as it extracts; the deploy side resolved it
                            // silently, so an operator who set Target:CompatEncoding had no confirmation it
                            // took effect -- and the encoding determines which helper set gets kindled.
                            SafeProgressLog($"  [{_databaseName}] model ingest encoding: {_ingestEncoding}" +
                                            (string.IsNullOrWhiteSpace(compatEncodingOverride)
                                                ? " (auto)"
                                                : $" (forced by Target:CompatEncoding={compatEncodingOverride})"));
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
                            // On SQL Server the script's own error arrives as a message, leaving a null that read as "no".
                            _infoMessageException = null;
                            var isValid = Convert.ToBoolean(command.ExecuteScalar());
                            if (_infoMessageException != null) throw _infoMessageException;
                            if (!isValid)
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

                // Step: Setup (D13). Database settings and the like that the objects and later scripts rely on: after the
                // helpers and the baseline check, before the first object. Every deploy, one pass, in order; the first error
                // fails the deploy, since nothing ahead of these scripts can be missing.
                if (_iteration.SetupScripts.Count > 0)
                {
                    if (!IsWhatIf)
                    {
                        SafeProgressLog("  Quenching Setup scripts");
                        QuenchSetupScripts(command, _iteration.SetupScripts);
                    }
                    else
                    {
                        SafeProgressLog("  [WhatIf] Setup scripts:");
                        WhatIfLogSetupScripts(_iteration.SetupScripts);
                    }
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
                    WhatIfLogScripts(nonTokenScripts, DatabaseScriptSlot.Object, effectiveObjectsCmd);
                }

                // Step: Domain types (PostgreSQL only). Before tables for the same reason enum types are:
                // a column can be OF a domain, so the domain has to exist before the table that uses it.
                if (_product.Platform == Platform.PostgreSQL && _template.DomainTypes.Count > 0)
                {
                    var domainTypeSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "DomainTypeQuench", () => QuenchDomainTypes(effectiveTableCmd));
                    domainTypeSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "DomainTypeQuench", domainTypeSw.ElapsedMilliseconds, 0);
                }

                // Step: Enum types (PostgreSQL only). BEFORE tables, deliberately: a column can be OF an
                // enum type, so the type has to exist before the table that uses it -- and a value the
                // package adds has to be present before a default or check constraint can reference it.
                if (_product.Platform == Platform.PostgreSQL && _template.EnumTypes.Count > 0)
                {
                    var enumTypeSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "EnumTypeQuench", () => QuenchEnumTypes(effectiveTableCmd));
                    enumTypeSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "EnumTypeQuench", enumTypeSw.ElapsedMilliseconds, 0);
                }

                // Step: Sequences (PostgreSQL only). Also before tables -- a column DEFAULT can call
                // nextval() on one.
                if ((_product.Platform == Platform.PostgreSQL && _template.Sequences.Count > 0) || (_product.Platform == Platform.MariaDb && _template.MariaDbSequences.Count > 0))
                {
                    var sequenceSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "SequenceQuench", () => QuenchSequences(effectiveTableCmd));
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
                    // Said before the change, so a column whose existing NULLs refuse it fails with its reason in view.
                    foreach (var notice in _template.LoadNotices)
                        lock (_lockObject) _progressLog.Warn($"{LogPrefix} {notice.Message}");
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
                    WhatIfLogScripts(_iteration.ObjectScripts.Where(s => s.Batches.All(b => !b.Contains("{{") && !b.Contains("}}"))).ToList(), DatabaseScriptSlot.Object, effectiveObjectsCmd);

                    if (_template.QueryTokens.Count > 0)
                        SafeProgressLog("  [WhatIf] Would resolve template query tokens");

                    SafeProgressLog("  [WhatIf] Before database scripts:");
                    WhatIfLogTemplateScripts(command, "Before", _iteration.BeforeScripts, DatabaseScriptSlot.Before);
                }

                if (_product.Platform.GetBasePlatform() == Platform.SqlServer && DeferDatabaseToggles
                    && !_template.IndexOnlyTableQuenches && _updateTables)
                {
                    QuenchDeferredDatabaseToggles(effectiveTableCmd);
                }

                // Step: Modified tables
                if (!_template.IndexOnlyTableQuenches && _updateTables)
                {
                    var modifiedTablesSw = Stopwatch.StartNew();
                    StageDeclaredModules(effectiveTableCmd);
                    _checkpointing.Track(DbScope, "ModifiedTables", () => QuenchModifiedTables(effectiveTableCmd));
                    modifiedTablesSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "ModifiedTables", modifiedTablesSw.ElapsedMilliseconds, 0);
                    if (!IsWhatIf) RequeueModulesTheTableQuenchDropped(effectiveTableCmd);
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
                    WhatIfLogScripts(_iteration.AfterTablesObjectScripts, DatabaseScriptSlot.AfterTablesObject, effectiveObjectsCmd);

                    SafeProgressLog("  [WhatIf] Between table and keys scripts:");
                    WhatIfLogTemplateScripts(command, "Between Table And Keys", _iteration.BetweenTablesAndKeysScripts, DatabaseScriptSlot.BetweenTablesAndKeys);
                }

                // Step: MariaDB VECTOR indexes whose M or DISTANCE changed. MariaDB has no ALTER for either, and the options are
                // only in SHOW CREATE TABLE, which a procedure cannot read: the index is dropped here, and the indexes step
                // builds it again as declared.
                if (_product.Platform == Platform.MariaDb && _updateTables && _mySqlServerVersionNum >= 1107)
                    RebuildChangedVectorIndexes(effectiveTableCmd);

                // Step: Indexes and constraints
                if (_updateTables)
                {
                    var indexesAndConstraintsSw = Stopwatch.StartNew();
                    _checkpointing.Track(DbScope, "IndexesAndConstraints", () => QuenchIndexesAndConstraints(effectiveTableCmd));
                    indexesAndConstraintsSw.Stop();
                    RunTiming?.Record(LogPrefix, _databaseName, "IndexesAndConstraints", indexesAndConstraintsSw.ElapsedMilliseconds, 0);
                }

                // Step: SQL Server table features. Not in index-only mode: those templates do not own the tables.
                if (_product.Platform.GetBasePlatform() == Platform.SqlServer && !_template.IndexOnlyTableQuenches && _updateTables)
                    _checkpointing.Track(DbScope, "TableFeatures", () => QuenchSqlServerTableFeatures(effectiveTableCmd));

                // #242: record what each expression-bearing object was applied with, AFTER the create passes
                // above, so an object created on this run is recorded on this run rather than churning once more.
                // Under WhatIf it records nothing and puts back what the #508 scratch-copy check verified for the preview.
                if (_updateTables) RecordExpressionMap(effectiveTableCmd);

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
                                RecordDowngrade = (type, name) => ChangeAudit?.Record(type, name, "downgraded", target: AuditTarget),
                                RecordDelivery = RecordDataDelivery,
                                WhatIf = IsWhatIf,
                                PostgreSqlServerVersionNum = _postgreSqlServerVersionNum,
                                MySqlServerVersionNum = _mySqlServerVersionNum,
                                SqlServerCompatibilityLevel = _sqlServerCompatibilityLevel,
                                UnsupportedFeaturePolicy = _unsupportedFeaturePolicy,
                                WriteResolvedSqlArtifact = (label, sql) =>
                                {
                                    if (!DebugSql.Enabled)
                                    {
                                        SafeProgressLogError($"    {DebugSql.HowToGetIt(ResolveArtifactDirectory())}");
                                        _debugSqlHintLogged = true;
                                        return;
                                    }
                                    try
                                    {
                                        var path = ResolvedSqlArtifactWriter.WriteFailureArtifact(
                                            ResolveArtifactDirectory(), DebugSqlOwner,
                                            $"Failed data delivery: {_server}.{_databaseName}" +
                                            $"{(string.IsNullOrEmpty(_schemaName) ? "" : $" [Schema: {_schemaName}]")} [{label}]",
                                            new List<string> { sql }, failingBatchIndex: 0, label,
                                            safeLabel => GetDebugFileName($"Failed DataDelivery {safeLabel}"));
                                        SafeProgressLogError($"    Resolved SQL written to: {path}");
                                        _dataDeliveryArtifact ??= path;
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
                        _checkpointing.Track(DbScope, "ForeignKeys", () => QuenchForeignKeys(effectiveTableCmd));
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
                    if (ShouldQuenchEvents(_product.Platform, _template.Events.Count, DropRemovedEvents))
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
                        ReapplyStashedIndexedViewPermissions(effectiveTableCmd);
                        indexedViewQuenchSw.Stop();
                        RunTiming?.Record(LogPrefix, _databaseName, "IndexedViewQuench", indexedViewQuenchSw.ElapsedMilliseconds, 0);
                    }

                    ReapplyPreservedModulePermissions(effectiveTableCmd);

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
                    WhatIfLogScripts(_iteration.AfterTablesObjectScripts.ToList(), DatabaseScriptSlot.AfterTablesObject, effectiveObjectsCmd);

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
                            Platform = _product.Platform.GetBasePlatform().ToString(),
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
                            RecordDowngrade = (type, name) => ChangeAudit?.Record(type, name, "downgraded", target: AuditTarget),
                            RecordDelivery = RecordDataDelivery,
                            WhatIf = true
                        });
                    }

                    // The same steps as the real run, in the same order. Each procedure takes the WhatIf flag and
                    // records what it would do instead of doing it; skipping the call left those changes out of the
                    // preview entirely.
                    if (!_template.IndexOnlyTableQuenches && _updateTables)
                        QuenchForeignKeys(effectiveTableCmd);

                    if (_product.Platform == Platform.PostgreSQL && _template.MaterializedViews.Count > 0)
                        QuenchMaterializedViews(effectiveTableCmd);

                    if (ShouldQuenchEvents(_product.Platform, _template.Events.Count, DropRemovedEvents))
                        QuenchEvents(effectiveTableCmd);

                    if (_product.Platform == Platform.SqlServer && _template.IndexedViews.Count > 0)
                        QuenchIndexedViews(effectiveTableCmd);

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
                if (_updateTables) DrainChangeAudit(tableCommand ?? command);
                // MySQL parses the table JSON into session-scoped temp tables that every table step
                // consumes, so the drop belongs AFTER the last consumer -- foreign keys -- not between
                // two of them. It used to run right after the index quench, which meant ForeignKeys
                // found the tables gone and re-parsed the whole payload: measured as a second
                // `CALL SchemaSmith_ParseTableJson` costing ~2.4s on an 11-table demo package, to feed a
                // ForeignKeyQuench that then took 3ms. Cleaning up here instead is also the stronger
                // guarantee -- it runs exactly once per work unit, on the exception path too, so a
                // pooled connection can never carry one unit's model into the next.
                if (_product.Platform.GetBasePlatform() == Platform.MySQL)
                    CleanupMySqlTempTables(command);
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
            else if (!DebugSql.Enabled && e is not ScriptQuenchException && !_debugSqlHintLogged)
                SafeProgressLogError(DebugSql.HowToGetIt(ResolveArtifactDirectory()));

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
                    string.IsNullOrWhiteSpace(_debugFileLocation) ? _dataDeliveryArtifact : _debugFileLocation);
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
    /// For <b>regular templates</b>: every script collection is cloned, with no
    /// substitution, so each database the template fans out to has its own
    /// <c>HasBeenQuenched</c> state and its own query-token values (#475).
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
            // Regular templates copy too: each database a template fans out to runs every script, with
            // its own query-token values (#475). Nothing to substitute here.
            var own = IterationCopier([]);
            _iteration.SetupScripts = own(_template.SetupScripts);
            _iteration.BeforeScripts = own(_template.BeforeScripts);
            _iteration.ObjectScripts = own(_template.ObjectScripts);
            _iteration.AfterTablesObjectScripts = own(_template.AfterTablesObjectScripts);
            _iteration.BetweenTablesAndKeysScripts = own(_template.BetweenTablesAndKeysScripts);
            _iteration.AfterTableScripts = own(_template.AfterTableScripts);
            _iteration.TableDataScripts = own(_template.TableDataScripts);
            _iteration.AfterScripts = own(_template.AfterScripts);
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

        var copy = IterationCopier(schemaNameTokens);
        _iteration.SetupScripts = copy(_template.SetupScripts);
        _iteration.BeforeScripts = copy(_template.BeforeScripts);
        _iteration.ObjectScripts = copy(_template.ObjectScripts);
        _iteration.AfterTablesObjectScripts = copy(_template.AfterTablesObjectScripts);
        _iteration.BetweenTablesAndKeysScripts = copy(_template.BetweenTablesAndKeysScripts);
        _iteration.AfterTableScripts = copy(_template.AfterTableScripts);
        _iteration.TableDataScripts = copy(_template.TableDataScripts);
        _iteration.AfterScripts = copy(_template.AfterScripts);

        _iteration.BaselineValidationScript = SqlScript.TokenReplace(
            _template.BaselineValidationScript ?? "", schemaNameTokens, _product.Platform);
        _iteration.VersionStampScript = SqlScript.TokenReplace(
            _template.VersionStampScript ?? "", schemaNameTokens, _product.Platform);

        // Engine-generated DDL (MissingTableAndColumnQuench, IndexOnlyQuench, MaterializedViewQuench)
        // consumes the serialized table-definition JSON literally. Slice 1's SchemaDefaultResolver
        // defaults schema-template tables / views to "{{SchemaName}}", which means the JSON carries
        // the token verbatim — substitute here so each iteration sees a fully-qualified DDL payload.
        //
        // EVERY MODEL PAYLOAD BELONGS IN THIS LIST -- it must stay in step with the version-token
        // block in ApplyVersionTokens, which already covers all seven. A payload resolved to the
        // token and never substituted is WORSE than one never resolved: instead of a null Schema
        // that each quench COALESCEs to the platform default, the engine is handed the literal
        // "{{SchemaName}}" as a schema name. Domain types were missing here, which is exactly that,
        // and it went unnoticed because the five neighbours were right.
        _iteration.TableSchema = (_template.TableSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.MaterializedViewSchema = (_template.MaterializedViewSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.IndexedViewSchema = (_template.IndexedViewSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.EventSchema = (_template.EventSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.DomainTypeSchema = (_template.DomainTypeSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.EnumTypeSchema = (_template.EnumTypeSchema ?? "").Replace("{{SchemaName}}", _schemaName);
        _iteration.SequenceSchema = (_template.SequenceSchema ?? "").Replace("{{SchemaName}}", _schemaName);
    }

    // This iteration's own copy of each template script, made once and shared by every slot list the script is in.
    // The Objects folders are also in AfterTablesObjectFolders, and the after-tables pass re-runs only what the first
    // pass did not apply -- copied per list, every object script ran twice.
    private static Func<List<SqlScript>, List<SqlScript>> IterationCopier(List<KeyValuePair<string, string>> tokens)
    {
        var copies = new Dictionary<SqlScript, SqlScript>();
        return source => source.Select(script =>
        {
            if (copies.TryGetValue(script, out var copy)) return copy;
            copy = script.Clone();
            copy.ReplaceQueryTokens(tokens);
            copies[script] = copy;
            return copy;
        }).ToList();
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

        _iteration.SetupScripts = SubstituteVersionTokens(_iteration.SetupScripts);
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
        // The iteration's scripts are its own copies (IterationCopier), so substitute in place: a copy made here
        // per list would split a script that is in two slot lists.
        if (scripts == null || !scripts.Any(s => s.Batches.Any(ContainsVersionToken)))
            return scripts;
        foreach (var script in scripts)
            script.ReplaceQueryTokens(_versionScriptTokens);
        return scripts;
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
            ChangeAudit?.Record("schema", _schemaName, IsWhatIf ? "wouldCreate" : "created", target: AuditTarget);
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
    /// Query tokens are resolved into a per-iteration dictionary (NOT the shared
    /// <c>_template.QueryTokens</c>) and applied to this iteration's own cloned scripts, so each
    /// database a regular template fans out to gets its own values (#475).
    /// </para>
    /// <para>
    /// Schema templates additionally have iteration-scoped tokens
    /// (those whose body references <c>{{SchemaName}}</c> directly or transitively per
    /// <see cref="Template.IsIterationScoped"/>) having <c>{{SchemaName}}</c> substituted in
    /// their bodies first. The resolved per-iteration map is then applied to the cloned
    /// scripts owned by this iteration.
    /// </para>
    /// </summary>
    private void ResolveAndApplyQueryTokens(IDbCommand silentCmd)
    {
        if (_template.QueryTokens.Count == 0) return;

        // Resolve into a per-iteration copy so every iteration of the template -- each database of a regular
        // template, each schema of a schema template -- starts from the template's pristine <*Query*> bodies.
        // Resolving into the shared dictionary gave every later database the first one's values (#475).
        var isSchemaIteration = !string.IsNullOrEmpty(_schemaName);
        SafeProgressLog(isSchemaIteration ? "  Resolving template query tokens (per-iteration)" : "  Resolving template query tokens");

        // 1. Build the per-iteration NonQueryTokens list — any iteration-scoped non-query token
        //    body (one containing {{SchemaName}} directly or transitively) needs the substitution
        //    applied BEFORE it's fed into ResolveQueryTokens (where it's inlined into <*Query*>
        //    bodies). Other non-query tokens pass through unchanged.
        var iterationNonQueryTokens = _template.NonQueryTokens
            .Select(kv =>
            {
                if (isSchemaIteration && _template.IsIterationScoped(kv.Key) && !string.IsNullOrEmpty(kv.Value))
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
            if (isSchemaIteration && _template.IsIterationScoped(kv.Key) && !string.IsNullOrEmpty(body))
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
        ApplyToIteration(_iteration.SetupScripts, tokenList);
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
        var configured = FactoryContainer.Resolve<IConfigurationRoot>()?[SettingsKeys.ArtifactPath];
        return string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured;
    }

    private string DebugSqlOwner => ResolvedSqlArtifactWriter.OwnerLine(_product.Name, _server, _databaseName, _schemaName);

    #endregion

    #region Platform-Specific Table Quench SQL

    internal void QuenchMissingTablesAndColumns(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Tables.Count == 0)
            return;

        SafeProgressLog("  Quenching missing tables and columns");
        using var timestampDefaults = PinExplicitTimestampDefaults(tableCommand);

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
            {
                var updateFillFactor = _template.UpdateFillFactor ? "1" : "0";
                if (_ingestEncoding == IngestEncoding.Xml)
                {
                    // The XML twin still builds its working set with SELECT ... INTO, so it has no point
                    // at which the temp tables exist but are empty and cannot be split. It keeps the
                    // inlined payload. See the header of ParseTableXmlIntoTempTables.sql.
                    tableCommand.CommandText = $@"
DECLARE @TableDefinitions XML = '{EscapeSqlLiteral(IterationTableXml)}',
        @UpdateFillFactor BIT = {updateFillFactor}
{ForgeKindler.GetParseTableXmlScript(Platform.SqlServer)}
{SqlServerCdcRotateTable}
{SqlServerExpressionMapAdoptUndoTable}
{SqlServerPreflight}
{SqlServerExpressionMapAdopt}EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingTableAndColumnQuench @WhatIf = {_whatIfOnly}";
                    break;
                }

                PrepareSqlServerTableIngest(tableCommand, updateFillFactor);
                break;
            }
            case Platform.PostgreSQL:
            {
                // The shred lives in a procedure so the model can be an argument. As an anonymous DO
                // block it had to be escaped into the statement text, which made a large product send
                // tens of megabytes of JSON for PostgreSQL to parse as SQL on every work unit. The temp
                // tables it builds are session-scoped either way, so every later step still reads them.
                PrepareParameterizedTableJson(tableCommand,
                    $@"CALL ""SchemaSmith"".""ParseTableJson""(@tableJson, {_template.UpdateFillFactor.ToString().ToLower()});",
                    IterationTableSchema, "Parse Table Json");

                // Ownership first, so a refusal leaves another product's table as it was. #508: adoption before any table
                // or column is created, so only what already exists is checked on a scratch copy.
                tableCommand.CommandText = $@"CALL ""SchemaSmith"".""ValidateTableOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
CALL ""SchemaSmith"".""ExpressionMapAdopt""(p_WhatIf := {_whatIfOnly}); CALL ""SchemaSmith"".""MissingTableAndColumnQuench""(p_WhatIf := {_whatIfOnly})";
                break;
            }
            case Platform.MySQL:
            {
                ParseMySqlTableJson(tableCommand);
                SetMySqlOnlineApply(tableCommand);
                var whatIf = _whatIfOnly == "1" ? 1 : 0;
                // Ownership first, so a refusal leaves another product's table as it was. #508: adoption before any table or
                // column is created (skipped under WhatIf inside the procedure: its copy is a real table).
                tableCommand.CommandText = $"CALL SchemaSmith_ValidateTableOwnership('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_databaseName)}'); CALL SchemaSmith_ExpressionMapAdopt('{EscapeSqlLiteral(_databaseName)}', {whatIf}); CALL SchemaSmith_MissingTableAndColumnQuench('{EscapeSqlLiteral(_databaseName)}', {whatIf})";
                break;
            }
        }

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Missing Tables And Columns"), ReadsTheParsedTables(tableCommand.CommandText));
        try
        {
            ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        }
        finally
        {
            // The SQL Server JSON path leaves the payload bound here; the command is pooled and reused by
            // every step after this one, so a leftover parameter would follow it around -- including down
            // the error path, where the next thing to touch the command is the failure handling.
            ClearParameters(tableCommand);
        }
        _debugFileLocation = "";
    }

    internal void QuenchModifiedTables(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() == Platform.MySQL && _template.Tables.Count == 0)
            return;

        SafeProgressLog("  Quenching modified tables");
        using var timestampDefaults = PinExplicitTimestampDefaults(tableCommand);

        switch (_product.Platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                tableCommand.CommandText = $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ModifiedTableQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @DropUnknownIndexes = {_dropUnknownIndexes}, @WhatIf = {_whatIfOnly}, @DropTablesRemovedFromProduct = {_dropRemovedTables}, @DropColumnsRemovedFromProduct = {_dropRemovedColumns}, @DropForeignKeysRemovedFromProduct = {_dropRemovedForeignKeys}, @DropCheckConstraintsRemovedFromProduct = {_dropRemovedCheckConstraints}, @DropExcludeConstraintsRemovedFromProduct = {_dropRemovedExcludeConstraints}, @DropStatisticsRemovedFromProduct = {_dropRemovedStatistics}, @DropIndexesRemovedFromProduct = {_dropRemovedIndexes}, @CaptureWouldDrop = {FormatBooleanFlag(CaptureWouldDrop)}, @RebuildPolicyMode = '{RebuildPolicyMode}', @RebuildPolicyThreshold = {RebuildPolicyThreshold}, @RebuildPolicyOnOrderMismatch = {RebuildPolicyOnOrderMismatch}, @DropSchemaBoundDependents = {(DropSchemaBoundDependents ? 1 : 0)}, @OnlineApply = '{OnlineApply}', @OnlineApplyMaxDuration = {OnlineApplyMaxDuration}";
                break;
            case Platform.PostgreSQL:
                tableCommand.CommandText = $@"
CALL ""SchemaSmith"".""ModifiedTableQuench""(p_DropUnknownIndexes := {_dropUnknownIndexes}, p_WhatIf := {_whatIfOnly}, p_DropTablesRemovedFromProduct := {_dropRemovedTables}, p_DropColumnsRemovedFromProduct := {_dropRemovedColumns}, p_DropForeignKeysRemovedFromProduct := {_dropRemovedForeignKeys}, p_DropCheckConstraintsRemovedFromProduct := {_dropRemovedCheckConstraints}, p_DropExcludeConstraintsRemovedFromProduct := {_dropRemovedExcludeConstraints}, p_DropStatisticsRemovedFromProduct := {_dropRemovedStatistics}, p_DropIndexesRemovedFromProduct := {_dropRemovedIndexes}, p_CaptureWouldDrop := {FormatBooleanFlag(CaptureWouldDrop)}, p_RebuildPolicyMode := '{RebuildPolicyMode}', p_RebuildPolicyThreshold := {RebuildPolicyThreshold}, p_RebuildPolicyOnOrderMismatch := {RebuildPolicyOnOrderMismatch}, p_OnlineApply := '{OnlineApply}');";
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
                SetMySqlOnlineApply(tableCommand);
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

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Modified Tables"), ReadsTheParsedTables(tableCommand.CommandText));
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
        RunDeferredOnlineWork(tableCommand);
    }

    // #242. The mapping is an optimisation over a working comparison: failing to record must never fail a deploy
    // whose changes are already applied, so this logs and carries on. A missing row costs one more comparison
    // next run, which is exactly today's behaviour.
    // Owned by the session, not by ModifiedTableQuench: that procedure decides the CDC rotations, and CdcQuench applies
    // them later, once every column exists (#420). A temp table created inside a procedure dies when it returns.
    private const string SqlServerCdcRotateTable = "IF OBJECT_ID('tempdb..#CdcRotate') IS NOT NULL DROP TABLE #CdcRotate\n"
                                                   + ForgeKindler.SqlServerCdcRotateTable;

    // #508: what ExpressionMapAdopt writes under WhatIf, so ExpressionMapRecord can put the map back at the end of the run.
    // Session-scoped like #CdcRotate, for the same reason: a temp table created inside a procedure dies when it returns.
    private const string SqlServerExpressionMapAdoptUndoTable =
        "IF OBJECT_ID('tempdb..#ExpressionMapAdoptUndo') IS NOT NULL DROP TABLE #ExpressionMapAdoptUndo\n" +
        "CREATE TABLE #ExpressionMapAdoptUndo (ObjectSchema NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, ObjectTable NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, " +
        "ObjectKind VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL, ObjectName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, Slot VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL, " +
        "WasPresent BIT NOT NULL, AuthoredText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, CanonicalText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, " +
        "PlatformName VARCHAR(20) COLLATE DATABASE_DEFAULT NULL, EngineVersion VARCHAR(200) COLLATE DATABASE_DEFAULT NULL, CompatLevel INT NULL, UpdatedUtc DATETIME2(3) NULL)\n";

    // #508: before any table or column is created, so only what already exists is checked on a scratch copy.
    private string SqlServerExpressionMapAdopt => $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ExpressionMapAdopt @WhatIf = {_whatIfOnly}\n";

    // Runs before anything is created, on every run including a resumed one: neutralizes (or, under 'fail', refuses) what
    // the detected version cannot support (#425), then resolves the template's CDC defaults and refuses a change the
    // capture-instance limit would block (#427). A RAISERROR does not end a batch, and this connection surfaces errors as
    // messages, so a refusal the batch merely reported would still let MissingTableAndColumnQuench create the tables.
    // The CATCH re-raises and ends the batch.
    // A Before script can enable CDC or Change Tracking on the database, and the Before slot runs after this preflight,
    // so with Before scripts present those two are judged after that slot instead (#432).
    internal bool DeferDatabaseToggles => _iteration.BeforeScripts is { Count: > 0 };

    internal void QuenchDeferredDatabaseToggles(IDbCommand tableCommand)
    {
        ClearParameters(tableCommand);
        tableCommand.CommandText = $@"BEGIN TRY
  EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.DegradeDatabaseToggles
END TRY
BEGIN CATCH
  DECLARE @v_DegradeRefusal NVARCHAR(4000) = ERROR_MESSAGE()
  RAISERROR(@v_DegradeRefusal, 16, 1)
  RETURN
END CATCH";
        ExecuteNonQueryHandlingMessages(tableCommand);
    }

    internal string SqlServerPreflight => $@"BEGIN TRY
  EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ValidateTableOwnership @ProductName = '{EscapeSqlLiteral(_product.Name)}', @WhatIf = {_whatIfOnly}
  EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.DegradeUnsupportedFeatures @DeferDatabaseToggles = {(DeferDatabaseToggles ? 1 : 0)}
  EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.CdcPreflight @CdcFilegroup = {TemplateCdcFilegroup}, @CdcSupportsNetChanges = {TemplateCdcSupportsNetChanges}
END TRY
BEGIN CATCH
  DECLARE @v_DegradeRefusal NVARCHAR(4000) = ERROR_MESSAGE()
  RAISERROR(@v_DegradeRefusal, 16, 1)
  RETURN
END CATCH";

    /// <summary>
    /// SQL Server's table-level features that run after indexes and constraints: a declared Disabled state needs its
    /// index, a FILESTREAM column needs its ROWGUIDCOL constraint, CDC must see every column (computed and FILESTREAM
    /// included), and Change Tracking needs the primary key. Same order as the <c>SchemaSmith.TableQuench</c> wrapper.
    /// </summary>
    internal void QuenchSqlServerTableFeatures(IDbCommand tableCommand)
    {
        SafeProgressLog("  Quenching index state, FILESTREAM columns, CDC and Change Tracking");
        var db = Identifier.EscapeDelimited(_databaseName, _product.Platform);
        tableCommand.CommandText = $@"EXEC [{db}].SchemaSmith.IndexStateQuench @WhatIf = {_whatIfOnly}
EXEC [{db}].SchemaSmith.FileStreamColumnQuench @WhatIf = {_whatIfOnly}
EXEC [{db}].SchemaSmith.CdcQuench @WhatIf = {_whatIfOnly}
EXEC [{db}].SchemaSmith.ChangeTrackingQuench @WhatIf = {_whatIfOnly}";
        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Table Features"), ReadsTheParsedTables(tableCommand.CommandText));
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
    }

    private void RecordExpressionMap(IDbCommand tableCommand)
    {
        try
        {
            tableCommand.CommandText = _product.Platform.GetBasePlatform() == Platform.MySQL
                ? $"CALL SchemaSmith_ExpressionMapRecord('{EscapeSqlLiteral(_databaseName)}', {_whatIfOnly})"
                : _product.Platform == Platform.PostgreSQL
                // _whatIfOnly is already rendered per engine (true/false on PostgreSQL, 1/0 elsewhere).
                ? $"CALL \"SchemaSmith\".\"ExpressionMapRecord\"(p_WhatIf := {_whatIfOnly})"
                : $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ExpressionMapRecord @WhatIf = {_whatIfOnly}";
            tableCommand.ExecuteNonQuery();
        }
        catch (DbException e)
        {
            SafeProgressLog($"  Could not record the expression map ({e.Message}). Expression comparison falls back to text on the next run.");
        }
    }

    private void RebuildChangedVectorIndexes(IDbCommand cmd)
    {
        var declared = _template.Tables
            .SelectMany(t => t.Indexes.OfType<Schema.Domain.MariaDb.MariaDbIndex>()
                .Where(i => string.Equals(i.IndexType, "VECTOR", StringComparison.OrdinalIgnoreCase))
                .Select(i => (Table: Unquote(t.Name), Index: i)))
            .ToList();
        if (declared.Count == 0) return;

        // Only what applies to this target: the parse has already removed the tables and indexes whose
        // ShouldApplyExpression is false, and the index step skips them, so one dropped here would stay dropped.
        if (!MySqlTempTablesExist(cmd))
            ParseMySqlTableJson(cmd);
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText = "SELECT TableName, IndexName FROM _SchemaSmith_Indexes WHERE UPPER(IndexType) = 'VECTOR'";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) applied.Add($"{Unquote(reader.GetString(0))}.{Unquote(reader.GetString(1))}");
        declared = declared.Where(d => applied.Contains($"{d.Table}.{Unquote(d.Index.Name)}")).ToList();
        if (declared.Count == 0) return;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cmd.CommandText = "SELECT TABLE_NAME, INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND INDEX_TYPE = 'VECTOR'";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) live.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        if (live.Count == 0) return;

        cmd.CommandText = "SELECT @@mhnsw_default_m, @@mhnsw_default_distance";
        int defaultM;
        string defaultDistance;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            defaultM = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            defaultDistance = reader.GetValue(1)?.ToString();
        }

        foreach (var table in declared.GroupBy(d => d.Table, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = table.Where(d => live.Contains($"{table.Key}.{Unquote(d.Index.Name)}")).ToList();
            if (candidates.Count == 0) continue;
            cmd.CommandText = $"SHOW CREATE TABLE `{table.Key.Replace("`", "``")}`";
            string createTable;
            using (var reader = cmd.ExecuteReader())
                createTable = reader.Read() ? reader.GetString(1) : "";
            foreach (var (_, index) in candidates.Where(c => MariaDbVectorIndexOptions.NeedsRebuild(c.Index, createTable, defaultM, defaultDistance)))
            {
                var name = Unquote(index.Name);
                var auditName = $"{table.Key}.{name}";
                if (IsWhatIf)
                {
                    SafeProgressLog($"  [WhatIf] Would rebuild VECTOR index `{table.Key}`.`{name}`: its M or DISTANCE changed");
                    ChangeAudit?.Record("index", auditName, "wouldRebuild", target: AuditTarget);
                    continue;
                }
                SafeProgressLog($"  Rebuilding VECTOR index `{table.Key}`.`{name}`: its M or DISTANCE changed");
                cmd.CommandText = $"ALTER TABLE `{table.Key.Replace("`", "``")}` DROP INDEX `{name.Replace("`", "``")}`";
                cmd.ExecuteNonQuery();
                ChangeAudit?.Record("index", auditName, "dropped", target: AuditTarget);
            }
        }

        static string Unquote(string name)
        {
            name = name?.Trim() ?? "";
            return name.Length > 1 && name[0] == '`' && name[^1] == '`' ? name[1..^1].Replace("``", "`") : name;
        }
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
                    ? $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.IndexOnlyQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @TableDefinitions = '{EscapeSqlLiteral(indexOnlyTableDefs)}', @DropUnknownIndexes = {_dropUnknownIndexes}, @DropIndexesRemovedFromProduct = {_dropRemovedIndexes}, @UpdateFillFactor = {updateFillFactor}, @WhatIf = {_whatIfOnly}, @CaptureWouldDrop = {FormatBooleanFlag(CaptureWouldDrop)}, @OnlineApply = '{OnlineApply}', @OnlineApplyMaxDuration = {OnlineApplyMaxDuration}"
                    : $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingIndexesAndConstraintsQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @WhatIf = {_whatIfOnly}, @OnlineApply = '{OnlineApply}', @OnlineApplyMaxDuration = {OnlineApplyMaxDuration}";
                break;
            }
            case Platform.PostgreSQL:
                tableCommand.CommandText = _template.IndexOnlyTableQuenches
                    ? $@"
CALL ""SchemaSmith"".""IndexOnlyQuench""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_TableDefinitions := '{EscapeSqlLiteral(IterationTableSchema)}', p_DropUnknownIndexes := {_dropUnknownIndexes}, p_DropIndexesRemovedFromProduct := {_dropRemovedIndexes}, p_WhatIf := {_whatIfOnly}, p_UpdateFillFactor := {_template.UpdateFillFactor.ToString().ToLower()}, p_CaptureWouldDrop := {FormatBooleanFlag(CaptureWouldDrop)}, p_OnlineApply := '{OnlineApply}');
CALL ""SchemaSmith"".""ReplicaIdentityQuench""(p_WhatIf := {_whatIfOnly});
CALL ""SchemaSmith"".""FixupIndexOwnership""(p_ProductName := '{EscapeSqlLiteral(_product.Name)}', p_WhatIf := {_whatIfOnly}, p_TemplateName := '{EscapeSqlLiteral(_template.Name)}', p_SchemaName := '{EscapeSqlLiteral(_schemaName)}');
"
                    : $@"
CALL ""SchemaSmith"".""MissingIndexesAndConstraintsQuench""(p_WhatIf := {_whatIfOnly}, p_OnlineApply := '{OnlineApply}');
CALL ""SchemaSmith"".""DescriptionQuench""(p_WhatIf := {_whatIfOnly});
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
                SetMySqlOnlineApply(tableCommand);
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

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Indexes"), ReadsTheParsedTables(tableCommand.CommandText));
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
        RunDeferredOnlineWork(tableCommand);
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

    // OnlineApply reaches the MySQL-family procedures the same way, as a session variable (SchemaSmith_StageOnlineApply
    // reads it), set before every pass so a pooled connection never carries another deploy's value.
    private void SetMySqlOnlineApply(IDbCommand tableCommand)
    {
        tableCommand.CommandText = $"SET @ss_online_apply = '{OnlineApply}'";
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
                // One batch, which a severity-16 error does not end: the TRY stops it at a key that fails, as PostgreSQL does.
                tableCommand.CommandText = $"BEGIN TRY\nEXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ForeignKeyQuench @ProductName = '{EscapeSqlLiteral(_product.Name)}', @WhatIf = {_whatIfOnly}, @OnlineApply = '{OnlineApply}'\nIF [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.fn_ServerMajorVersion() >= 15 EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.EdgeConstraintQuench @WhatIf = {_whatIfOnly}\nEXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ConstraintStateQuench @WhatIf = {_whatIfOnly}\nEXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.ExtendedPropertyQuench @WhatIf = {_whatIfOnly}\nEND TRY\nBEGIN CATCH\nDECLARE @v_Error NVARCHAR(2047) = ERROR_MESSAGE()\nRAISERROR('%s', 16, 1, @v_Error)\nEND CATCH";
                break;
            case Platform.PostgreSQL:
                tableCommand.CommandText = $@"CALL ""SchemaSmith"".""ForeignKeyQuench""(p_WhatIf := {_whatIfOnly}, p_OnlineApply := '{OnlineApply}');
CALL ""SchemaSmith"".""ConstraintStateQuench""(p_WhatIf := {_whatIfOnly}, p_OnlineApply := '{OnlineApply}');";
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

        _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Foreign Keys"), ReadsTheParsedTables(tableCommand.CommandText));
        ExecuteNonQueryHandlingMessages(tableCommand, retryOnDeadlock: true);
        _debugFileLocation = "";
        RunDeferredOnlineWork(tableCommand);
    }

    // PostgreSQL OnlineApply: what the pass just queued runs now, outside its transaction (PostgreSqlOnlineWork). Skipped
    // when neither the environment nor any table asks for it, so an Off deploy sends exactly what it always did.
    private void RunDeferredOnlineWork(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() != Platform.PostgreSQL || _whatIfOnly == FormatBooleanFlag(true) ||
            (OnlineApply == "OFF" && !_template.Tables.Exists(t => !string.IsNullOrWhiteSpace(t.OnlineApply) &&
                                                                   !t.OnlineApply.Trim().Equals("Off", StringComparison.OrdinalIgnoreCase))))
            return;

        PostgreSqlOnlineWork.Run(tableCommand, SafeProgressLog,
            sql => _debugFileLocation = LogSqlScript(GetDebugFileName("Online Work"), sql));
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
    /// Whether the scheduled-event step runs at all.
    /// <para><b>An empty declaration still qualifies when by-absence removal is on</b>, and that is the
    /// whole point of this predicate. A product that used to own events and now declares none needs the
    /// pass to run against an empty declared set -- <see cref="QuenchEvents"/> falls through with a
    /// canonical <c>"[]"</c> for exactly that case. Gating the step on <c>Events.Count &gt; 0</c> made
    /// that fall-through unreachable, so removing the LAST declared event was a silent no-op: exit 0,
    /// nothing logged, and a live scheduled job still running in production. Removing one of TWO
    /// declared events always worked, which is what hid it.</para>
    /// <para>With the flag off and nothing declared this is still false, so the overwhelmingly common
    /// package -- which declares no events and does not set the flag -- pays no round trip.</para>
    /// </summary>
    internal static bool ShouldQuenchEvents(Platform platform, int declaredEventCount, bool dropRemovedEvents)
        => platform.GetBasePlatform() == Platform.MySQL && (declaredEventCount > 0 || dropRemovedEvents);

    /// <summary>
    /// Converges DECLARED scheduled events (MySQL/MariaDB). Scripted events in the same Events/ folder
    /// still run through the Objects slot untouched, so this is purely additive for existing packages.
    /// <para><b>This is the only quench that executes DDL from C# rather than inside the procedure, and
    /// it is not a style choice.</b> MySQL before 9.0 cannot PREPARE event DDL — both CREATE EVENT and DROP
    /// EVENT fail with 1295, "This command is not supported in the prepared statement protocol yet" —
    /// so a stored procedure cannot create an event on 8.4 and below. MariaDB and MySQL 9.0+ can, but writing to
    /// the lowest common denominator keeps ONE implementation for every supported version.</para>
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

    /// <summary>
    /// Converges DECLARED enum types (PostgreSQL). Runs BEFORE tables: a column can be of an enum type,
    /// so the type has to exist first, and a value the package adds has to be there before a column
    /// default or check references it.
    /// </summary>
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
        if (_product.Platform != Platform.PostgreSQL && _product.Platform != Platform.MariaDb) return;
        var sequences = IterationSequenceSchema;
        if (string.IsNullOrWhiteSpace(sequences) || sequences.Trim() == "[]") return;

        SafeProgressLog("  Quenching sequences");
        if (_product.Platform == Platform.MariaDb)
        {
            tableCommand.CommandText = $"CALL SchemaSmith_SequenceQuench('{EscapeMySqlLiteral(_databaseName)}', '{EscapeMySqlLiteral(sequences)}', {_whatIfOnly})";
            _debugFileLocation = LogSqlScript(GetDebugFileName("Quench Sequences"), tableCommand.CommandText);
            tableCommand.ExecuteNonQuery();
            return;
        }
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

    /// <summary>
    /// Build the SQL Server table working set in two phases so the model travels as a parameter rather
    /// than as a string literal inside the batch.
    /// <para>
    /// A large product ships tens of megabytes of table JSON. Inlined, SQL Server had to lex and parse
    /// all of it as part of the batch on every work unit, and cache a plan keyed by the entire text.
    /// Sent as a parameter it is data: the batch is the script alone, and the same script text is reused
    /// across databases instead of producing one cache entry per payload.
    /// </para>
    /// <para>
    /// The split is what makes that legal. Any parameterized command goes through sp_executesql, and a
    /// temp table created inside that nested scope dies when it returns — the working set would vanish
    /// before MissingTableAndColumnQuench ever saw it. So phase A creates the tables in an
    /// unparameterized batch, putting them in the session scope, and phase B, parameterized, only ever
    /// INSERTs into tables that already exist.
    /// </para>
    /// </summary>
    private void PrepareSqlServerTableIngest(IDbCommand command, string updateFillFactor)
    {
        var (createTables, fillTables) = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer);
        var tableJson = IterationTableSchema;
        var quench = SqlServerExpressionMapAdopt + $"EXEC [{Identifier.EscapeDelimited(_databaseName, _product.Platform)}].SchemaSmith.MissingTableAndColumnQuench @WhatIf = {_whatIfOnly}";

        // The debug artifact stays replayable, which means it stays whole: the undivided script with the
        // payload inlined, exactly the text this path used to execute. Splitting the execution should not
        // cost anyone the ability to paste the failing run into a query window. Written before either
        // phase runs, so a failure in the first one still leaves the artifact that explains it.
        _debugFileLocation = LogSqlScript(GetDebugFileName("Parse Table Json"), $@"
DECLARE @TableDefinitions VARCHAR(MAX)= '{EscapeSqlLiteral(tableJson)}',
        @UpdateFillFactor BIT = {updateFillFactor}
{ForgeKindler.GetParseTableJsonScript(Platform.SqlServer)}
{SqlServerCdcRotateTable}
{SqlServerExpressionMapAdoptUndoTable}
{SqlServerPreflight}
{quench}");

        ClearParameters(command);
        command.CommandText = createTables + Environment.NewLine + SqlServerCdcRotateTable + SqlServerExpressionMapAdoptUndoTable;
        // Retried like every other step: these reads and creates now contend with concurrent DDL for
        // real locks, where the dirty read they replaced waited for nothing.
        ExecuteNonQueryHandlingMessages(command, retryOnDeadlock: true);


        // @v_SQL is re-declared because variables, unlike temp tables, do not cross a batch boundary.
        command.CommandText = $@"
DECLARE @v_SQL NVARCHAR(MAX) = ''
SET NOCOUNT ON
{fillTables}
{SqlServerPreflight}
{quench}";

        // AnsiString with Size -1 is VARCHAR(MAX) -- the type the inlined literal had. Left to infer,
        // the provider would size it at 8000 and silently truncate every model bigger than that.
        var payload = command.CreateParameter();
        payload.ParameterName = "@TableDefinitions";
        payload.Value = tableJson;
        payload.DbType = DbType.AnsiString;
        payload.Size = -1;
        command.Parameters.Add(payload);

        var fillFactor = command.CreateParameter();
        fillFactor.ParameterName = "@UpdateFillFactor";
        fillFactor.Value = _template.UpdateFillFactor;
        fillFactor.DbType = DbType.Boolean;
        command.Parameters.Add(fillFactor);
    }

    #endregion

    #region MySQL Temp Tables

    private void ParseMySqlTableJson(IDbCommand command)
    {
        // The iteration's payload: it carries the engine tokens ({{ServerMajorVersion}}) as well as the user's.
        var tableJson = !string.IsNullOrEmpty(IterationTableSchema)
            ? IterationTableSchema
            : JsonHelper.SerializeAll(_template.Tables);
        PrepareParameterizedTableJson(command,
            $"CALL SchemaSmith_ParseTableJson('{EscapeSqlLiteral(_databaseName)}', @tableJson)",
            tableJson, "Parse Table Json");
    }

    /// <summary>
    /// Run a shred whose payload is bound as a parameter rather than escaped into the statement text.
    /// <para>
    /// The debug artifact is written with the payload inlined, so it stays replayable: a parameterized
    /// statement pasted into a query window without its arguments is not much use to whoever is chasing
    /// the failure the artifact exists to explain.
    /// </para>
    /// </summary>
    private void PrepareParameterizedTableJson(IDbCommand command, string commandText, string tableJson, string debugName)
    {
        command.CommandText = commandText;
        _debugFileLocation = LogSqlScript(GetDebugFileName(debugName),
            commandText.Replace("@tableJson", $"'{EscapeSqlLiteral(tableJson)}'"));

        ClearParameters(command);
        AddJsonParameter(command, "@tableJson", tableJson);
        try
        {
            ExecuteNonQueryHandlingMessages(command, retryOnDeadlock: true);
        }
        finally
        {
            // The command is pooled and every later step reuses it, so the payload must not outlive this
            // call -- including down the error path, where the next thing to touch it is failure handling.
            ClearParameters(command);
        }
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

    // A table step reads the temp tables the parse step built on the same connection, so its file alone fails on a missing
    // temp table; the file says what has to run first.
    internal const string ParsedTablesNote = "-- Reads the temp tables the Parse Table Json step builds: run that step's file first, in the same session.";

    private static string ReadsTheParsedTables(string sql) => ParsedTablesNote + Environment.NewLine + sql;

    // A connect that stalled said only "The operation has timed out", under "Begin Quench", naming neither the target nor
    // that it was the connect.
    internal static string ConnectFailure(Platform platform, string server, string database, string user, string reason) =>
        ProductQuench.ConnectFailure(platform, server, $" [Database: {database}]", user, reason);

    // Not wrapped: the lost-connection classifier walks inner exceptions and would call a failed connect a mid-run drop.
    private RunFailedException ConnectFailed(IDbConnection connection, Exception e, IConfigurationRoot config)
    {
        connection.Dispose();
        _errorLog.Error($"Could not connect to {_databaseName} on {_server}", e);
        return new RunFailedException(ConnectFailure(_product.Platform, _server, _databaseName, config[SettingsKeys.Target.User], e.Message));
    }

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

        try
        {
            connection.Open();
        }
        catch (DbException e)
        {
            throw ConnectFailed(connection, e, config);
        }
        catch (TimeoutException e)
        {
            throw ConnectFailed(connection, e, config);
        }
        catch (InvalidOperationException e)
        {
            throw ConnectFailed(connection, e, config);
        }

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
                _statusMonitor = new StatusMessageMonitor(connectionString, sessionId, msg => LogServerMessage($"    {msg}"));
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

    // #576: only with WriteDebugSql. This ran before every generated step of every deploy, so every run left a file per
    // step in ArtifactPath, the working folder by default.
    private string LogSqlScript(string name, string sql)
    {
        if (!DebugSql.Enabled) return "";
        try
        {
            var dir = ResolveArtifactDirectory();
            DirectoryWrapper.GetFromFactory().CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            FileWrapper.GetFromFactory().WriteAllText(path, ResolvedSqlArtifactWriter.WithOwner(DebugSqlOwner, sql));
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
                    SafeProgressLog($"    Skipping (previously quenched per checkpoint) {script.LogPath}");
                    script.HasBeenQuenched = true;
                    continue;
                }

                var stamp = ProbeScriptStamp(destCmd, script, slot);
                if (stamp is { Unchanged: true })
                {
                    script.HasBeenQuenched = true;
                    script.Error = null;
                    script.Outcome = ScriptOutcome.Unchanged;
                    SafeProgressLog($"    Unchanged, not re-run: {script.LogPath}");
                    _checkpointing.MarkScriptCompleted(DbScope, slot.ToString(), script.LogPath);
                    ChangeAudit?.Record(ObjectScriptClassifier.Classify(script.LogPath), script.LogPath, "unchanged", target: AuditTarget);
                    continue;
                }

                var captured = CaptureModulePermissions(destCmd, script);
                var signatures = CaptureModuleSignatures(destCmd, script);
                QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
                if (script.Error != null && TryClearSchemaBoundDependents(destCmd, script, out var droppedModuleScripts))
                {
                    QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
                    if (script.Error == null) RecreateDroppedModules(destCmd, droppedModuleScripts, showErrors);
                }
                if (captured != null && script.HasBeenQuenched && script.Error == null && script.Outcome == ScriptOutcome.Applied)
                    ReapplyModulePermissions(destCmd, script, captured);
                if (signatures != null && script.HasBeenQuenched && script.Error == null && script.Outcome == ScriptOutcome.Applied)
                    ReapplyModuleSignatures(destCmd, script, signatures);
                if (script.HasBeenQuenched)
                {
                    _checkpointing.MarkScriptCompleted(DbScope, slot.ToString(), script.LogPath);
                    if (slot is DatabaseScriptSlot.Object or DatabaseScriptSlot.AfterTablesObject)
                        ChangeAudit?.RecordRan(ObjectScriptClassifier.Classify(script.LogPath), script.LogPath, AuditTarget);
                    if (stamp != null && script.Outcome == ScriptOutcome.Applied) RecordScriptStamp(destCmd, stamp);
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
        foreach (var r in rows) ChangeAudit.Record(r.ObjectType, r.ObjectName, r.Action, r.Mode, AuditTarget);
        foreach (var line in DowngradeLog.Lines(_product.Platform, rows)) SafeProgressLog(line);
        ChangeAudit.MarkInstrumented();
    }

    internal void QuenchDatabaseObjects(IDbCommand destCmd, List<SqlScript> templateObjects, bool showErrors = true)
    {
        var lastQuenchCount = 0;
        while (lastQuenchCount != templateObjects.Count(s => !s.HasBeenQuenched) && templateObjects.Any(s => !s.HasBeenQuenched))
        {
            lastQuenchCount = templateObjects.Count(s => !s.HasBeenQuenched);
            foreach (var script in templateObjects.Where(s => !s.HasBeenQuenched))
            {
                var captured = CaptureModulePermissions(destCmd, script);
                var signatures = CaptureModuleSignatures(destCmd, script);
                QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
                if (script.Error != null && TryClearSchemaBoundDependents(destCmd, script, out var droppedModuleScripts))
                {
                    QuenchOneScript(destCmd, script, _runScriptsTwice, showErrors);
                    if (script.Error == null) RecreateDroppedModules(destCmd, droppedModuleScripts, showErrors);
                }
                if (captured != null && script.HasBeenQuenched && script.Error == null && script.Outcome == ScriptOutcome.Applied)
                    ReapplyModulePermissions(destCmd, script, captured);
                if (signatures != null && script.HasBeenQuenched && script.Error == null && script.Outcome == ScriptOutcome.Applied)
                    ReapplyModuleSignatures(destCmd, script, signatures);
            }
        }

        _debugFileLocation = "";
        if (showErrors) LogScriptErrors(templateObjects);
    }

    internal void QuenchOneScript(IDbCommand destCmd, SqlScript script, bool runTwice, bool showErrors = true)
    {
        // The script's own file is not resolved SQL: its artifact, when one is written, is reported with the failure.
        _debugFileLocation = "";

        if (showErrors) SafeProgressLog($"    Quenching {script.LogPath}");
        var needDBReset = false;
        try
        {
            script.CheckForUnresolvedTokens(_databaseName, "", _progressLog.Warn);
            for (var i = 1; i <= (runTwice ? 2 : 1); i++)
                foreach (var batch in script.Batches)
                {
                    needDBReset = needDBReset || batch.ContainsIgnoringCase("USE ");
                    _infoMessageException = null;
                    ScriptStatement.Execute(destCmd, batch);
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
            // A failed script can leave the driver's session marked broken although the server kept it: MariaDB reports
            // "referenced trigger does not exist" (a FOLLOWS whose predecessor is a later file) as error 4031, the
            // number MySqlConnector reserves for "disconnected by the server". The retry pass needs a live connection.
            if (destCmd.Connection is { State: not ConnectionState.Open } connection) needDBReset = Reopen(connection);
        }
        finally
        {
            if (needDBReset) ResetDb(destCmd);
        }
    }

    private bool Reopen(IDbConnection connection)
    {
        try
        {
            connection.Close();
            connection.Open();
            SafeProgressLog("    Reopened the connection the failed script left broken");
            return true;
        }
        catch (System.Data.Common.DbException ex)
        {
            SafeProgressLog($"    Could not reopen the connection: {ex.Message}");
            return false;
        }
    }

    #region Schema-bound dependents of a changed module

    // SQL Server refuses to alter a module that a schema-bound object references (3729). The catalog lists the
    // dependents and the package says which are its own; those are dropped so the change can apply, and the run puts
    // them back -- a scripted module right after the change, a constraint, column or indexed view by the pass that
    // owns it. One the package does not declare stops the deploy by name, before anything is dropped.
    // SQL Server only: SCHEMABINDING has no counterpart on PostgreSQL or MySQL.
    private readonly List<(string ObjectName, string Script)> _indexedViewPermissions = new();
    private readonly List<PreservedModulePermissions> _droppedDependentPermissions = new();

    private static bool IsBlockedBySchemaBinding(Exception error) => error switch
    {
        SqlServerErrorException { Number: 3729 } => true,
        SqlException { Number: 3729 } => true,
        _ => false
    };

    private SchemaBoundPlan SchemaBoundPlanFor(IDbCommand destCmd, SqlScript script)
    {
        if (destCmd == null || _product.Platform.GetBasePlatform() != Platform.SqlServer) return null;
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        if (identity == null || identity.Kind is not (ModuleKind.Function or ModuleKind.View)) return null;
        List<SchemaBoundDependent> dependents;
        try
        {
            dependents = SchemaBoundDependents.Read(destCmd, identity.Schema, identity.Name);
        }
        catch (System.Data.Common.DbException)
        {
            return null;
        }
        return dependents.Count == 0 ? null : SchemaBoundDependents.Plan(dependents, DeclaredObjectsOfIteration());
    }

    private bool TryClearSchemaBoundDependents(IDbCommand destCmd, SqlScript script, out List<SqlScript> droppedModuleScripts)
    {
        droppedModuleScripts = [];
        if (!IsBlockedBySchemaBinding(script.Error)) return false;
        var plan = SchemaBoundPlanFor(destCmd, script);
        if (plan == null) return false;
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        if (!plan.CanProceed)
        {
            script.Error = new Exception(SchemaBoundDependents.BlockedMessage(identity.Schema, identity.Name, plan), script.Error);
            return false;
        }
        try
        {
            foreach (var drop in plan.Drops)
            {
                SafeProgressLog($"    Dropping {drop.Dependent.Display}: it is bound to {identity.Schema}.{identity.Name}, which {script.LogPath} changes; the run puts it back");
                if (drop.Dependent.Kind == SchemaBoundDependentKind.IndexedView) StashIndexedViewPermissions(destCmd, drop.Dependent);
                if (drop.Dependent.Kind == SchemaBoundDependentKind.Module) StashDependentModulePermissions(destCmd, drop.Dependent);
                foreach (var statement in drop.Statements)
                {
                    destCmd.CommandText = statement;
                    destCmd.ExecuteNonQuery();
                }
                ChangeAudit?.Record(drop.AuditType, drop.AuditName, "dropped", target: AuditTarget);
                if (drop.Dependent.Kind == SchemaBoundDependentKind.Module)
                    droppedModuleScripts.AddRange(ScriptsCreating(drop.Dependent.Schema, drop.Dependent.Name));
                if (drop.Dependent.Kind == SchemaBoundDependentKind.SecurityPolicy)
                    droppedModuleScripts.AddRange(ScriptsCreatingSecurityPolicy(drop.Dependent.Schema, drop.Dependent.Name));
            }
        }
        catch (System.Data.Common.DbException ex)
        {
            script.Error = new Exception($"Could not drop a schema-bound dependent of {identity.Schema}.{identity.Name}: {ex.Message}", ex);
            return false;
        }
        script.Error = null;
        return true;
    }

    // Deepest-first was the drop order, so the scripts come back innermost-first. A script that already ran in this
    // run keeps its quenched state; one from a later slot runs again there and finds the module unchanged.
    private void RecreateDroppedModules(IDbCommand destCmd, List<SqlScript> scripts, bool showErrors)
    {
        foreach (var dependent in Enumerable.Reverse(scripts))
        {
            var wasQuenched = dependent.HasBeenQuenched;
            var skippedAsUnchanged = dependent.Outcome == ScriptOutcome.Unchanged;
            QuenchOneScript(destCmd, dependent, _runScriptsTwice, showErrors);
            if (dependent.Error != null) continue;
            dependent.HasBeenQuenched = wasQuenched;
            dependent.Outcome = ScriptOutcome.Applied;
            // Its "unchanged" was logged before this run dropped it; what the summary counts is that it ran.
            if (skippedAsUnchanged)
            {
                var objectType = ObjectScriptClassifier.Classify(dependent.LogPath);
                ChangeAudit?.Retract(objectType, dependent.LogPath, "unchanged", AuditTarget);
                ChangeAudit?.RecordRan(objectType, dependent.LogPath, AuditTarget);
            }
            if (ProbeScriptStamp(destCmd, dependent, DatabaseScriptSlot.AfterTablesObject) is { } stamp) RecordScriptStamp(destCmd, stamp);
        }
        ReapplyCapturedPermissions(destCmd, _droppedDependentPermissions);
        _droppedDependentPermissions.Clear();
    }

    // What ModifiedTableQuench may drop and expect this run to put back: the views and routines the package scripts, and
    // on PostgreSQL the materialized views it declares. A dependent of a changing column that is not here stops the
    // deploy by name before anything is dropped (PostgreSQL #533, SQL Server #542). The grants a drop takes off the ones
    // that are here are captured beside them, for ReapplyPreservedModulePermissions.
    private bool _modulePermissionsStaged;

    private void StageDeclaredModules(IDbCommand tableCommand)
    {
        var basePlatform = _product.Platform.GetBasePlatform();
        if (basePlatform is not (Platform.SqlServer or Platform.PostgreSQL)) return;
        static string Literal(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
        var declared = new HashSet<(string Schema, string Name)>();
        foreach (var script in _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts))
            if (ModuleScriptIdentity.Parse(script.Batches, _product.Platform) is { Name: not null } id)
                declared.Add(basePlatform == Platform.SqlServer
                    ? (DeclaredObjects.Bare(id.Schema ?? "dbo"), DeclaredObjects.Bare(id.Name))
                    : (id.Schema ?? "public", id.Name));
        if (basePlatform == Platform.PostgreSQL)
            foreach (var view in JsonObjects(IterationMaterializedViewSchema))
                declared.Add((view["Schema"]?.ToString() ?? "public", view["Name"]?.ToString() ?? ""));
        ClearParameters(tableCommand);
        var sql = new System.Text.StringBuilder();
        if (basePlatform == Platform.SqlServer)
        {
            sql.Append("IF OBJECT_ID('tempdb..#DeclaredModules') IS NOT NULL DROP TABLE #DeclaredModules; ")
               .Append("CREATE TABLE #DeclaredModules ([Schema] NVARCHAR(128) COLLATE DATABASE_DEFAULT, [Name] NVARCHAR(128) COLLATE DATABASE_DEFAULT); ")
               .Append("IF OBJECT_ID('tempdb..#PreservedModulePermissions') IS NULL ")
               .Append("CREATE TABLE #PreservedModulePermissions ([ObjectType] NVARCHAR(64) COLLATE DATABASE_DEFAULT, [Schema] NVARCHAR(128) COLLATE DATABASE_DEFAULT, [Name] NVARCHAR(128) COLLATE DATABASE_DEFAULT, [Script] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT);");
            // A table value constructor takes at most 1000 rows.
            foreach (var chunk in declared.Chunk(900))
                sql.Append(" INSERT INTO #DeclaredModules VALUES ").Append(string.Join(", ", chunk.Select(d => $"(N{Literal(d.Schema)}, N{Literal(d.Name)})"))).Append(';');
        }
        else
        {
            sql.Append("DROP TABLE IF EXISTS temp_declared_modules; CREATE TEMPORARY TABLE temp_declared_modules (\"Schema\" TEXT, \"Name\" TEXT); ")
               .Append("CREATE TEMPORARY TABLE IF NOT EXISTS temp_preserved_module_permissions (\"ObjectType\" TEXT, \"Schema\" TEXT, \"Name\" TEXT, \"Script\" TEXT);");
            if (declared.Count > 0)
                sql.Append(" INSERT INTO temp_declared_modules VALUES ").Append(string.Join(", ", declared.Select(d => $"({Literal(d.Schema)}, {Literal(d.Name)})"))).Append(';');
        }
        tableCommand.CommandText = sql.ToString();
        tableCommand.ExecuteNonQuery();
        _modulePermissionsStaged = true;
    }

    private sealed record PreservedModulePermissions(string ObjectType, string Schema, string Name, string Script);

    // The grants ModifiedTableQuench captured off the modules it dropped for a column change, put back once the run has
    // re-created them: the after-tables object pass, and on PostgreSQL the materialized-view pass.
    private void ReapplyPreservedModulePermissions(IDbCommand tableCommand)
    {
        if (!_modulePermissionsStaged) return;
        var basePlatform = _product.Platform.GetBasePlatform();
        var preserved = new List<PreservedModulePermissions>();
        ClearParameters(tableCommand);
        tableCommand.CommandText = basePlatform == Platform.SqlServer
            ? "SELECT [ObjectType], [Schema], [Name], [Script] FROM #PreservedModulePermissions"
            : "SELECT \"ObjectType\", \"Schema\", \"Name\", \"Script\" FROM temp_preserved_module_permissions";
        using (var reader = tableCommand.ExecuteReader())
            while (reader.Read())
                preserved.Add(new PreservedModulePermissions(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        if (preserved.Count == 0) return;
        ReapplyCapturedPermissions(tableCommand, preserved);
        tableCommand.CommandText = basePlatform == Platform.SqlServer ? "DELETE FROM #PreservedModulePermissions" : "DELETE FROM temp_preserved_module_permissions";
        tableCommand.ExecuteNonQuery();
    }

    private void ReapplyCapturedPermissions(IDbCommand command, IEnumerable<PreservedModulePermissions> preserved)
    {
        var sqlServer = _product.Platform.GetBasePlatform() == Platform.SqlServer;
        foreach (var item in preserved)
        {
            var objectName = sqlServer ? $"[{item.Schema.Replace("]", "]]")}].[{item.Name.Replace("]", "]]")}]" : QuoteIdent(item.Schema) + "." + QuoteIdent(item.Name);
            command.CommandText = sqlServer
                ? $"SELECT CASE WHEN OBJECT_ID(N'{EscapeSqlLiteral(objectName)}') IS NULL THEN 0 ELSE 1 END"
                : $"SELECT CASE WHEN to_regclass('{EscapeSqlLiteral(objectName)}') IS NOT NULL OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{EscapeSqlLiteral(item.Schema)}' AND p.proname = '{EscapeSqlLiteral(item.Name)}') THEN 1 ELSE 0 END";
            if (Convert.ToInt32(command.ExecuteScalar()) == 0)
            {
                _progressLog.Warn($"{objectName} was dropped for a column change and this run did not re-create it, so its grants were not put back.");
                continue;
            }
            command.CommandText = sqlServer
                ? $"EXEC SchemaSmith.ReapplyPermissions @Script = N'{EscapeSqlLiteral(item.Script)}', @ObjectType = N'{item.ObjectType}', @ObjectName = N'{EscapeSqlLiteral(objectName)}', @WhatIf = 0"
                : $"CALL \"SchemaSmith\".\"ReapplyPermissions\"('{EscapeSqlLiteral(item.Script)}', '{item.ObjectType}', '{EscapeSqlLiteral(objectName)}', FALSE)";
            ExecuteNonQueryHandlingMessages(command);
        }
    }

    // A column type change drops the views and SQL-standard routines that depend on the column (PostgreSQL), and the
    // first object pass has already applied their scripts, so nothing in this run would create them again: they were
    // missing until the next deploy. The procedure records each drop; the scripts that create those objects go back to
    // the after-tables pass.
    private void RequeueModulesTheTableQuenchDropped(IDbCommand tableCommand)
    {
        var rows = ChangeAuditReader.Peek(_product.Platform, tableCommand);
        if (rows == null) return;
        foreach (var row in rows.Where(r => r.Action == "dropped" && r.ObjectType is "view" or "function" or "procedure"))
        {
            var dot = row.ObjectName.IndexOf('.');
            if (dot <= 0) continue;
            foreach (var script in ScriptsCreating(row.ObjectName[..dot], row.ObjectName[(dot + 1)..]))
            {
                script.HasBeenQuenched = false;
                script.Outcome = ScriptOutcome.Applied;
            }
        }
        RequeueViewsOverChangedTables(rows);
    }

    // A view takes its table's columns when it is created (SELECT * is expanded then, or its metadata fixed), so after a
    // table change its text and its rendering can both match the stamp while it still describes the old table. A view
    // skipped as unchanged whose text names a table this run changed, or names such a view, goes back to the
    // after-tables pass, and none is skipped from here on. They go to the end of their list with each after the views it
    // reads, since a view re-created ahead of the one it reads would take that one's old shape.
    private void RequeueViewsOverChangedTables(IReadOnlyList<ChangeAuditRow> rows)
    {
        var changed = rows.Where(r => r.ObjectType is "table" or "column" && r.Action is "created" or "modified" or "dropped" or "renamed" or "rebuilt")
            .Select(r => TableNameOf(r.ObjectType, r.ObjectName)).Where(n => n != null).ToList();
        if (changed.Count == 0) return;
        lock (_tablesChangedGate)
        {
            _tablesChangedThisRun.UnionWith(changed);
            var skippedViews = _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts)
                .Where(s => s.Outcome == ScriptOutcome.Unchanged)
                .Select(s => (Script: s, Identity: ModuleScriptIdentity.Parse(s.Batches, _product.Platform)))
                .Where(v => v.Identity is { Kind: ModuleKind.View }).ToList();
            var requeued = new List<(SqlScript Script, ModuleScriptIdentity Identity)>();
            List<(SqlScript Script, ModuleScriptIdentity Identity)> found;
            do
            {
                found = skippedViews.Where(v => v.Script.Outcome == ScriptOutcome.Unchanged && NamesAChangedTable(v.Script)).ToList();
                foreach (var view in found)
                {
                    view.Script.HasBeenQuenched = false;
                    view.Script.Outcome = ScriptOutcome.Applied;
                    ChangeAudit?.Retract(ObjectScriptClassifier.Classify(view.Script.LogPath), view.Script.LogPath, "unchanged", AuditTarget);
                    _tablesChangedThisRun.Add(view.Identity.Name);
                    requeued.Add(view);
                }
            } while (found.Count > 0);

            foreach (var view in ReadersLast(requeued))
                foreach (var list in new[] { _iteration.ObjectScripts, _iteration.AfterTablesObjectScripts }.Where(l => l.Remove(view)))
                    list.Add(view);
        }
    }

    private static List<SqlScript> ReadersLast(List<(SqlScript Script, ModuleScriptIdentity Identity)> views)
    {
        var ordered = new List<SqlScript>();
        var remaining = views.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(v => !remaining.Any(other => other != v && Mentions(string.Join("\n", v.Script.Batches), other.Identity.Name)));
            if (next.Script == null) next = remaining[0];
            ordered.Add(next.Script);
            remaining.Remove(next);
        }
        return ordered;
    }

    private static bool Mentions(string text, string name) =>
        Regex.IsMatch(text, $@"(?<![\w$#@]){Regex.Escape(name)}(?![\w$#@])", RegexOptions.IgnoreCase);

    private static readonly Regex AuditNamePart = new(@"\[([^\]]+)\]|""([^""]+)""|`([^`]+)`|([^.\s\[\]""`]+)", RegexOptions.Compiled);

    // A table row names [schema.]table, a column row [schema.]table.column; the table is the bare name either way.
    private static string TableNameOf(string objectType, string objectName)
    {
        var parts = AuditNamePart.Matches(objectName.Split(" (")[0]).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value).ToList();
        var index = objectType == "column" ? parts.Count - 2 : parts.Count - 1;
        return index >= 0 ? parts[index] : null;
    }

    private bool NamesAChangedTable(SqlScript script)
    {
        lock (_tablesChangedGate)
        {
            if (_tablesChangedThisRun.Count == 0) return false;
            var text = string.Join("\n", script.Batches);
            return _tablesChangedThisRun.Any(name => Mentions(text, name));
        }
    }

    private List<SqlScript> ScriptsCreatingSecurityPolicy(string schema, string name)
    {
        var key = DeclaredObjects.Key(schema, name);
        return _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts)
            .Where(s => DeclaredObjects.SecurityPoliciesCreatedBy(s.Batches).Contains(key))
            .ToList();
    }

    private List<SqlScript> ScriptsCreating(string schema, string name)
    {
        var key = DeclaredObjects.Key(schema, name);
        return _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts)
            .Where(s => ModuleScriptIdentity.Parse(s.Batches, _product.Platform) is { } id && DeclaredObjects.Key(id.Schema, id.Name) == key)
            .ToList();
    }

    // What this iteration declares, read from the resolved JSON so a schema template's substituted names count.
    private DeclaredObjects DeclaredObjectsOfIteration()
    {
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts))
            if (ModuleScriptIdentity.Parse(script.Batches, _product.Platform) is { } id) modules.Add(DeclaredObjects.Key(id.Schema, id.Name));
        var indexedViews = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in JsonObjects(IterationIndexedViewSchema))
            indexedViews.Add(DeclaredObjects.Key(view["Schema"]?.ToString() ?? "dbo", view["Name"]?.ToString()));
        var tables = new Dictionary<string, DeclaredTable>(StringComparer.Ordinal);
        foreach (var table in JsonObjects(IterationTableSchema))
        {
            tables[DeclaredObjects.Key(table["Schema"]?.ToString() ?? "dbo", table["Name"]?.ToString())] = new DeclaredTable(
                new HashSet<string>((table["CheckConstraints"] as JArray ?? []).Select(c => DeclaredObjects.Bare(c["Name"]?.ToString()))),
                new HashSet<string>((table["Columns"] as JArray ?? []).Where(c => !string.IsNullOrWhiteSpace(c["ComputedExpression"]?.ToString())).Select(c => DeclaredObjects.Bare(c["Name"]?.ToString()))),
                new HashSet<string>((table["Indexes"] as JArray ?? []).Select(i => DeclaredObjects.Bare(i["Name"]?.ToString()))));
        }
        var securityPolicies = new HashSet<string>(
            _iteration.ObjectScripts.Concat(_iteration.AfterTablesObjectScripts).SelectMany(s => DeclaredObjects.SecurityPoliciesCreatedBy(s.Batches)),
            StringComparer.Ordinal);
        return new DeclaredObjects(modules, indexedViews, tables, securityPolicies);
    }

    private static IEnumerable<JObject> JsonObjects(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var token = JToken.Parse(json);
        return token is JArray array ? array.OfType<JObject>() : token is JObject single ? [single] : [];
    }

    private void StashDependentModulePermissions(IDbCommand destCmd, SchemaBoundDependent module)
    {
        destCmd.CommandText = $"DECLARE @s NVARCHAR(MAX); EXEC SchemaSmith.CapturePermissions @Schema = N'{EscapeSqlLiteral(module.Schema)}', @Name = N'{EscapeSqlLiteral(module.Name)}', @Script = @s OUTPUT; SELECT @s;";
        if (destCmd.ExecuteScalar() is string script && script.Length > 0)
            _droppedDependentPermissions.Add(new PreservedModulePermissions(module.TypeDescription == "view" ? "view" : "function", module.Schema, module.Name, script));
    }

    private void StashIndexedViewPermissions(IDbCommand destCmd, SchemaBoundDependent view)
    {
        destCmd.CommandText = $"DECLARE @s NVARCHAR(MAX); EXEC SchemaSmith.CapturePermissions @Schema = N'{EscapeSqlLiteral(view.Schema)}', @Name = N'{EscapeSqlLiteral(view.Name)}', @Script = @s OUTPUT; SELECT @s;";
        if (destCmd.ExecuteScalar() is string script && script.Length > 0) _indexedViewPermissions.Add(($"[{view.Schema}].[{view.Name}]", script));
    }

    private void ReapplyStashedIndexedViewPermissions(IDbCommand tableCommand)
    {
        foreach (var (name, script) in _indexedViewPermissions)
        {
            tableCommand.CommandText = $"EXEC SchemaSmith.ReapplyPermissions @Script = N'{EscapeSqlLiteral(script)}', @ObjectType = N'view', @ObjectName = N'{EscapeSqlLiteral(name)}', @WhatIf = 0";
            tableCommand.ExecuteNonQuery();
        }
        _indexedViewPermissions.Clear();
    }

    #endregion

    #region Signatures of a changed module

    // ALTER strips a module's signatures, and the private key behind them cannot be scripted. Before a SQL Server module
    // script runs, the signatures a master-key-protected certificate or asymmetric key can make again are captured;
    // after the change applies they are made again, and the ones no key the server holds can make are warned about.
    private sealed record ModuleSignatures(string Script, string Unsignable);

    private ModuleSignatures CaptureModuleSignatures(IDbCommand destCmd, SqlScript script)
    {
        if (destCmd == null || _product.Platform.GetBasePlatform() != Platform.SqlServer) return null;
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        if (identity == null) return null;
        try
        {
            destCmd.CommandText = $"DECLARE @s NVARCHAR(MAX), @u NVARCHAR(MAX); EXEC SchemaSmith.CaptureSignatures @Schema = N'{EscapeSqlLiteral(identity.Schema)}', @Name = N'{EscapeSqlLiteral(identity.Name)}', @Script = @s OUTPUT, @Unsignable = @u OUTPUT; SELECT @s, @u;";
            using var reader = destCmd.ExecuteReader();
            if (!reader.Read()) return null;
            var captured = reader.IsDBNull(0) ? null : reader.GetString(0);
            var unsignable = reader.IsDBNull(1) ? null : reader.GetString(1);
            return captured == null && unsignable == null ? null : new ModuleSignatures(captured, unsignable);
        }
        catch (System.Data.Common.DbException)
        {
            // No capture procedure (kindling suppressed on a database that never had one): the script runs as before.
            return null;
        }
    }

    private void ReapplyModuleSignatures(IDbCommand destCmd, SqlScript script, ModuleSignatures signatures)
    {
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        var objectName = ModuleAuditName(identity);
        if (signatures.Unsignable != null) WarnUnsignable(objectName, signatures.Unsignable);
        if (signatures.Script == null) return;
        try
        {
            destCmd.CommandText = $"EXEC SchemaSmith.ReapplySignatures @Script = N'{EscapeSqlLiteral(signatures.Script)}', @ObjectType = N'{identity.Kind.ToString().ToLowerInvariant()}', @ObjectName = N'{EscapeSqlLiteral(objectName)}', @WhatIf = 0";
            destCmd.ExecuteNonQuery();
            DrainChangeAudit(destCmd);
        }
        catch (System.Data.Common.DbException ex)
        {
            script.Error = new Exception($"The script applied, but {objectName} could not be signed again: {ex.Message}", ex);
        }
    }

    private void WarnUnsignable(string objectName, string keys) =>
        _progressLog.Warn($"{objectName} was signed by {keys}, whose private key is not encrypted by the database master key, so SchemaQuench cannot sign it again after the change. Sign it by hand.");

    #endregion

    #region Permissions around a dropped module

    // A script that drops its module takes the module's grants with it (and, on SQL Server, its extended properties).
    // They are captured before the script runs and put back after it; under WhatIf the capture is only reported. The
    // first capture is kept per script, because a retry pass after a failed attempt may find the module already gone.
    private readonly Dictionary<SqlScript, string> _capturedModulePermissions = new();

    private string CaptureModulePermissions(IDbCommand destCmd, SqlScript script)
    {
        if (destCmd == null || !ModuleScriptIdentity.DropsModule(script.Batches, _product.Platform)) return null;
        if (_capturedModulePermissions.TryGetValue(script, out var kept)) return kept;
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        if (identity.Kind is not (ModuleKind.Procedure or ModuleKind.Function or ModuleKind.View)) return null;
        var basePlatform = _product.Platform.GetBasePlatform();
        // MySQL keys a view's grants by name, so a dropped view keeps them with no help.
        if (basePlatform == Platform.MySQL && identity.Kind == ModuleKind.View) return null;
        try
        {
            destCmd.CommandText = basePlatform switch
            {
                Platform.SqlServer => $"DECLARE @s NVARCHAR(MAX); EXEC SchemaSmith.CapturePermissions @Schema = N'{EscapeSqlLiteral(identity.Schema)}', @Name = N'{EscapeSqlLiteral(identity.Name)}', @Script = @s OUTPUT; SELECT @s;",
                Platform.PostgreSQL => $"SELECT \"SchemaSmith\".\"CapturePermissions\"('{EscapeSqlLiteral(identity.Schema)}', '{EscapeSqlLiteral(identity.Name)}', '{ModuleKindWord(identity.Kind)}')",
                _ => $"CALL SchemaSmith_CapturePermissions('{EscapeMySqlLiteral(_databaseName)}', '{EscapeMySqlLiteral(identity.Name)}', '{ModuleKindWord(identity.Kind)}')"
            };
            var value = destCmd.ExecuteScalar();
            var captured = value == null || value is DBNull ? null : value.ToString();
            if (captured != null) _capturedModulePermissions[script] = captured;
            return captured;
        }
        catch (System.Data.Common.DbException)
        {
            // No capture procedure (kindling suppressed on a database that never had one): the script runs as before.
            return null;
        }
    }

    private void ReapplyModulePermissions(IDbCommand destCmd, SqlScript script, string captured)
    {
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        var objectType = identity.Kind.ToString().ToLowerInvariant();
        var objectName = ModuleAuditName(identity);
        try
        {
            destCmd.CommandText = _product.Platform.GetBasePlatform() switch
            {
                Platform.SqlServer => $"EXEC SchemaSmith.ReapplyPermissions @Script = N'{EscapeSqlLiteral(captured)}', @ObjectType = N'{objectType}', @ObjectName = N'{EscapeSqlLiteral(objectName)}', @WhatIf = 0",
                Platform.PostgreSQL => $"CALL \"SchemaSmith\".\"ReapplyPermissions\"('{EscapeSqlLiteral(captured)}', '{objectType}', '{EscapeSqlLiteral(objectName)}', FALSE)",
                _ => $"CALL SchemaSmith_ReapplyPermissions('{EscapeMySqlLiteral(captured)}', '{objectType}', '{EscapeMySqlLiteral(objectName)}', 0)"
            };
            destCmd.ExecuteNonQuery();
            _capturedModulePermissions.Remove(script);
            // The procedure records 'reapplied' on this session, which is not the one the table work is drained from.
            DrainChangeAudit(destCmd);
        }
        catch (System.Data.Common.DbException ex)
        {
            script.Error = new Exception($"The script applied, but the grants its DROP took off {objectName} could not be put back: {ex.Message}", ex);
        }
    }

    private string ModuleAuditName(ModuleScriptIdentity identity) => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"[{identity.Schema}].[{identity.Name}]",
        Platform.PostgreSQL => QuoteIdent(identity.Schema) + "." + QuoteIdent(identity.Name),
        _ => identity.Name
    };

    // quote_ident's rule: a plain lower-case identifier stays bare.
    private static string QuoteIdent(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^[a-z_][a-z0-9_]*$") ? name : "\"" + (name ?? "").Replace("\"", "\"\"") + "\"";

    private static string ModuleKindWord(ModuleKind kind) => kind switch
    {
        ModuleKind.Procedure => "PROCEDURE",
        ModuleKind.Function => "FUNCTION",
        _ => "VIEW"
    };

    // A MySQL literal reads a backslash as an escape unless NO_BACKSLASH_ESCAPES is on.
    private static string EscapeMySqlLiteral(string value) => (value ?? "").Replace("\\", "\\\\").Replace("'", "''");

    #endregion

    #region Unchanged-module stamps

    // An object script is not re-run when the text the deploy would run and the module the server renders are both
    // as they were after the last apply. The two hashes are kept per script in SchemaSmith's ScriptStamp table; a
    // hand change on the server changes the rendering, so the script runs. A script with no single recognisable
    // CREATE, a script from a folder declared without an ObjectType, an [ALWAYS] script, a '-- SchemaSmith: AlwaysRun'
    // sentinel, a RunScriptsTwice run, or the setting turned off never stamps. A WhatIf run reads stamps and writes none.
    private sealed record ScriptStampProbe(string PathHash, string ScriptPath, string TextHash, ModuleScriptIdentity Identity, bool Unchanged);

    private static readonly Regex AlwaysRunSentinel = new(@"--\s*SchemaSmith:\s*AlwaysRun\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool HasAlwaysRunSentinel(SqlScript script) => script.Batches.Any(b => AlwaysRunSentinel.IsMatch(b));

    private ScriptStampProbe ProbeScriptStamp(IDbCommand destCmd, SqlScript script, DatabaseScriptSlot slot)
    {
        if (destCmd == null || !_skipUnchangedObjects || _runScriptsTwice || ShouldAlwaysRun(script.Name) || HasAlwaysRunSentinel(script)) return null;
        if (slot is not (DatabaseScriptSlot.Object or DatabaseScriptSlot.AfterTablesObject) || script.ObjectType == ScriptObjectType.None) return null;
        var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
        if (identity == null) return null;

        var scriptPath = GetRelativeScriptPath(script.LogPath);
        var pathHash = Sha256(scriptPath);
        var textHash = Sha256(string.Join("\n", script.Batches));
        var unchanged = false;
        try
        {
            destCmd.CommandText = $"SELECT {Q("TextHash")}, {Q("DefinitionHash")} FROM {ScriptStampTable} WHERE {Q("ProductName")} = '{EscapeSqlLiteral(_product.Name)}' AND {Q("TemplateName")} = '{EscapeSqlLiteral(_template.Name)}' AND {Q("PathHash")} = '{pathHash}'";
            string storedText = null, storedDefinition = null;
            using (var reader = destCmd.ExecuteReader())
            {
                if (reader.Read()) { storedText = reader.GetString(0); storedDefinition = reader.GetString(1); }
            }
            if (storedText == textHash && !(identity.Kind == ModuleKind.View && NamesAChangedTable(script)))
            {
                var rendering = ModuleDefinitionReader.Read(destCmd, _product.Platform, identity);
                unchanged = rendering != null && Sha256(rendering) == storedDefinition;
            }
        }
        catch (System.Data.Common.DbException)
        {
            // No stamp table (kindling suppressed on a database that never had one): the script runs as before.
            return null;
        }
        return new ScriptStampProbe(pathHash, scriptPath, textHash, identity, unchanged);
    }

    private void RecordScriptStamp(IDbCommand destCmd, ScriptStampProbe stamp)
    {
        try
        {
            var rendering = ModuleDefinitionReader.Read(destCmd, _product.Platform, stamp.Identity);
            var where = $"WHERE {Q("ProductName")} = '{EscapeSqlLiteral(_product.Name)}' AND {Q("TemplateName")} = '{EscapeSqlLiteral(_template.Name)}' AND {Q("PathHash")} = '{stamp.PathHash}'";
            destCmd.CommandText = $"DELETE FROM {ScriptStampTable} {where}";
            destCmd.ExecuteNonQuery();
            if (rendering == null) return;
            destCmd.CommandText = $"INSERT INTO {ScriptStampTable} ({Q("ProductName")}, {Q("TemplateName")}, {Q("PathHash")}, {Q("ScriptPath")}, {Q("TextHash")}, {Q("DefinitionHash")}) " +
                                  $"VALUES ('{EscapeSqlLiteral(_product.Name)}', '{EscapeSqlLiteral(_template.Name)}', '{stamp.PathHash}', '{EscapeSqlLiteral(stamp.ScriptPath)}', '{stamp.TextHash}', '{Sha256(rendering)}')";
            destCmd.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException ex)
        {
            if (_stampWriteFailureLogged) return;
            _stampWriteFailureLogged = true;
            SafeProgressLog($"    Could not record the script stamp, so object scripts will run again next time: {ex.Message}");
        }
    }

    private string ScriptStampTable => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "[SchemaSmith].[ScriptStamp]",
        Platform.PostgreSQL => "\"SchemaSmith\".\"ScriptStamp\"",
        _ => "SchemaSmith_ScriptStamp"
    };

    private string Q(string column) => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => $"[{column}]",
        Platform.PostgreSQL => $"\"{column}\"",
        _ => column
    };

    private static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    #endregion

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
                    MigrationScripts?.Record(_server, _databaseName, _schemaName ?? "", _template.Name, checkpointSlot.ToString(), GetRelativeScriptPath(script.LogPath),
                        script.Outcome == ScriptOutcome.Skipped ? "Skipped" : "Ran");
                    if (_trackRunOnceMigrations && !ShouldAlwaysRun(script.Name))
                        MarkScriptCompleted(destCmd, script, slot);
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

    // D13: always run, so no run-once tracking; a resumed run skips the scripts that succeeded in it. Stops at the first
    // failure and reports that script alone -- the scripts after it did not run, and did not fail.
    private void QuenchSetupScripts(IDbCommand destCmd, List<SqlScript> scripts)
    {
        foreach (var script in scripts.Where(s => !s.HasBeenQuenched))
        {
            if (_checkpointing.HasCompletedScript(DbScope, DatabaseScriptSlot.Setup.ToString(), script.LogPath))
            {
                script.HasBeenQuenched = true;
                SafeProgressLog($"    Skipping (previously quenched per checkpoint) {script.LogPath}");
                continue;
            }

            QuenchOneScript(destCmd, script, _runScriptsTwice);
            if (!script.HasBeenQuenched)
            {
                _debugFileLocation = "";
                LogScriptErrors([script]);
            }
            _checkpointing.MarkScriptCompleted(DbScope, DatabaseScriptSlot.Setup.ToString(), script.LogPath);
            MigrationScripts?.Record(_server, _databaseName, _schemaName ?? "", _template.Name, DatabaseScriptSlot.Setup.ToString(),
                GetRelativeScriptPath(script.LogPath), script.Outcome == ScriptOutcome.Skipped ? "Skipped" : "Ran");
        }
    }

    private void WhatIfLogSetupScripts(List<SqlScript> scripts)
    {
        var entries = new List<WhatIfConsoleEntry>();
        foreach (var script in scripts)
        {
            entries.Add(new WhatIfConsoleEntry("apply", "APPLY", script.LogPath));
            WhatIf?.Record(WhatIfCategory.Apply, LogPrefix, script.LogPath);
        }
        foreach (var line in WhatIfConsoleFormatter.Render(entries, WhatIfConsoleDetail))
            SafeProgressLog(line);
    }

    internal static bool ShouldAlwaysRun(string scriptName) => Path.GetFileNameWithoutExtension(scriptName).EndsWith("[ALWAYS]");

    // Only an absent tracking table reads as "nothing has run yet". Any other failure -- the deploy login cannot read the
    // table -- would make every run-once script run again, so the run stops before running one.
    private List<string> GetCompletedEntriesBySlot(IDbCommand destCmd, string slot)
    {
        var entries = new List<string>();
        // SQL Server reports a denied SELECT as a message on this connection rather than an exception, and hides a table
        // the login has no rights on, so it is asked directly.
        if (_product.Platform.GetBasePlatform() == Platform.SqlServer)
        {
            destCmd.CommandText = "SELECT ISNULL(HAS_PERMS_BY_NAME(N'SchemaSmith.CompletedMigrationScripts', N'OBJECT', N'SELECT'), 0) "
                                  + "& ISNULL(HAS_PERMS_BY_NAME(N'SchemaSmith.CompletedMigrationScripts', N'OBJECT', N'INSERT'), 0)";
            if (Convert.ToInt32(destCmd.ExecuteScalar()) == 0)
                throw new RunFailedException($"Cannot read or record which {slot} scripts have already run: the deploy login has no SELECT and INSERT on "
                                             + $"{TrackingTableName()}. Nothing in the slot was run, because a run-once script would run again. "
                                             + $"Grant the deploy login SELECT, INSERT and DELETE on {TrackingTableName()}.");
        }
        try
        {
            destCmd.CommandText = GetSelectCompletedScriptsSql(
                _product.Name, slot, _template.Name, DbScope.SchemaName ?? "");
            using var reader = destCmd.ExecuteReader();
            while (reader.Read())
                entries.Add(reader.GetString(0));
        }
        catch (DbException ex) when (IsMissingTable(ex))
        {
            return entries;
        }
        catch (DbException ex)
        {
            throw new RunFailedException(RunOnceReadFailure(slot, TrackingTableName(), ex.Message));
        }

        return entries;
    }

    // The engine's own text follows the sentence rather than sitting inside it: PostgreSQL's carries a blank line and a
    // POSITION detail, which split the instruction across three log lines.
    internal static string RunOnceReadFailure(string slot, string trackingTable, string engineError) =>
        $"Cannot read which {slot} scripts have already run from {trackingTable}. Nothing in the slot was run, because a run-once "
        + $"script would run again. Grant the deploy login SELECT, INSERT and DELETE on {trackingTable}.{Environment.NewLine}"
        + $"  {engineError.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)}";

    private void MarkScriptCompleted(IDbCommand destCmd, SqlScript script, string slot)
    {
        try
        {
            destCmd.CommandText = GetInsertCompletedScriptSql(
                GetRelativeScriptPath(script.LogPath), _product.Name, slot,
                _template.Name, DbScope.SchemaName ?? "");
            destCmd.ExecuteNonQuery();
        }
        catch (DbException ex) when (IsMissingTable(ex) || IsDuplicateKey(ex))
        {
            // Never kindled, or already recorded.
        }
        catch (DbException ex)
        {
            script.HasBeenQuenched = false;
            script.Error = new Exception($"The script applied, but could not be recorded as run, so the next deploy would run it again: {ex.Message}. "
                                         + $"Grant the deploy login SELECT, INSERT and DELETE on {TrackingTableName()}.", ex);
        }
    }

    private string TrackingTableName() => _product.Platform.GetBasePlatform() switch
    {
        Platform.SqlServer => "SchemaSmith.CompletedMigrationScripts",
        Platform.PostgreSQL => "\"SchemaSmith\".\"CompletedMigrationScripts\" (and USAGE on the schema)",
        _ => "SchemaSmith_CompletedMigrationScripts"
    };

    private static bool IsMissingTable(DbException ex) => ex switch
    {
        SqlException { Number: 208 } => true,
        Npgsql.PostgresException { SqlState: "42P01" or "3F000" } => true,
        MySqlConnector.MySqlException { Number: 1146 } => true,
        _ => false
    };

    private static bool IsDuplicateKey(DbException ex) => ex switch
    {
        SqlException { Number: 2627 or 2601 } => true,
        Npgsql.PostgresException { SqlState: "23505" } => true,
        MySqlConnector.MySqlException { Number: 1062 } => true,
        _ => false
    };

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
                if (DebugSql.Enabled)
                {
                    artifactPath = ResolvedSqlArtifactWriter.WriteFailureArtifact(directory, DebugSqlOwner, header,
                        sqlScript.Batches, FailingBatchIndex(sqlScript), fileName);
                    SafeProgressLogError($"    Resolved SQL written to: {artifactPath}");
                    SafeErrorLogError($"Unable to quench '{sqlScript.LogPath}': {sqlScript.Error?.Message} — resolved SQL: {artifactPath}");
                }
                else
                    SafeProgressLogError($"    {DebugSql.HowToGetIt(directory)}");
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
            if (!DebugSql.Enabled)
            {
                SafeProgressLogError($"    {DebugSql.HowToGetIt(ResolveArtifactDirectory())}");
                return;
            }
            var fileName = GetDebugFileName($"Failed {label}");
            var path = ResolvedSqlArtifactWriter.WriteFailureArtifact(ResolveArtifactDirectory(), DebugSqlOwner, header,
                new[] { command.CommandText }, 0, fileName);
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

    private bool _explicitTimestampDefaultsReadOnlyReported;

    // A failed data delivery writes its own file and says how to get it; the work unit's failure line and roll-up use them.
    private string _dataDeliveryArtifact;
    private bool _debugSqlHintLogged;

    // A TIMESTAMP column must be created with the default it declares. With explicit_defaults_for_timestamp off the
    // engine invents one, so the table steps run with it on and give the session its own value back afterwards. User
    // scripts on the same connection keep the server's setting.
    private IDisposable PinExplicitTimestampDefaults(IDbCommand tableCommand)
    {
        if (_product.Platform.GetBasePlatform() != Platform.MySQL) return null;
        return MySqlSessionSettings.UseExplicitTimestampDefaults(tableCommand, () =>
        {
            if (_explicitTimestampDefaultsReadOnlyReported || !DeclaresATimestampTheServerWouldDefault(_template.Tables)) return;
            _explicitTimestampDefaultsReadOnlyReported = true;
            SafeProgressLog("  explicit_defaults_for_timestamp is OFF and this server or account does not let a session change it, so a " +
                            "TIMESTAMP NOT NULL column declared without a default gets one the server chooses. Declare the " +
                            "default, or turn the variable ON in the server configuration.");
        });
    }

    // The note is about a TIMESTAMP NOT NULL declared without a default; a package with none has nothing to be told.
    internal static bool DeclaresATimestampTheServerWouldDefault(IEnumerable<Table> tables) =>
        tables.SelectMany(t => t.Columns).Any(c => !c.Nullable && string.IsNullOrWhiteSpace(c.Default)
            && (c.DataType ?? "").TrimStart().StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase));

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
                    LogServerMessage($"      {err.Message}");
            }
        }
    }

    private void OnPostgreSqlNotice(object sender, NpgsqlNoticeEventArgs e)
    {
        var verboseLogging = FactoryContainer.ResolveOrCreate<IConfigurationRoot>()[SettingsKeys.VerboseLogging]?.ToLower() == "true";
        if (ShouldLogPostgreSqlNotice(e.Notice.MessageText, e.Notice.Where, verboseLogging))
            LogServerMessage($"      {e.Notice.MessageText}");
    }

    // SchemaSmith's own routines report progress through RAISE NOTICE, so those always log, as SQL Server's state-100
    // messages do. Most are raised from a block the routine builds and EXECUTEs, and the context names the innermost
    // frame first -- PL/pgSQL function inline_code_block line N at RAISE -- with the routine further down the chain, so
    // it is looked for anywhere in it. A notice raised by anything else -- a user script's DO block or function -- has
    // no SchemaSmith frame and logs only with VerboseLogging.
    internal static bool ShouldLogPostgreSqlNotice(string messageText, string where, bool verboseLogging)
    {
        if (messageText.EndsWith("does not exist, skipping") || messageText.EndsWith("already exists, skipping")) return false;
        return verboseLogging || (where?.Contains("PL/pgSQL function \"SchemaSmith\".", StringComparison.Ordinal) ?? false);
    }

    // A message a SchemaSmith routine raises with a WARNING: prefix names a declared change the deploy did not apply,
    // or a state the next deploy will not fix. It is logged as a warning so ExitNonZeroOnWarning sees it; every other
    // server message is progress.
    internal static bool IsServerWarning(string message) =>
        message != null && message.TrimStart().StartsWith("WARNING:", StringComparison.Ordinal);

    internal void LogServerMessage(string msg)
    {
        if (!IsServerWarning(msg))
        {
            SafeProgressLog(msg);
            return;
        }
        lock (_lockObject)
        {
            _progressLog.Warn($"{LogPrefix} {msg}");
            FailureCtx.Log(msg);
        }
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
    internal void WhatIfLogScripts(List<SqlScript> scripts, DatabaseScriptSlot slot, IDbCommand destCmd = null)
    {
        var entries = new List<WhatIfConsoleEntry>();
        foreach (var script in scripts.Where(s => !s.HasBeenQuenched))
        {
            script.HasBeenQuenched = true;
            if (ProbeScriptStamp(destCmd, script, slot) is { Unchanged: true })
            {
                script.Outcome = ScriptOutcome.Unchanged;
                entries.Add(new WhatIfConsoleEntry("skip (unchanged)", "SKIP (unchanged)", script.LogPath));
                WhatIf?.Record(WhatIfCategory.Skip, LogPrefix, script.LogPath);
                ChangeAudit?.Record(ObjectScriptClassifier.Classify(script.LogPath), script.LogPath, "unchanged", target: AuditTarget);
                continue;
            }
            entries.Add(new WhatIfConsoleEntry("apply", "APPLY", script.LogPath));
            WhatIf?.Record(WhatIfCategory.Apply, LogPrefix, script.LogPath);
            if (CaptureModulePermissions(destCmd, script) != null)
            {
                var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
                var name = ModuleAuditName(identity);
                SafeProgressLog($"    Would re-apply the grants on {name} after {script.LogPath}");
                ChangeAudit?.Record(identity.Kind.ToString().ToLowerInvariant(), name, "wouldReapply", target: AuditTarget);
            }
            if (CaptureModuleSignatures(destCmd, script) is { } signatures)
            {
                var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
                var name = ModuleAuditName(identity);
                if (signatures.Script != null)
                {
                    SafeProgressLog($"    Would sign {name} again after {script.LogPath}");
                    ChangeAudit?.Record(identity.Kind.ToString().ToLowerInvariant(), name, "wouldResign", target: AuditTarget);
                }
                if (signatures.Unsignable != null) WarnUnsignable(name, signatures.Unsignable);
            }
            if (SchemaBoundPlanFor(destCmd, script) is { } plan)
            {
                var identity = ModuleScriptIdentity.Parse(script.Batches, _product.Platform);
                if (!plan.CanProceed)
                    _progressLog.Warn("    The real run would stop at " + script.LogPath + ": " + SchemaBoundDependents.BlockedMessage(identity.Schema, identity.Name, plan));
                foreach (var drop in plan.Drops)
                {
                    SafeProgressLog($"    Would drop {drop.Dependent.Display} before re-applying {script.LogPath}; the run would put it back");
                    ChangeAudit?.Record(drop.AuditType, drop.AuditName, "wouldDrop", target: AuditTarget);
                }
            }
        }
        foreach (var line in WhatIfConsoleFormatter.Render(entries, WhatIfConsoleDetail))
            SafeProgressLog(line);
    }

    private void RecordDataDelivery(string label)
    {
        if (IsWhatIf)
        {
            WhatIf?.Record(WhatIfCategory.Deliver, LogPrefix, label);
            ChangeAudit?.Record("dataDelivery", label, "wouldDeliver", target: AuditTarget);
        }
        else
            ChangeAudit?.Record("dataDelivery", label, "delivered", target: AuditTarget);
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
