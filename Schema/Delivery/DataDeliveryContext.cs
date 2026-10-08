// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;

namespace Schema.Delivery;

/// <summary>
/// Runtime context passed from SchemaQuench's DatabaseQuench to an IDataDelivery
/// implementation.
/// </summary>
public class DataDeliveryContext
{
    public IList<IDeliverableTable> Tables { get; set; }
    public string Platform { get; set; }
    public IDbCommand Command { get; set; }
    public string DatabaseName { get; set; }
    public string TemplateRootPath { get; set; }
    public IMergeScriptHelper ScriptHelper { get; set; }
    public Func<string, string> ReadFileContent { get; set; }
    public Action<string, string> ExecuteScript { get; set; }
    public Action<string> ProgressLog { get; set; }

    /// <summary>
    /// Records a delivery the target cannot take (object type, object name), so the run reports it as incomplete
    /// alongside the other downgrades instead of in the progress log alone.
    /// </summary>
    public Action<string, string> RecordDowngrade { get; set; }
    public Action<string> ProgressLogError { get; set; }
    public bool WhatIf { get; set; }

    /// <summary>
    /// Resolved schema name for the current schema-template iteration. Empty string
    /// for regular templates (the default). When non-empty, DataDeliveryProcessor
    /// substitutes occurrences of <c>"{{SchemaName}}"</c> in each table's Schema
    /// before passing it to the catalog-probing metadata helpers and the
    /// MERGE-script builder. Without this substitution the literal token reaches
    /// the SQL helpers, yielding empty catalog result sets and emitting
    /// <c>MERGE INTO [{{SchemaName}}].[Table]</c> — slice-3 audit bug B3.
    /// </summary>
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// Per-target version tokens (<c>{{ServerMajorVersion}}</c>, <c>{{CompatibilityLevel}}</c>) —
    /// the same list <c>DatabaseQuench.ResolveFolderGateExpression</c> assembles for folder gates,
    /// via the shared <see cref="Schema.Utility.TokenHelper.AssembleGateTokens"/>. Threaded through so a
    /// delivery's <see cref="DataDelivery.ShouldApplyExpression"/> can gate on the target version the same
    /// way a folder or table gate already can, instead of only resolving <see cref="SchemaName"/> (N2).
    /// Empty for a caller with no version tokens to offer (e.g. a unit test harness).
    /// </summary>
    public List<KeyValuePair<string, string>> VersionTokens { get; set; } = [];

    /// <summary>
    /// Detected PostgreSQL server major (e.g. 16). 0 = unknown/not-PostgreSQL; codegen treats
    /// unknown as modern (>= 17). Drives the MERGE-vs-DELETE branch in MergeScriptHelper (#241).
    /// </summary>
    public int PostgreSqlServerVersionNum { get; set; }

    /// <summary>
    /// Detected MySQL/MariaDb server version (major*100+minor; 0 = unknown/modern). Selects the
    /// data-delivery JSON-array shred: JSON_TABLE on MySQL 8.0+/MariaDB 10.6+, a recursive CTE on
    /// MariaDB 10.2-10.5, and (gated) below MySQL 8.0 where automatic delivery is unsupported.
    /// </summary>
    public int MySqlServerVersionNum { get; set; }

    /// <summary>
    /// Detected SQL Server target database compatibility_level (e.g. 100, 130, 160). 0 = unknown/not-SQL-Server.
    /// A JSON (OPENJSON) data delivery requires level 130; below that a JSON-encoded delivery degrades through
    /// Target:UnsupportedFeaturePolicy (warn = skip that delivery, fail = abort). XML-encoded deliveries apply
    /// at every level and are never gated by this. (B1 slice 2.)
    /// </summary>
    public int SqlServerCompatibilityLevel { get; set; }

    /// <summary>
    /// Target:UnsupportedFeaturePolicy ('warn' default / 'fail'). Governs how an unsupported target is
    /// handled — e.g. automatic data delivery below MySQL 8.0: 'warn' skips with a clear log, 'fail' aborts.
    /// </summary>
    public string UnsupportedFeaturePolicy { get; set; }

    /// <summary>
    /// Optional. Invoked when a generated data-delivery script fails, with (label, resolvedSql) so the
    /// caller can persist the SQL to a re-runnable artifact and surface its path. Null = no artifact
    /// (e.g. WhatIf). Keeps artifact directory / scrub / token concerns on the caller side.
    /// </summary>
    public Action<string, string> WriteResolvedSqlArtifact { get; set; }
}
