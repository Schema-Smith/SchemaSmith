// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Schema.Checkpointing;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench;

/// <summary>
/// The names a deploy records its completed steps under. They are read back from checkpoint files written by earlier
/// versions, so a name is never changed or reused for a different step; a new tracked step gets a new name.
/// </summary>
internal static class CheckpointNames
{
    public const string ValidateBaseline = "ValidateBaseline";
    public const string DomainTypeQuench = "DomainTypeQuench";
    public const string EnumTypeQuench = "EnumTypeQuench";
    public const string SequenceQuench = "SequenceQuench";
    public const string ModifiedTables = "ModifiedTables";
    public const string IndexesAndConstraints = "IndexesAndConstraints";
    public const string TableFeatures = "TableFeatures";
    public const string TableDataDelivery = "TableDataDelivery";
    public const string ForeignKeys = "ForeignKeys";
    public const string MaterializedViewQuench = "MaterializedViewQuench";
    public const string EventQuench = "EventQuench";
    public const string IndexedViewQuench = "IndexedViewQuench";
    public const string VersionStamp = "VersionStamp";

    public static readonly IReadOnlyList<string> All =
    [
        ValidateBaseline, DomainTypeQuench, EnumTypeQuench, SequenceQuench, ModifiedTables, IndexesAndConstraints, TableFeatures,
        TableDataDelivery, ForeignKeys, MaterializedViewQuench, EventQuench, IndexedViewQuench, VersionStamp,
    ];
}

/// <summary>What a step does in a preview.</summary>
internal enum StepPreview
{
    /// <summary>The same call: the procedure takes the WhatIf flag and records what it would do.</summary>
    SameCall,
    /// <summary>The step's own preview action, which lists what would run.</summary>
    Log,
    /// <summary>Nothing.</summary>
    Skip,
}

/// <summary>The commands a deploy's steps run on.</summary>
internal sealed class QuenchLanes(IDbCommand main, IDbCommand table, IDbCommand objects, IDbCommand silent)
{
    public IDbCommand Main { get; } = main;
    public IDbCommand Table { get; } = table;
    public IDbCommand Objects { get; } = objects;
    public IDbCommand Silent { get; } = silent;
    // Opened by data delivery only when a table delivers; closed with the others.
    public IDbConnection DeliveryConnection { get; set; }
}

/// <summary>
/// One step of the deploy. <paramref name="CheckpointName"/> is null for a step that always runs, even on resume.
/// </summary>
internal sealed record QuenchStep(string Name, string CheckpointName, Func<bool> Applies, Action Run, StepPreview Preview, Action PreviewAction = null);

