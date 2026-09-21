// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.Delivery;

/// <summary>
/// IDataDelivery implementation providing 2-pass FK-aware data delivery.
/// Orchestrates topological ordering and delegates SQL fragment generation
/// via IMergeScriptHelper callbacks. Owns assembly logic for deferred merge
/// scripts and CASCADE validation.
/// </summary>
public class DataDeliveryProcessor : IDataDelivery
{
    public static IDataDelivery GetFromFactory()
        => FactoryContainer.ResolveOrCreate<IDataDelivery, DataDeliveryProcessor>();

    // Deliveries per table with a real (non-None) MergeType, in declared order — ungated. Used
    // only by ValidateMergeTypes, which is config-validity checking (a bad MergeType string is
    // invalid regardless of whether its gate currently passes) and so must consider every
    // configured delivery. CASCADE validation is gate-aware (see DeliverTables) since it reflects
    // what will actually run. Actual delivery (which deliveries run) is decided by the gate-aware
    // ResolveApplied local function in DeliverTables, whose result is threaded through the rest of
    // the method.
    private static List<(int Index, DataDelivery Delivery)> ApplicableDeliveries(IDeliverableTable table) =>
        (table.DataDeliveries ?? [])
            .Select((d, i) => (Index: i, Delivery: d))
            .Where(x => !string.IsNullOrEmpty(x.Delivery.MergeType) &&
                        !x.Delivery.MergeType.Equals("None", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string VariantSuffix(DataDelivery d) =>
        string.IsNullOrWhiteSpace(d.VariantName) ? "" : $" [{d.VariantName}]";

    public void DeliverTables(DataDeliveryContext context)
    {
        if (context?.Tables == null || context.Tables.Count == 0) return;

        var platform = context.Platform;
        var helper = context.ScriptHelper ?? throw new InvalidOperationException("DataDeliveryContext.ScriptHelper is required");
        var log = context.ProgressLog ?? (_ => { });
        var logError = context.ProgressLogError ?? (_ => { });

        var tablesToDeliver = context.Tables
            .Where(DataDeliveryHelper.HasDeliverable)
            .OrderBy(t => DataDeliveryHelper.GetTableKey(t, platform))
            .ToList();

        if (tablesToDeliver.Count == 0) return;

        // Automatic data delivery below MySQL 8.0 is unsupported: 5.7 has neither JSON_TABLE nor recursive
        // CTEs, so the JSON-array shred cannot be generated. Skip it with a clear message (warn, the default)
        // or abort (fail) per Target:UnsupportedFeaturePolicy. The supported path on such targets is manual
        // data scripts. MariaDB (>= 1000) always proceeds -- 10.2-10.5 use the recursive-CTE fallback.
        if (context.MySqlServerVersionNum is > 0 and < 800)
        {
            var msg = $"Automatic data delivery requires MySQL 8.0+ (JSON_TABLE); the target is MySQL " +
                      $"{context.MySqlServerVersionNum / 100}.{context.MySqlServerVersionNum % 100}. " +
                      "Deliver data on this target with manual data scripts.";
            if (string.Equals(context.UnsupportedFeaturePolicy, "fail", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(msg);
            log($"  [SKIPPED - unsupported below MySQL 8.0] {msg}");
            return;
        }

        log("  Delivering table data");

        // Answer each table's catalog questions once for the length of this pass. Building one table's
        // merge script reads INFORMATION_SCHEMA several times over, and on MySQL/MariaDB every such read
        // materialises to a disk temp table -- measured at 65% of a no-op quench's catalog traffic.
        // Bounded to this call on purpose: delivery performs DML only (the user's own table-data scripts
        // run after it returns), so the catalog cannot move underneath it. Anything wider would break the
        // point-in-time reads the rest of the quench depends on.
        using var catalogScope = MergeScriptHelper.BeginCatalogScope();

        var mergeTypeErrors = ValidateMergeTypes(tablesToDeliver);
        if (mergeTypeErrors.Count > 0)
        {
            foreach (var error in mergeTypeErrors)
                logError($"    {error}");
            throw new InvalidOperationException("Data delivery aborted: Invalid MergeType values detected.");
        }

        // Gate-aware applicable deliveries per table: non-None MergeType AND a passing
        // ShouldApplyExpression (blank expression always applies). Evaluated once per table so
        // sequenced gate mocks/real scalar reads see each delivery's gate exactly once, and so
        // the result can be threaded through the rest of the method (CASCADE validation, content
        // resolution, FK edges, and the per-delivery deliver loop) instead of being recomputed.
        // Runs in WhatIf too (read-only scalar) so WhatIf reporting accurately reflects
        // deliver-vs-skip. A gate-eval exception propagates (fail-closed), aborting the run like
        // FolderGate callers — and runs BEFORE the CASCADE check below, so a gate exception aborts
        // the same way it always has.
        var gateTokenPlatform = PlatformExtensions.ParsePlatform(platform);
        List<(int Index, DataDelivery Delivery)> ResolveApplied(IDeliverableTable table)
        {
            var applied = new List<(int, DataDelivery)>();
            var idx = 0;
            foreach (var d in table.DataDeliveries ?? [])
            {
                var i = idx++;
                if (string.IsNullOrEmpty(d.MergeType) || d.MergeType.Equals("None", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool apply;
                var gateExpression = ResolveGateExpression(d.ShouldApplyExpression, context.SchemaName,
                    context.VersionTokens, gateTokenPlatform);
                try { apply = GateEvaluator.ShouldApply(context.Command, gateExpression); }
                catch (Exception e)
                {
                    logError($"    Data delivery gate for {DataDeliveryHelper.GetTableKey(table, platform)}" +
                             $"{VariantSuffix(d)} failed: {e.Message}");
                    throw;
                }
                if (apply) applied.Add((i, d));
                else log($"    Skipping data delivery for {DataDeliveryHelper.GetTableKey(table, platform)}{VariantSuffix(d)} — ShouldApplyExpression evaluated false");
            }
            return applied;
        }

        var appliedByTable = tablesToDeliver.ToDictionary(t => t, ResolveApplied);

        // B1 slice 2: a JSON (OPENJSON) delivery requires SQL Server compatibility level 130. On a
        // below-cliff target (compat 100-120, common where a line-of-business app is certified at an
        // older level) an XML-encoded delivery still applies (.nodes()/.value() works at every level),
        // but a JSON one parse-errors — so degrade it per Target:UnsupportedFeaturePolicy: warn (the
        // default) skips just that delivery with a clear message and delivers the rest; fail aborts.
        // Per-delivery (not a whole-table return like the MySQL < 8.0 case at :59) because the encoding
        // is a per-delivery author choice — a table may pair a JSON and an XML variant. Filtered before
        // CASCADE validation / content reads / the deliver loop so skipped deliveries never reach them.
        if (platform.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) &&
            context.SqlServerCompatibilityLevel is > 0 and < 130)
        {
            var failFast = string.Equals(context.UnsupportedFeaturePolicy, "fail", StringComparison.OrdinalIgnoreCase);
            foreach (var table in tablesToDeliver)
            {
                var applied = appliedByTable[table];
                var kept = applied.Where(x => string.Equals(x.Delivery.ContentEncoding, "Xml", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var (_, delivery) in applied.Where(x => !string.Equals(x.Delivery.ContentEncoding, "Xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var msg = $"JSON data delivery for {DataDeliveryHelper.GetTableKey(table, platform)}{VariantSuffix(delivery)} " +
                              $"requires SQL Server compatibility level 130 (target is at {context.SqlServerCompatibilityLevel}); " +
                              "re-encode this delivery as XML (\"ContentEncoding\": \"Xml\") to deploy it on a legacy-compat target.";
                    if (failFast)
                        throw new NotSupportedException(msg);
                    log($"    [SKIPPED - requires compatibility level 130 for JSON delivery] {msg}");
                }
                if (kept.Count != applied.Count)
                    appliedByTable[table] = kept;
            }
        }

        // CASCADE validation is scoped to the gated-IN (applied) deliveries only — a delivery
        // whose gate is false this run will never execute here, so its table must not be able to
        // false-abort the whole run just because a CASCADE FK exists on it (#278). Unlike
        // ValidateMergeTypes (config-validity, environment-independent, still checks every
        // non-None delivery), this check must reflect what the deliver loop is actually about to do.
        var cascadeErrors = ValidateDeleteCascade(context.Command, platform, context.DatabaseName,
            tablesToDeliver.Where(t => appliedByTable[t].Any(x =>
                    (x.Delivery.MergeType ?? "").IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(t => (
                    Schema: GetSchemaOrDb(t, context.DatabaseName, platform, context.SchemaName),
                    TableName: DataDeliveryHelper.TrimIdentifierQuotes(t.Name, platform)
                )).ToList());
        if (cascadeErrors.Count > 0)
        {
            foreach (var error in cascadeErrors)
                logError($"    {error}");
            throw new InvalidOperationException("Data delivery aborted: Delete merge type with CASCADE delete detected.");
        }

        tablesToDeliver = tablesToDeliver.Where(t => appliedByTable[t].Count > 0).ToList();
        if (tablesToDeliver.Count == 0) return;

        var deliverySet = DataDeliveryHelper.BuildDeliveryTableSet(tablesToDeliver, platform);

        var tableEdges = new Dictionary<IDeliverableTable, (HashSet<string> RequiredDeps, List<string> DeferredColumns)>();
        var tableDataMap = new Dictionary<(IDeliverableTable Table, int Index), string>();
        var contentFileErrors = new List<string>();

        foreach (var table in tablesToDeliver.ToList())
        {
            tableEdges[table] = DataDeliveryHelper.ClassifyFKEdges(table, deliverySet, platform);
            var tableKey = DataDeliveryHelper.GetTableKey(table, platform);

            foreach (var (index, delivery) in appliedByTable[table])
            {
                var contentPath = ResolveContentFilePath(context.TemplateRootPath, delivery.ContentFile);
                if (contentPath == null)
                {
                    contentFileErrors.Add($"{tableKey}: Unable to locate content file: '{delivery.ContentFile}'");
                    continue;
                }

                if (context.ReadFileContent == null)
                {
                    contentFileErrors.Add($"{tableKey}: No content file reader configured for '{contentPath}'");
                    continue;
                }

                try
                {
                    var content = context.ReadFileContent(contentPath);
                    if (content == null)
                    {
                        contentFileErrors.Add($"{tableKey}: Content file not found: '{contentPath}'");
                        continue;
                    }
                    tableDataMap[(table, index)] = content;
                }
                catch (Exception ex)
                {
                    contentFileErrors.Add($"{tableKey}: Error reading content file: '{contentPath}' - {ex.Message}");
                }
            }
        }

        if (contentFileErrors.Count > 0)
        {
            foreach (var error in contentFileErrors)
                logError($"    {error}");
            throw new InvalidOperationException("Data delivery aborted: Unable to read one or more content files.");
        }

        var delivered = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        var pass2Units = new List<(IDeliverableTable Table, int Index, DataDelivery Delivery, List<string> DeferredColumns)>();
        // Failing SQL per (table, delivery), recorded on failure but NOT written to an artifact yet.
        // A delivery that fails an early dependency-ordering pass often succeeds on retry, so writing
        // the artifact inline would leave a misleading "Failed DataDelivery" artifact on a green
        // deploy. An entry is removed when its delivery later succeeds, so what remains after every
        // pass is exactly the deliveries that never recovered — those are flushed to artifacts
        // alongside the aggregate failure below. (Keyed by artifact key = table key + variant/index,
        // so a table attempted in both the while loop and the circular-fallback loop yields one
        // artifact per failed delivery, not one per attempt.)
        var pendingArtifacts = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        // Per-delivery success tracking: a table with multiple deliveries can be re-attempted
        // (dependency retry loop + circular-fallback loop). A delivery that already succeeded on a
        // prior attempt must not re-run when a *sibling* delivery's failure re-queues the table.
        var deliverySucceeded = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        // Record (don't log) the FIRST failure reason per table. First-pass failures are usually
        // transient dependency-ordering ones that succeed on retry, so logging them would be noise on a
        // green deploy. But a single genuine failure can leave the shared connection unusable, making
        // *later* tables report that downstream symptom instead of their own root cause. Capturing the
        // first reason here lets the permanent-failure pass below report the real root, not the cascade.
        var firstFailureReason = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        var lastCount = -1;

        while (tablesToDeliver.Count > 0 && tablesToDeliver.Count != lastCount)
        {
            lastCount = tablesToDeliver.Count;
            var remaining = new List<IDeliverableTable>();

            foreach (var table in tablesToDeliver)
            {
                var (requiredDeps, deferredColumns) = tableEdges[table];

                if (requiredDeps.Any(dep => !delivered.Contains(dep)))
                {
                    remaining.Add(table);
                    continue;
                }

                try
                {
                    DeliverTable(context, table, tableDataMap, deferredColumns, delivered, pass2Units, false, pendingArtifacts, deliverySucceeded, appliedByTable[table]);
                }
                catch (Exception ex)
                {
                    var failedKey = DataDeliveryHelper.GetTableKey(table, context.Platform);
                    if (!firstFailureReason.ContainsKey(failedKey))
                        firstFailureReason[failedKey] = ex.Message;
                    remaining.Add(table);
                }
            }

            tablesToDeliver = remaining;
        }

        // Deliveries that survive the dependency-retry while-loop and still fail here (or in pass 2)
        // are PERMANENT failures — not transient dependency-ordering ones. They are logged (+ artifact
        // written) per delivery below, but a permanent failure must also fail the whole deploy (exit 2)
        // rather than shipping "green" with a silent data gap (#334). Accumulate them and throw one
        // aggregate at the very end, after every deliverable table has been attempted ("deliver what
        // you can, then fail").
        var permanentFailures = new List<string>();

        foreach (var table in tablesToDeliver)
        {
            try
            {
                DeliverTable(context, table, tableDataMap, new List<string>(), delivered, pass2Units, true, pendingArtifacts, deliverySucceeded, appliedByTable[table]);
            }
            catch (Exception ex)
            {
                var failedKey = DataDeliveryHelper.GetTableKey(table, context.Platform);
                // Prefer the first recorded failure reason: it is the real root, captured before any
                // connection-poisoning cascade rewrote later attempts into a downstream symptom.
                var rootReason = firstFailureReason.TryGetValue(failedKey, out var r) ? r : ex.Message;
                logError($"    Error delivering {failedKey}: {rootReason}");
                permanentFailures.Add(failedKey);
            }
        }

        foreach (var (table, index, delivery, _) in pass2Units)
        {
            try
            {
                var tableKey = DataDeliveryHelper.GetTableKey(table, platform);
                var variantLabel = !string.IsNullOrWhiteSpace(delivery.VariantName) ? delivery.VariantName : index.ToString();
                var artifactKey = $"{tableKey}#{variantLabel}";
                log($"    Delivering {tableKey}{VariantSuffix(delivery)} (pass 2 - updating deferred FK columns)");

                var schemaOrDb = GetSchemaOrDb(table, context.DatabaseName, platform, context.SchemaName);
                var keyColumns = string.IsNullOrWhiteSpace(delivery.MatchColumns)
                    ? helper.GetKeyColumns(context.Command, schemaOrDb, table.Name)
                    : delivery.MatchColumns;
                var tableData = tableDataMap.TryGetValue((table, index), out var data) ? data : "";
                var update = (delivery.MergeType ?? "").IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0;
                var delete = (delivery.MergeType ?? "").IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0;
                var mergeFilter = ResolveMergeFilter(delivery.MergeFilter, context.SchemaName);

                var mergeScript = helper.BuildMergeScript(context.Command, schemaOrDb, table.Name,
                    tableData, keyColumns, update, delete, delivery.MergeDisableTriggers, false, mergeFilter,
                    delivery.MergeDisableRules, delivery.MergeUpdateDescendents, context.PostgreSqlServerVersionNum, context.MySqlServerVersionNum,
                    delivery.ContentEncoding ?? "Json");

                if (!context.WhatIf)
                {
                    try
                    {
                        context.ExecuteScript?.Invoke(table.Name, mergeScript);
                    }
                    catch (Exception ex)
                    {
                        pendingArtifacts[artifactKey] = mergeScript;
                        logError($"    Error in pass 2 for {tableKey}: {ex.Message}");
                        permanentFailures.Add(tableKey);
                    }
                }
            }
            catch (Exception ex)
            {
                logError($"    Error in pass 2 for {DataDeliveryHelper.GetTableKey(table, platform)}: {ex.Message}");
                permanentFailures.Add(DataDeliveryHelper.GetTableKey(table, platform));
            }
        }

        // Flush the deferred failure artifacts. Only deliveries that never recovered remain in
        // pendingArtifacts (successful retries removed their entries), so each one is a genuine
        // permanent failure worth a "Failed DataDelivery" artifact — no artifact is left behind for a
        // delivery that failed an early pass but succeeded on retry.
        foreach (var kvp in pendingArtifacts)
            context.WriteResolvedSqlArtifact?.Invoke(kvp.Key, kvp.Value);

        if (permanentFailures.Count > 0)
        {
            var failed = permanentFailures.Distinct().ToList();
            throw new InvalidOperationException(
                $"Data delivery failed for {failed.Count} table(s): {string.Join(", ", failed)}");
        }
    }

    private void DeliverTable(DataDeliveryContext context, IDeliverableTable table,
        Dictionary<(IDeliverableTable Table, int Index), string> tableDataMap, List<string> deferredColumns,
        HashSet<string> delivered,
        List<(IDeliverableTable Table, int Index, DataDelivery Delivery, List<string> DeferredColumns)> pass2Units,
        bool isCircularFallback, Dictionary<string, string> pendingArtifacts, HashSet<string> deliverySucceeded,
        List<(int Index, DataDelivery Delivery)> appliedDeliveries)
    {
        var platform = context.Platform;
        var helper = context.ScriptHelper;
        var log = context.ProgressLog ?? (_ => { });
        var tableKey = DataDeliveryHelper.GetTableKey(table, platform);
        var schemaOrDb = GetSchemaOrDb(table, context.DatabaseName, platform, context.SchemaName);

        if (context.WhatIf)
        {
            foreach (var (_, delivery) in appliedDeliveries)
                log($"    Would DELIVER: {tableKey}{VariantSuffix(delivery)}");
            delivered.Add(tableKey);
            return;
        }

        foreach (var (index, delivery) in appliedDeliveries)
        {
            var variantLabel = !string.IsNullOrWhiteSpace(delivery.VariantName) ? delivery.VariantName : index.ToString();
            var artifactKey = $"{tableKey}#{variantLabel}";
            var deliveryKey = $"{tableKey}#{index}";

            if (deliverySucceeded.Contains(deliveryKey))
                continue; // already delivered on a prior attempt of this table; don't re-run an idempotent sibling

            var keyColumns = string.IsNullOrWhiteSpace(delivery.MatchColumns)
                ? helper.GetKeyColumns(context.Command, schemaOrDb, table.Name)
                : delivery.MatchColumns;
            var tableData = tableDataMap.TryGetValue((table, index), out var data) ? data : "";

            if (deferredColumns.Count > 0 && !isCircularFallback)
            {
                log($"    Delivering {tableKey}{VariantSuffix(delivery)} (pass 1 - deferred columns as NULL)");

                var mergeScript = BuildDeferredMergeScript(context, schemaOrDb, table, delivery, tableData, keyColumns, deferredColumns);

                try
                {
                    context.ExecuteScript?.Invoke(table.Name, mergeScript);
                }
                catch
                {
                    pendingArtifacts[artifactKey] = mergeScript;   // defer; flushed only if it never recovers
                    throw;
                }

                pendingArtifacts.Remove(artifactKey);   // pass-1 succeeded — clear any earlier deferred artifact
                pass2Units.Add((table, index, delivery, deferredColumns));
                deliverySucceeded.Add(deliveryKey);
            }
            else
            {
                log($"    Delivering {tableKey}{VariantSuffix(delivery)}");
                var update = (delivery.MergeType ?? "").IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0;
                var delete = (delivery.MergeType ?? "").IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0;
                var mergeFilter = ResolveMergeFilter(delivery.MergeFilter, context.SchemaName);
                var mergeScript = helper.BuildMergeScript(context.Command, schemaOrDb, table.Name,
                    tableData, keyColumns, update, delete, delivery.MergeDisableTriggers, false, mergeFilter,
                    delivery.MergeDisableRules, delivery.MergeUpdateDescendents, context.PostgreSqlServerVersionNum, context.MySqlServerVersionNum,
                    delivery.ContentEncoding ?? "Json");

                try
                {
                    context.ExecuteScript?.Invoke(table.Name, mergeScript);
                }
                catch
                {
                    pendingArtifacts[artifactKey] = mergeScript;   // defer; flushed only if it never recovers
                    throw;
                }
                pendingArtifacts.Remove(artifactKey);   // delivery succeeded — clear any earlier deferred artifact
                deliverySucceeded.Add(deliveryKey);
            }
        }

        delivered.Add(tableKey);
    }

    internal static string BuildDeferredMergeScript(DataDeliveryContext context, string schemaOrDb,
        IDeliverableTable table, DataDelivery delivery, string tableData, string keyColumns, List<string> deferredColumns)
    {
        return DeferredMergeBuilder.Build(context.ScriptHelper, context.Command, context.Platform,
            schemaOrDb, table.Name, tableData, keyColumns,
            delivery.MergeDisableTriggers, deferredColumns,
            delivery.MergeDisableRules, delivery.MergeUpdateDescendents,
            context.PostgreSqlServerVersionNum, delivery.ContentEncoding ?? "Json");
    }

    /// <summary>
    /// Validates that tables using Delete merge type don't have referencing FKs with
    /// CASCADE delete. Deletes would cascade to child tables, which is almost never
    /// intended during data delivery. Cross-platform (MySQL, PostgreSQL, SQL Server).
    /// </summary>
    internal static List<string> ValidateDeleteCascade(System.Data.IDbCommand cmd, string platform, string databaseName,
        IList<(string Schema, string TableName)> deleteTables)
    {
        var errors = new List<string>();
        if (deleteTables == null || deleteTables.Count == 0) return errors;

        var isMySql = platform.Equals("MySQL", StringComparison.OrdinalIgnoreCase);
        var dbName = (databaseName ?? string.Empty).Trim().Trim('`');

        foreach (var (schema, tableName) in deleteTables)
        {
            var schemaKey = (schema ?? string.Empty).Trim().Trim('`', '"', '[', ']');
            var tableKey = (tableName ?? string.Empty).Trim().Trim('`', '"', '[', ']');

            cmd.CommandText = isMySql
                ? BuildMySqlCascadeQuery(dbName, tableKey)
                : BuildStandardCascadeQuery(schemaKey, tableKey);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var fkName = reader.GetString(0);
                var childTable = reader.GetString(1);
                errors.Add(FormatCascadeError(platform, schemaKey, tableKey, fkName, childTable));
            }
        }

        return errors;
    }

    private static string BuildMySqlCascadeQuery(string databaseName, string tableName) => $@"
SELECT rc.CONSTRAINT_NAME, rc.TABLE_NAME, rc.DELETE_RULE
FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
WHERE BINARY rc.UNIQUE_CONSTRAINT_SCHEMA = BINARY '{databaseName.Replace("'", "''")}'
  AND BINARY rc.REFERENCED_TABLE_NAME = BINARY '{tableName.Replace("'", "''")}'
  AND rc.DELETE_RULE = 'CASCADE';
";

    private static string BuildStandardCascadeQuery(string schema, string tableName) => $@"
SELECT rc.CONSTRAINT_NAME, tc_c.TABLE_NAME, rc.DELETE_RULE
FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc_p
    ON tc_p.CONSTRAINT_NAME = rc.UNIQUE_CONSTRAINT_NAME
   AND tc_p.CONSTRAINT_SCHEMA = rc.UNIQUE_CONSTRAINT_SCHEMA
INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc_c
    ON tc_c.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
   AND tc_c.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA
WHERE tc_p.TABLE_SCHEMA = '{schema.Replace("'", "''")}'
  AND tc_p.TABLE_NAME = '{tableName.Replace("'", "''")}'
  AND rc.DELETE_RULE = 'CASCADE';
";

    private static string FormatCascadeError(string platform, string schema, string tableName, string fkName, string childTable)
    {
        var isSqlServer = platform.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        var isPg = platform.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);
        var (open, close) = isSqlServer ? ("[", "]") : isPg ? ("\"", "\"") : ("`", "`");
        string Q(string s) => $"{open}{s}{close}";

        var parent = isSqlServer || isPg ? $"{Q(schema)}.{Q(tableName)}" : Q(tableName);
        var child = isSqlServer || isPg ? $"{Q(schema)}.{Q(childTable)}" : Q(childTable);

        return $"Table {parent} uses MergeType=Insert/Update/Delete but is referenced by FK {Q(fkName)} on {child} with ON DELETE CASCADE. " +
               $"Deletes would cascade to {child}. Change MergeType to Insert/Update or remove the CASCADE rule.";
    }

    /// <summary>
    /// Resolves the schema (or database, on MySQL) qualifier for a table at delivery time.
    /// For schema-template iterations (<paramref name="iterationSchemaName"/> non-empty),
    /// substitutes the <c>{{SchemaName}}</c> token in <c>table.Schema</c> with the resolved
    /// iteration value before returning. Without this substitution the literal token would
    /// reach the catalog probes in <see cref="Schema.Utility.MergeScriptHelper"/> (returning
    /// empty result sets) and the emitted <c>MERGE INTO [{{SchemaName}}].[Table]</c> clause —
    /// slice-3 audit bug B3.
    /// Regular templates pass an empty <paramref name="iterationSchemaName"/> and the method
    /// behaves identically to its pre-bug-fix form.
    /// </summary>
    internal static string GetSchemaOrDb(IDeliverableTable table, string databaseName, string platform,
        string iterationSchemaName)
    {
        if (platform.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
            return databaseName;
        var raw = table.Schema ?? DataDeliveryHelper.GetDefaultSchema(platform);
        return string.IsNullOrEmpty(iterationSchemaName)
            ? raw
            : raw.Replace("{{SchemaName}}", iterationSchemaName);
    }

    internal static string ResolveContentFilePath(string templateRootPath, string contentFile)
    {
        if (string.IsNullOrEmpty(contentFile) || string.IsNullOrEmpty(templateRootPath))
            return null;

        // Normalize both separator styles to the platform separator so output is native on either OS.
        var normalized = contentFile
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        // An absolute contentFile is never inside the template root — reject it outright.
        if (Path.IsPathRooted(normalized))
            return null;

        var fullRoot = Path.GetFullPath(templateRootPath);
        // Append the (now guaranteed relative) suffix and canonicalize. Concatenate rather than
        // Path.Combine, which would discard fullRoot for a rooted suffix — already excluded above.
        var fullCombined = Path.GetFullPath(fullRoot + Path.DirectorySeparatorChar + normalized);

        // Require that the resolved path stays inside the template root (containment guard).
        // Append the separator before comparing so "/rootEvil" is not treated as inside "/root".
        if (!fullCombined.Equals(fullRoot, StringComparison.Ordinal) &&
            !fullCombined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        return fullCombined;
    }

    /// <summary>
    /// Substitutes the <c>{{SchemaName}}</c> token in a user-authored
    /// <see cref="DataDelivery.MergeFilter"/> with the iteration's resolved schema name.
    /// Mirrors the <see cref="GetSchemaOrDb"/> pattern (slice-3 audit B3) for the parallel
    /// case: MergeFilter is verbatim text embedded into the executed merge body and never
    /// passes through engine-level token resolution between the helper and the database.
    /// Without this substitution, schema-template authors who reference <c>{{SchemaName}}</c>
    /// in MergeFilter would see the literal token reach the server and produce a SQL parse
    /// error. Regular templates pass an empty <paramref name="iterationSchemaName"/> and
    /// MergeFilter is returned unchanged.
    /// </summary>
    internal static string ResolveMergeFilter(string mergeFilter, string iterationSchemaName)
    {
        if (string.IsNullOrEmpty(mergeFilter) || string.IsNullOrEmpty(iterationSchemaName))
            return mergeFilter;
        return mergeFilter.Replace("{{SchemaName}}", iterationSchemaName);
    }

    /// <summary>
    /// Resolves the full gate-token vocabulary — <c>{{SchemaName}}</c> plus the per-target version
    /// tokens (<c>{{ServerMajorVersion}}</c>, <c>{{CompatibilityLevel}}</c>) — in a delivery's
    /// <see cref="DataDelivery.ShouldApplyExpression"/> before it reaches <see cref="GateEvaluator.ShouldApply"/>,
    /// which sets the resolved text as the command's CommandText and executes it directly — there is no
    /// engine-level token resolution between here and the database. Uses <see cref="TokenHelper.AssembleGateTokens"/>,
    /// the same assembly <c>DatabaseQuench.ResolveFolderGateExpression</c> uses for folder gates, so this
    /// site speaks the same gate vocabulary rather than a narrower subset (N2 — this resolver used to
    /// substitute only <c>{{SchemaName}}</c>, leaving a version-token gate here to error against the server).
    /// </summary>
    internal static string ResolveGateExpression(string shouldApplyExpression, string iterationSchemaName,
        List<KeyValuePair<string, string>> versionTokens, Platform platform = Platform.SqlServer)
    {
        if (string.IsNullOrEmpty(shouldApplyExpression)) return shouldApplyExpression;
        var tokens = TokenHelper.AssembleGateTokens(iterationSchemaName, versionTokens);
        return tokens.Count == 0 ? shouldApplyExpression : SqlScript.TokenReplace(shouldApplyExpression, tokens, platform);
    }

    private static readonly string[] ValidMergeTypes =
        { "Insert", "Insert/Update", "Insert/Update/Delete" };

    private static List<string> ValidateMergeTypes(IList<IDeliverableTable> tables)
    {
        var errors = new List<string>();
        foreach (var table in tables)
        {
            foreach (var (_, delivery) in ApplicableDeliveries(table))
            {
                var mergeType = delivery.MergeType;
                if (ValidMergeTypes.Any(v => v.Equals(mergeType, StringComparison.OrdinalIgnoreCase))) continue;
                errors.Add($"Table {table.Name} has invalid MergeType '{mergeType}'. " +
                           $"Valid values: {string.Join(", ", ValidMergeTypes)}");
            }
        }
        return errors;
    }
}