public partial class DatabaseQuench
{
    /// <summary>
    /// The deploy order, once, for a real run and a preview alike: a step cannot be added without saying what a
    /// preview does with it. Checkpoints are the run mode's business, not the step's -- a preview runs against
    /// <see cref="NoCheckpointing"/> (A16).
    /// </summary>
    internal List<QuenchStep> QuenchSteps(QuenchLanes lanes)
    {
        var command = lanes.Main;
        var tableCmd = lanes.Table;
        var objectsCmd = lanes.Objects;
        var silentCmd = lanes.Silent;
        bool TablesUpdated() => !_template.IndexOnlyTableQuenches && _updateTables;
        bool NoTokens(SqlScript s) => s.Batches.All(b => !b.Contains("{{") && !b.Contains("}}"));

        return
        [
            // Not checkpointed: KindleForge is cheap and self-verifying (ForgeKindler compares the in-database stamp and does
            // nothing when it is current), so a database reset out of band and resumed against a stale checkpoint still gets its
            // helpers back, instead of failing later with "SchemaSmith_ParseTableJson does not exist".
            new("KindleForge", null, () => !_suppressKindling, () =>
            {
                SafeProgressLog("  Kindling the forge");
                // SQL Server bakes the detected server version and the resolved unsupported-feature policy into the helper
                // functions at kindle time. Both are no-ops for PostgreSQL and MySQL (PostgreSQL reads a runtime setting).
                var kindlePolicy = string.Equals(FactoryContainer.ResolveOrCreate<Microsoft.Extensions.Configuration.IConfigurationRoot>()[Schema.Configuration.SettingsKeys.UnsupportedFeaturePolicy],
                    "fail", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
                // Retried on contention: the stamp compare makes a second kindle a no-op for what the first one finished.
                // A tier-2 drift check kindles into its helper database, not the target (H1).
                var kindleCmd = _driftKindleCommand ?? silentCmd;
                RetryOnContention(() => ForgeKindler.KindleTheForge(kindleCmd, _product.Platform, _forceReKindle, _ingestEncoding, _sqlServerMajorVersion, kindlePolicy));
            }, StepPreview.SameCall),

            // H1, tier 2: what the deploy reads back from the target -- ownership, stamps, migrations run, the expression map.
            new("DriftCopyState", null, () => _driftHelperDatabase != null, () =>
            {
                SafeProgressLog($"  Drift: reading this database's SchemaSmith state into {_driftHelperDatabase}");
                DriftHelperDatabase.CopyState(_driftKindleCommand, _databaseName, _driftHelperDatabase);
            }, StepPreview.SameCall),

            // H1, tier 0: a drift check without kindling reads the helpers already there, so they must be this build's.
            new("VerifyForge", null, () => _suppressKindling && IsDrift, () =>
            {
                var policy = string.Equals(FactoryContainer.ResolveOrCreate<Microsoft.Extensions.Configuration.IConfigurationRoot>()[Schema.Configuration.SettingsKeys.UnsupportedFeaturePolicy],
                    "fail", StringComparison.OrdinalIgnoreCase) ? "fail" : "warn";
                ForgeKindler.VerifyForDrift(silentCmd, _product.Platform, _ingestEncoding, _sqlServerMajorVersion, policy);
            }, StepPreview.SameCall),

            // Resolved against per-iteration tokens: the script may reference {{SchemaName}} on a schema template.
            new(CheckpointNames.ValidateBaseline, CheckpointNames.ValidateBaseline, () => !string.IsNullOrWhiteSpace(_iteration.BaselineValidationScript),
                () => ValidateBaseline(command), StepPreview.SameCall),

            // D13: database settings and the like that the objects and later scripts rely on, after the helpers and the baseline
            // check and before the first object. Every deploy, one pass, in order; the first error fails the deploy.
            new("SetupScripts", null, () => _iteration.SetupScripts.Count > 0, () =>
            {
                SafeProgressLog("  Quenching Setup scripts");
                QuenchSetupScripts(command, _iteration.SetupScripts);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Setup scripts:");
                WhatIfLogSetupScripts(_iteration.SetupScripts);
            }),

            new("ObjectScriptsWithoutUnresolvedTokens", null, () => true, () =>
            {
                SafeProgressLog("  Quenching object scripts without unresolved tokens");
                QuenchDatabaseObjectsWithCheckpoint(objectsCmd, _iteration.ObjectScripts.Where(NoTokens).ToList(), false, DatabaseScriptSlot.Object);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Object scripts without unresolved tokens:");
                WhatIfLogScripts(_iteration.ObjectScripts.Where(NoTokens).ToList(), DatabaseScriptSlot.Object, objectsCmd);
            }),

            // PostgreSQL domains, enums and sequences come before tables: a column can be OF a domain or enum, a default or
            // check can name an enum value the package adds, and a default can call nextval() on a sequence.
            new(CheckpointNames.DomainTypeQuench, CheckpointNames.DomainTypeQuench,
                () => _product.Platform == Platform.PostgreSQL && _template.DomainTypes.Count > 0, () => QuenchDomainTypes(tableCmd), StepPreview.SameCall),
            new(CheckpointNames.EnumTypeQuench, CheckpointNames.EnumTypeQuench,
                () => _product.Platform == Platform.PostgreSQL && _template.EnumTypes.Count > 0, () => QuenchEnumTypes(tableCmd), StepPreview.SameCall),
            new(CheckpointNames.SequenceQuench, CheckpointNames.SequenceQuench,
                () => (_product.Platform == Platform.PostgreSQL && _template.Sequences.Count > 0) || (_product.Platform == Platform.MariaDb && _template.MariaDbSequences.Count > 0)
                      || (_product.Platform == Platform.SqlServer && _template.SqlServerSequences.Count > 0),
                () => QuenchSequences(tableCmd), StepPreview.SameCall),

            // Not checkpointed: it parses the table JSON into the session's temp tables (#Tables, temp_tables,
            // _SchemaSmith_Tables) that the next table steps read, and those do not survive into a resumed run's session.
            // The procedure adds only what is missing, so running it again on resume is safe and cheap.
            new("MissingTablesAndColumns", null, TablesUpdated, () =>
            {
                // Said before the change, so a column whose existing NULLs refuse it fails with its reason in view.
                foreach (var notice in _template.LoadNotices)
                    lock (_lockObject) _progressLog.Warn($"{LogPrefix} {notice.Message}");
                QuenchMissingTablesAndColumns(tableCmd);
            }, StepPreview.SameCall),

            new("ObjectScriptsWithoutQueryTokens", null, () => true, () =>
            {
                SafeProgressLog("  Quenching object scripts without query tokens");
                QuenchDatabaseObjectsWithCheckpoint(objectsCmd, _iteration.ObjectScripts.Where(NoTokens).ToList(), false, DatabaseScriptSlot.Object);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Object scripts without query tokens:");
                WhatIfLogScripts(_iteration.ObjectScripts.Where(NoTokens).ToList(), DatabaseScriptSlot.Object, objectsCmd);
            }),

            new("ResolveQueryTokens", null, () => true, () => ResolveAndApplyQueryTokens(silentCmd), StepPreview.Log, () =>
            {
                if (_template.QueryTokens.Count > 0) SafeProgressLog("  [WhatIf] Would resolve template query tokens");
            }),

            new("BeforeScripts", null, () => true, () =>
            {
                SafeProgressLog("  Quenching before database scripts");
                QuenchTemplateScriptsWithCheckpoint(command, "Before", _iteration.BeforeScripts, DatabaseScriptSlot.Before);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Before database scripts:");
                WhatIfLogTemplateScripts(command, "Before", _iteration.BeforeScripts, DatabaseScriptSlot.Before);
            }),

            new("DeferredDatabaseToggles", null, () => _product.Platform.GetBasePlatform() == Platform.SqlServer && DeferDatabaseToggles && TablesUpdated(),
                () => QuenchDeferredDatabaseToggles(tableCmd), StepPreview.SameCall),

            // Staged outside the checkpoint: the modules a table change drops are read from it on resume too.
            new("StageDeclaredModules", null, TablesUpdated, () => StageDeclaredModules(tableCmd), StepPreview.SameCall),
            new(CheckpointNames.ModifiedTables, CheckpointNames.ModifiedTables, TablesUpdated, () => QuenchModifiedTables(tableCmd), StepPreview.SameCall),
            new("RequeueDroppedModules", null, TablesUpdated, () => RequeueModulesTheTableQuenchDropped(tableCmd), StepPreview.Skip),

            new("AfterTablesObjectScripts", null, () => true, () =>
            {
                SafeProgressLog("  Quenching object scripts");
                QuenchDatabaseObjectsWithCheckpoint(objectsCmd, _iteration.AfterTablesObjectScripts, false, DatabaseScriptSlot.AfterTablesObject);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Object scripts (after tables):");
                WhatIfLogScripts(_iteration.AfterTablesObjectScripts, DatabaseScriptSlot.AfterTablesObject, objectsCmd);
            }),

            new("BetweenTablesAndKeysScripts", null, () => true, () =>
            {
                SafeProgressLog("  Quenching between table and keys scripts");
                QuenchTemplateScriptsWithCheckpoint(command, "Between Table And Keys", _iteration.BetweenTablesAndKeysScripts, DatabaseScriptSlot.BetweenTablesAndKeys);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Between table and keys scripts:");
                WhatIfLogTemplateScripts(command, "Between Table And Keys", _iteration.BetweenTablesAndKeysScripts, DatabaseScriptSlot.BetweenTablesAndKeys);
            }),

            // MariaDB has no ALTER for a VECTOR index's M or DISTANCE, and the options are only in SHOW CREATE TABLE, which a
            // procedure cannot read: a changed one is dropped here, and the indexes step builds it again as declared.
            new("RebuildChangedVectorIndexes", null, () => _product.Platform == Platform.MariaDb && _updateTables && _mySqlServerVersionNum >= 1107,
                () => RebuildChangedVectorIndexes(tableCmd), StepPreview.SameCall),

            new(CheckpointNames.IndexesAndConstraints, CheckpointNames.IndexesAndConstraints, () => _updateTables,
                () => QuenchIndexesAndConstraints(tableCmd), StepPreview.SameCall),

            // Not in index-only mode: those templates do not own the tables.
            new(CheckpointNames.TableFeatures, CheckpointNames.TableFeatures, () => _product.Platform.GetBasePlatform() == Platform.SqlServer && TablesUpdated(),
                () => QuenchSqlServerTableFeatures(tableCmd), StepPreview.SameCall),

            // #242: after the create passes, so an object created on this run is recorded on this run. A preview records
            // nothing and puts back what the #508 scratch-copy check verified.
            new("RecordExpressionMap", null, () => _updateTables, () => RecordExpressionMap(tableCmd), StepPreview.SameCall),

            new("AfterTableScripts", null, () => true, () =>
            {
                SafeProgressLog("  Quenching after table scripts");
                QuenchTemplateScriptsWithCheckpoint(command, "After Table", _iteration.AfterTableScripts, DatabaseScriptSlot.AfterTable);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] After table scripts:");
                WhatIfLogTemplateScripts(command, "After Table", _iteration.AfterTableScripts, DatabaseScriptSlot.AfterTable);
            }),

            // The final pass picks up an object script that failed earlier on a dependency created since. A preview lists them all.
            new("ObjectScriptsFinalPass", null, () => IsWhatIf || _iteration.ObjectScripts.Union(_iteration.AfterTablesObjectScripts).Any(s => !s.HasBeenQuenched), () =>
            {
                SafeProgressLog("  Quenching object scripts");
                QuenchDatabaseObjectsWithCheckpoint(objectsCmd, _iteration.AfterTablesObjectScripts.ToList(), true, DatabaseScriptSlot.AfterTablesObject);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Object scripts (final pass):");
                WhatIfLogScripts(_iteration.AfterTablesObjectScripts.ToList(), DatabaseScriptSlot.AfterTablesObject, objectsCmd);
            }),

            new(CheckpointNames.TableDataDelivery, CheckpointNames.TableDataDelivery, () => _deliverData, () => DeliverTableData(lanes), StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] Table data delivery:");
                WhatIfLogTableDataScripts(_iteration.TableDataScripts.ToList());
                DeliverTableDataPreview(command);
            }),

            // A preview listed these with the delivery, above.
            new("TableDataScripts", null, () => _deliverData && _iteration.ObjectScripts.Union(_iteration.TableDataScripts).Any(s => !s.HasBeenQuenched), () =>
            {
                SafeProgressLog("  Quenching table data scripts");
                QuenchDatabaseObjectsWithCheckpoint(objectsCmd, _iteration.TableDataScripts.ToList(), true, DatabaseScriptSlot.TableData);
            }, StepPreview.Skip),

            // After data delivery, on every engine.
            new(CheckpointNames.ForeignKeys, CheckpointNames.ForeignKeys, TablesUpdated, () => QuenchForeignKeys(tableCmd), StepPreview.SameCall),

            new(CheckpointNames.MaterializedViewQuench, CheckpointNames.MaterializedViewQuench,
                () => _product.Platform == Platform.PostgreSQL && _template.MaterializedViews.Count > 0, () => QuenchMaterializedViews(tableCmd), StepPreview.SameCall),

            // After tables, so an event whose body references a table the same deploy creates does not fail on first run.
            new(CheckpointNames.EventQuench, CheckpointNames.EventQuench,
                () => ShouldQuenchEvents(_product.Platform, _template.Events.Count, DropRemovedEvents), () => QuenchEvents(tableCmd), StepPreview.SameCall),

            new(CheckpointNames.IndexedViewQuench, CheckpointNames.IndexedViewQuench,
                () => _product.Platform == Platform.SqlServer && _template.IndexedViews.Count > 0, () => QuenchIndexedViews(tableCmd), StepPreview.SameCall),
            new("ReapplyIndexedViewPermissions", null, () => _product.Platform == Platform.SqlServer && _template.IndexedViews.Count > 0,
                () => ReapplyStashedIndexedViewPermissions(tableCmd), StepPreview.Skip),

            new("ReapplyModulePermissions", null, () => true, () => ReapplyPreservedModulePermissions(tableCmd), StepPreview.Skip),

            new("AfterScripts", null, () => true, () =>
            {
                SafeProgressLog("  Quenching after database scripts");
                QuenchTemplateScriptsWithCheckpoint(command, "After", _iteration.AfterScripts, DatabaseScriptSlot.After);
            }, StepPreview.Log, () =>
            {
                SafeProgressLog("  [WhatIf] After database scripts:");
                WhatIfLogTemplateScripts(command, "After", _iteration.AfterScripts, DatabaseScriptSlot.After);
            }),

            new(CheckpointNames.VersionStamp, CheckpointNames.VersionStamp, () => !string.IsNullOrWhiteSpace(_iteration.VersionStampScript),
                () => StampVersion(command), StepPreview.Log, () => SafeProgressLog("  [WhatIf] Would stamp version")),
        ];
    }

    /// <summary>
    /// Steps a drift check leaves out (H1): Setup scripts, data delivery and the version stamp act on every deploy by design,
    /// so they say nothing about the target's state.
    /// </summary>
    internal static readonly IReadOnlySet<string> DriftExcludedSteps = new HashSet<string>
    {
        "SetupScripts", CheckpointNames.TableDataDelivery, "TableDataScripts", CheckpointNames.VersionStamp,
    };

    private void RunQuenchSteps(QuenchLanes lanes)
    {
        foreach (var step in QuenchSteps(lanes))
        {
            if (!step.Applies() || (IsDrift && DriftExcludedSteps.Contains(step.Name))) continue;
            _currentStep = step.Name;
            var sw = Stopwatch.StartNew();
            if (IsWhatIf)
            {
                if (step.Preview == StepPreview.SameCall) step.Run();
                else if (step.Preview == StepPreview.Log) step.PreviewAction?.Invoke();
            }
            else if (step.CheckpointName != null)
            {
                if (_checkpointing.HasCompleted(DbScope, step.CheckpointName))
                    SafeProgressLog($"  Skipping {step.Name} (completed in the run being resumed)");
                _checkpointing.Track(DbScope, step.CheckpointName, step.Run);
            }
            else
                step.Run();
            // A checkpointed step's time is recorded whenever it ran -- in a preview, only when the preview made the same call.
            if (step.CheckpointName != null && (!IsWhatIf || step.Preview == StepPreview.SameCall))
                RunTiming?.Record(LogPrefix, _databaseName, step.Name, sw.ElapsedMilliseconds, 0);
        }
    }

    private void ValidateBaseline(IDbCommand command)
    {
        SafeProgressLog("  Validate Baseline");
        command.CommandText = _iteration.BaselineValidationScript;
        RunWritingFailureArtifact(command, "BaselineValidation", () =>
        {
            // On SQL Server the script's own error arrives as a message, leaving a null that read as "no".
            _infoMessageException = null;
            var isValid = Convert.ToBoolean(command.ExecuteScalar());
            if (_infoMessageException != null) throw _infoMessageException;
            if (!isValid)
                throw new InvalidOperationException("Invalid baseline for this release");
        });
    }

    private void StampVersion(IDbCommand command)
    {
        SafeProgressLog("  Stamp version");
        command.CommandText = _iteration.VersionStampScript;
        RunWritingFailureArtifact(command, "VersionStamp", () => ExecuteNonQueryHandlingMessages(command));
    }

    // A failed baseline check or version stamp leaves its resolved SQL behind, then fails the deploy as before. These are
    // what the two can fail with: the server's error, one SQL Server reported as a message, a result that is not a boolean,
    // or a baseline that said no.
    private void RunWritingFailureArtifact(IDbCommand command, string label, Action work)
    {
        try
        {
            work();
        }
        catch (System.Data.Common.DbException ex) { WriteValidationFailureArtifact(command, label, ex); throw; }
        catch (SqlServerErrorException ex) { WriteValidationFailureArtifact(command, label, ex); throw; }
        catch (InvalidOperationException ex) { WriteValidationFailureArtifact(command, label, ex); throw; }
        catch (InvalidCastException ex) { WriteValidationFailureArtifact(command, label, ex); throw; }
        catch (FormatException ex) { WriteValidationFailureArtifact(command, label, ex); throw; }
    }

    private void RegisterMergeScriptHelper()
    {
        if (FactoryContainer.Resolve<IMergeScriptHelper>() is not MergeScriptHelperAdapter adapter || adapter.Platform != _product.Platform)
            FactoryContainer.Register<IMergeScriptHelper>(new MergeScriptHelperAdapter(_product.Platform));
    }

    private void DeliverTableData(QuenchLanes lanes)
    {
        // Not the silent connection: the merge counts what an append-only ledger table kept in state-100 messages. Opened only
        // when a table delivers.
        var deliveryCmd = lanes.Main;
        if (_product.Platform.GetBasePlatform() != Platform.MySQL && _template.Tables.Cast<IDeliverableTable>().Any(DataDeliveryHelper.HasDeliverable))
        {
            lanes.DeliveryConnection = GetConnection(fireInfoMessageEventOnUserErrors: false);
            deliveryCmd = Timeouts.NewCommand(lanes.DeliveryConnection);
        }
        RegisterMergeScriptHelper();

        DataDeliveryProcessor.GetFromFactory().DeliverTables(new DataDeliveryContext
        {
            Tables = _template.Tables.Cast<IDeliverableTable>().ToList(),
            Command = deliveryCmd,
            // MariaDb is delivered as its MySQL base: the delivery subsystem dispatches on this platform string and has no
            // MariaDb-specific behaviour (the enum-typed ScriptHelper still carries the true platform for SQL generation).
            Platform = _product.Platform.GetBasePlatform().ToString(),
            DatabaseName = _databaseName,
            SchemaName = _schemaName,
            // N2: the folder gate's vocabulary, so a DataDelivery.ShouldApplyExpression can gate on {{CompatibilityLevel}} or
            // {{ServerMajorVersion}} as well as {{SchemaName}}.
            VersionTokens = _versionScriptTokens,
            TemplateRootPath = Path.GetDirectoryName(_template.FilePath) ?? "",
            ScriptHelper = FactoryContainer.Resolve<IMergeScriptHelper>(),
            ReadFileContent = path => ProductFileWrapper.GetFromFactory().ReadAllText(path),
            ExecuteScript = (_, script) => { deliveryCmd.CommandText = script; deliveryCmd.ExecuteNonQuery(); },
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
                catch (IOException artifactEx)
                {
                    SafeProgressLog($"    Could not write resolved-SQL artifact for data delivery '{label}': {artifactEx.Message}");
                }
                catch (UnauthorizedAccessException artifactEx)
                {
                    SafeProgressLog($"    Could not write resolved-SQL artifact for data delivery '{label}': {artifactEx.Message}");
                }
            }
        });
    }

    // A preview builds every delivery script and runs none of them, on the main command.
    private void DeliverTableDataPreview(IDbCommand command)
    {
        RegisterMergeScriptHelper();
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
}
