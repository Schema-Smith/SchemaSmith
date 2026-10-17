// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Index = Schema.Domain.Index;

namespace Schema.Validation.Checks;

/// <summary>
/// Structural cross-object reference checks the JSON schema can't express: FK local/related
/// columns, related-table resolution (incl. schema defaulting), FK column-count cardinality, and
/// index-column existence. Deliberately NO type-agreement checks and NO DeleteAction/UpdateAction
/// checks — those are out of scope for this check (see task brief); they belong to JSON-schema
/// lint or a future slice.
/// </summary>
public sealed class CoherenceCheck : ISchemaCheck
{
    private const string LocalColumnCode = "SS-FK-001";
    private const string RelatedTableCode = "SS-FK-002";
    private const string RelatedColumnCode = "SS-FK-004";
    private const string CardinalityCode = "SS-FK-005";
    private const string ForeignKeyNameReusedCode = "SS-FK-006";
    private const string SetNullColumnsCode = "SS-FK-007";
    private const string IndexColumnCode = "SS-IDX-001";
    private const string TemporalKeyShapeCode = "SS-IDX-002";
    private const string TemporalKeyPeriodCode = "SS-IDX-003";
    private const string NullsNotDistinctOnKeyCode = "SS-IDX-004";
    private const string PeriodPairIncompleteCode = "SS-TMP-001";
    private const string PeriodRoleRepeatedCode = "SS-TMP-002";
    private const string PeriodWithoutTemporalCode = "SS-TMP-003";
    private const string PeriodTypeCode = "SS-TMP-004";
    private const string LedgerColumnWithoutLedgerCode = "SS-LDG-001";
    private const string LedgerEndOnAppendOnlyCode = "SS-LDG-002";
    private const string LedgerRoleRepeatedCode = "SS-LDG-003";
    private const string LedgerViewWithoutLedgerCode = "SS-LDG-004";
    private const string LedgerColumnTypeCode = "SS-LDG-005";
    private const string SpatialColumnCode = "SS-SPX-001";
    private const string SpatialFamilyCode = "SS-SPX-002";
    private const string SpatialBoundingBoxCode = "SS-SPX-003";
    private const string SpatialGridsCode = "SS-SPX-004";
    private const string BackfillWithoutDefaultCode = "SS-COL-001";
    private const string CharsetWithoutCollationCode = "SS-COL-002";
    private const string RebuildThresholdCode = "SS-TBL-001";
    private const string IgnoredThresholdCode = "SS-TBL-002";
    private const string EdgeConstraintTableKindCode = "SS-TBL-003";
    private const string EdgeConstraintNodeCode = "SS-TBL-004";
    private const string RlsWithoutPoliciesCode = "SS-RLS-001";
    private const string PoliciesWithoutRlsCode = "SS-RLS-002";
    private const string ReplicaIdentityIndexMissingCode = "SS-RI-001";
    private const string QuotedIdentifierCode = "SS-IDENT-001";
    private const string IdentifierTooLongCode = "SS-IDENT-002";
    private const int PostgreSqlMaxIdentifierBytes = 63;
    private const string ReplicaIdentityIndexUnknownCode = "SS-RI-002";
    private const string ReplicaIdentityIndexNotUniqueCode = "SS-RI-003";
    private const string ReplicaIdentityIndexIgnoredCode = "SS-RI-004";
    private const string VersioningExclusionInertCode = "SS-SV-001";
    private const string MinimumVersionUnresolvableCode = "SS-VER-001";
    private const string CdcFilegroupInertCode = "SS-CDC-001";
    private const string CdcIndexNameInvalidCode = "SS-CDC-002";
    private const string FullTextKeyIndexInvalidCode = "SS-FT-001";
    private const string CompressionConflictCode = "SS-CO-001";
    private const string CompressionLevelInertCode = "SS-CO-002";
    private const string DuplicateEventCode = "SS-EVT-001";
    private const string DuplicateEnumTypeCode = "SS-ENUM-001";
    private const string DuplicateSequenceCode = "SS-SEQ-001";
    private const string DuplicateDomainTypeCode = "SS-DOM-001";
    private const string PartitionHalfDeclaredCode = "SS-PART-001";
    private const string PartitionAndFileGroupCode = "SS-PART-002";
    private const string MyPartitionRangeListNoPartitionsCode = "SS-PART-003";
    private const string MyPartitionHashBoundaryCode = "SS-PART-004";
    private const string MemoryOptimizedPlacementCode = "SS-XTP-001";
    private const string Category = "Coherence";

    public IEnumerable<Finding> Run(ValidationContext ctx)
    {
        // (schema, name) identity groups multiple GATED variants of the same logical table
        // together (DuplicationCheck already established this is one logical table, not a
        // collision) — union their columns so a column that exists only on one variant still
        // counts as present on "the table".
        var tablesByKey = ctx.AllTables
            .GroupBy(t => TableKey(t))
            .ToDictionary(g => g.Key, g => g.ToList());

        var findings = new List<Finding>();
        findings.AddRange(CheckMinimumVersion(ctx.Product));
        // A policy is resolved as a WHOLE object from the nearest level that declares one, so an unusable
        // policy at the product or template tier replaces an inherited one exactly as a table's does.
        findings.AddRange(CheckRebuildPolicy(ctx.Product.RebuildPolicy, $"Product '{ctx.Product.Name}'",
            $"Product '{ctx.Product.Name}'"));
        foreach (var template in ctx.Templates)
            findings.AddRange(CheckRebuildPolicy(template.RebuildPolicy, $"Template '{template.Name}'",
                $"Template '{template.Name}'"));
        foreach (var template in ctx.Templates)
            findings.AddRange(CheckScheduledEvents(template));

        foreach (var template in ctx.Templates)
            findings.AddRange(CheckForeignKeyNameReuse(template, ctx.Product));

        foreach (var template in ctx.Templates)
            findings.AddRange(CheckModeledFolderObjectCoexistence(template));

        foreach (var template in ctx.Templates)
            findings.AddRange(CheckPostgreSqlQuotedIdentifierOnModeledObjects(template));

        foreach (var template in ctx.Templates)
            findings.AddRange(IdentifierLengthFindings(ModeledObjectNamedParts(template), $"Template '{template.Name}'"));

        foreach (var template in ctx.Templates)
        foreach (var table in template.Tables)
        {
            var location = $"Template '{template.Name}' / Table '{table.Name}'";

            // Under IndexOnlyTableQuenches the package does not own the table's columns -- the table is
            // created outside it (a vendor product, a replicated copy) and the template manages only its
            // indexes and statistics. So there is no authoritative local column list to check anything
            // against, and the checks that need one cannot be evaluated rather than merely passing.
            // Suppressed on the FLAG, not on "the table declared no columns": columns authored under this
            // flag are ignored by the deploy, so validating against them would be validating against a
            // list the deploy does not use -- the same false error in a less obvious costume.
            var columnsAreOwnedElsewhere = template.IndexOnlyTableQuenches;

            foreach (var fk in table.ForeignKeys)
                findings.AddRange(CheckForeignKey(table, fk, location, tablesByKey, columnsAreOwnedElsewhere));

            foreach (var index in table.Indexes)
                findings.AddRange(CheckIndex(table, index, location, columnsAreOwnedElsewhere));

            findings.AddRange(CheckBackfill(table, location));
            findings.AddRange(CheckPeriodColumns(table, location));
            findings.AddRange(CheckLedgerColumns(table, location));
            findings.AddRange(CheckSpatialIndexes(table, location, columnsAreOwnedElsewhere));
            findings.AddRange(CheckEdgeConstraints(table, location, tablesByKey));
            findings.AddRange(CheckCharsetWithoutCollation(table, location));
            findings.AddRange(CheckRebuildPolicy(table.RebuildPolicy, $"Table '{table.Name}'", location));
            findings.AddRange(CheckRowLevelSecurity(table, location));
            findings.AddRange(CheckReplicaIdentity(table, location));
            findings.AddRange(CheckPostgreSqlQuotedIdentifier(table, location));
            findings.AddRange(CheckSystemVersioningExclusions(table, location));
            findings.AddRange(CheckCdcFilegroup(table, location));
            findings.AddRange(CheckCdcSupportsNetChanges(table, location));
            findings.AddRange(CheckCdcIndexName(table, location));
            findings.AddRange(CheckFullTextKeyIndex(table, location));
            findings.AddRange(CheckCompressionOptions(table, location));
            findings.AddRange(CheckPartitionPlacement(table, location));
            findings.AddRange(CheckMyPartitioning(table, location));
            findings.AddRange(CheckMemoryOptimizedPlacement(table, location));
        }

        return findings;
    }

    // MariaDB names foreign keys per table from 12.1 and per database before it.
    private const int MariaDbPerTableForeignKeyNames = 1201;

    /// <summary>
    /// The same foreign-key name on two tables in one template. Where the engine names foreign keys per database
    /// (MySQL) or per schema (SQL Server) the second one fails the deploy. MariaDB names them per table from 12.1,
    /// so there the reuse fails only on an older server: a warning, unless MinimumVersion already excludes those.
    /// PostgreSQL names them per table on every version.
    /// </summary>
    private static IEnumerable<Finding> CheckForeignKeyNameReuse(Template template, Product product)
    {
        if (product.Platform == Platform.PostgreSQL) yield break;
        var severity = Severity.Error;
        if (product.Platform == Platform.MariaDb)
        {
            if (VersionHelper.ParseDeclaredVersion(product.MinimumVersion, product.Platform) >= MariaDbPerTableForeignKeyNames)
                yield break;
            severity = Severity.Warning;
        }

        var scopeBySchema = product.Platform == Platform.SqlServer;
        var reused = template.Tables
            .SelectMany(t => t.ForeignKeys.Select(fk => (Table: t, Fk: fk)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Fk.Name))
            .GroupBy(x => (Schema: scopeBySchema ? NormalizedSchema(x.Table) : "", Name: IdentityKey(x.Fk.Name)))
            .Where(g => g.Select(x => TableKey(x.Table)).Distinct().Count() > 1);

        foreach (var group in reused)
        {
            var name = NormalizeIdentifier(group.First().Fk.Name);
            var tables = string.Join(", ", group.Select(x => $"'{x.Table.Name}'").Distinct());
            yield return new Finding(severity, ForeignKeyNameReusedCode, Category, $"Template '{template.Name}'",
                product.Platform == Platform.MariaDb
                    ? $"Foreign key '{name}' is declared on tables {tables}. MariaDB names foreign keys per database " +
                      "before 12.1, so the deploy fails on an older server. Rename one, or set the product's " +
                      "MinimumVersion to 12.1 or later."
                    : $"Foreign key '{name}' is declared on tables {tables}. {product.Platform} names foreign keys per " +
                      (scopeBySchema ? "schema" : "database") + ", so the second one fails the deploy. Rename one.");
        }
    }

    private static IEnumerable<Finding> CheckForeignKey(
        Table table,
        ForeignKey fk,
        string tableLocation,
        IReadOnlyDictionary<(string Schema, string Name), List<Table>> tablesByKey,
        bool columnsAreOwnedElsewhere)
    {
        var location = $"{tableLocation} / FK '{fk.Name}'";
        var localColumnNames = ColumnNames(table);
        var fkColumns = SplitNames(fk.Columns);
        var fkRelatedColumns = SplitNames(fk.RelatedColumns);

        // Only the LOCAL half is suppressed under IndexOnlyTableQuenches. The related table is a
        // different table, usually one the package does declare in full, so its column list stays
        // authoritative -- and the cardinality check below is a pure string-count comparison that
        // never needed a column list at all.
        if (!columnsAreOwnedElsewhere)
            foreach (var column in fkColumns.Where(column => !localColumnNames.Contains(NormalizeIdentifier(column))))
                yield return new Finding(Severity.Error, LocalColumnCode, Category, location,
                    $"Local column '{column}' referenced in Columns does not exist on table '{table.Name}'.");

        // Cardinality is a pure string-count comparison — independent of whether the related
        // table resolves, so it always runs.
        if (fkColumns.Count != fkRelatedColumns.Count)
            yield return new Finding(Severity.Error, CardinalityCode, Category, location,
                $"Columns has {fkColumns.Count} entries but RelatedColumns has {fkRelatedColumns.Count} — FK column lists must be the same length.");

        // SetNullColumns is the subset ON DELETE SET NULL applies to: it needs that action, and names only key columns.
        if (fk is PostgreSqlForeignKey { SetNullColumns: not null } pgFk && !string.IsNullOrWhiteSpace(pgFk.SetNullColumns))
        {
            if (!string.Equals(NormalizeAction(fk.DeleteAction), "SET NULL", StringComparison.OrdinalIgnoreCase))
                yield return new Finding(Severity.Error, SetNullColumnsCode, Category, location,
                    $"SetNullColumns is set but DeleteAction is '{fk.DeleteAction ?? "NO ACTION"}': the subset applies to ON DELETE SET NULL alone, so set DeleteAction to SET NULL or remove SetNullColumns.");
            var keyColumns = fkColumns.Select(NormalizeIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var column in SplitNames(pgFk.SetNullColumns).Where(column => !keyColumns.Contains(NormalizeIdentifier(column))))
                yield return new Finding(Severity.Error, SetNullColumnsCode, Category, location,
                    $"SetNullColumns names '{column}', which is not one of the key's Columns: only a key column can be set to NULL on delete.");
        }

        var (schema, name) = ResolveRelatedTarget(table, fk);

        if (!tablesByKey.TryGetValue((schema, name), out var relatedTables))
        {
            // A warning, not an error: the deploy creates a foreign key to any table that exists on the target,
            // declared or not, so an unresolved reference is only a likely mistake. Never silenced -- not even for
            // a partial deployment such as a SchemaShears patch, where it is expected: DropTablesRemovedFromProduct
            // false is also a common safety setting on a complete product, and there it would hide a real typo.
            yield return new Finding(Severity.Warning, RelatedTableCode, Category, location,
                    $"RelatedTable '{fk.RelatedTable}' does not resolve to any table in the package (resolved schema '{schema}'). The deploy succeeds only if it already exists on the target.");
            yield break;
        }

        var relatedColumnNames = new HashSet<string>(
            relatedTables.SelectMany(ColumnNames),
            StringComparer.OrdinalIgnoreCase);

        foreach (var column in fkRelatedColumns.Where(column => !relatedColumnNames.Contains(NormalizeIdentifier(column))))
            yield return new Finding(Severity.Error, RelatedColumnCode, Category, location,
                $"Related column '{column}' referenced in RelatedColumns does not exist on related table '{fk.RelatedTable}'.");
    }

    /// <summary>
    /// A declared <c>MinimumVersion</c> the engine cannot resolve aborts the deploy before any object is
    /// touched. That refusal is correct — the gap was that nothing said so EARLIER, so the author's first
    /// signal was a failed deployment.
    /// <para>This calls <see cref="VersionHelper.ParseDeclaredVersion"/>, the same function
    /// <c>ProductQuench.ValidateMinimumVersion</c> calls. That is deliberate rather than convenient: any
    /// re-implementation here could disagree with the deploy about what resolves, which is a worse version
    /// of the bug being fixed.</para>
    /// <para><c>MinimumVersion</c> carries no <c>[SchemaProperty]</c>, so no pattern reaches the generated
    /// <c>.json-schema</c> and <c>JsonSchemaCheck</c> cannot cover this. Only SQL Server can reject a
    /// WELL-FORMED value: a year >= 2000 is looked up in a closed table of release years, so 2013 / 2018 /
    /// 2020 / 2023 / 2024 are unresolvable while looking entirely plausible. PostgreSQL and the MySQL family
    /// parse arithmetically and fail only on a non-numeric value.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckMinimumVersion(Product product)
    {
        // Absent is not invalid -- MinimumVersion is optional, and the deploy returns early on it too.
        if (string.IsNullOrWhiteSpace(product.MinimumVersion))
            yield break;

        if (VersionHelper.ParseDeclaredVersion(product.MinimumVersion, product.Platform) != null)
            yield break;

        yield return new Finding(Severity.Error, MinimumVersionUnresolvableCode, Category,
            $"Product '{product.Name}'",
            $"MinimumVersion '{product.MinimumVersion}' is not a valid {product.Platform} version, so the "
            + "deploy will refuse this package before touching any object."
            + (product.Platform == Platform.SqlServer
                ? " On SQL Server a value of 2000 or more is read as a RELEASE YEAR and must be one of "
                  + "2008, 2012, 2014, 2016, 2017, 2019, 2022 or 2025 — an in-between year such as 2018 or "
                  + "2020 does not resolve. A major version number (13, 16) also works."
                : " Use a numeric version such as " + (product.Platform.GetBasePlatform() == Platform.MySQL
                    ? "'8.0' or '10.6'." : "'12' or '16'.")));
    }

    /// <summary>
    /// An edge constraint lives on an edge table and connects node tables. The table kind is an error (SQL Server
    /// refuses the constraint by name); a clause naming a table the package does not declare as a node is a warning,
    /// since the deploy succeeds when the node table already exists on the target.
    /// </summary>
    private static IEnumerable<Finding> CheckEdgeConstraints(Table table, string tableLocation,
        IReadOnlyDictionary<(string Schema, string Name), List<Table>> tablesByKey)
    {
        if (table is not SqlServerTable { EdgeConstraints.Count: > 0 } sqlTable) yield break;
        var schema = NormalizedSchema(table);
        foreach (var constraint in sqlTable.EdgeConstraints)
        {
            var location = $"{tableLocation} / EdgeConstraint '{constraint.Name}'";
            if (!string.Equals(sqlTable.GraphType, "Edge", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(Severity.Error, EdgeConstraintTableKindCode, Category, location,
                    $"Table '{table.Name}' declares an edge constraint but its GraphType is '{sqlTable.GraphType ?? "None"}': only an edge table (GraphType \"Edge\") can carry one.");
                continue;
            }
            foreach (var clause in constraint.Clauses ?? [])
                foreach (var name in new[] { clause.FromTable, clause.ToTable }.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    var parts = SplitNames(name.Replace(".", ","));
                    var key = (parts.Count > 1 ? IdentityKey(parts[0]) : schema, IdentityKey(parts[^1]));
                    if (tablesByKey.TryGetValue(key, out var targets) && targets.OfType<SqlServerTable>().Any(x => string.Equals(x.GraphType, "Node", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var resolvedSchema = parts.Count > 1 ? NormalizeIdentifier(parts[0]) : NormalizeIdentifier((table as IDeliverableTable)?.Schema ?? "");
                    yield return new Finding(Severity.Warning, EdgeConstraintNodeCode, Category, location,
                        $"Clause table '{name}' is not a node table in the package (resolved '{resolvedSchema}.{NormalizeIdentifier(parts[^1])}'). The deploy succeeds only if a node table of that name already exists on the target.");
                }
        }
    }

    /// <summary>
    /// A SQL Server spatial index covers one geometry or geography column with a tessellation of the same family; a
    /// geometry index needs a bounding box with an area and a geography index cannot have one; GRIDS belong to the GRID
    /// schemes. The engine refuses each other shape at create time, so it is reported here. An index-only template owns
    /// no columns, so the column checks are skipped there, as for ordinary indexes.
    /// </summary>
    private static IEnumerable<Finding> CheckSpatialIndexes(Table table, string tableLocation, bool columnsAreOwnedElsewhere)
    {
        if (table is not SqlServerTable sqlTable) yield break;
        foreach (var index in sqlTable.SpatialIndexes)
        {
            var location = $"{tableLocation} / Spatial index '{index.Name}'";
            var tessellation = index.Tessellation?.Trim().ToUpperInvariant();
            string family = null;
            if (!columnsAreOwnedElsewhere)
            {
                var column = sqlTable.Columns.FirstOrDefault(c => string.Equals(NormalizeIdentifier(c.Name), NormalizeIdentifier(index.Column), StringComparison.OrdinalIgnoreCase));
                var type = column?.DataType?.Trim().Trim('[', ']').ToUpperInvariant();
                if (type is not ("GEOMETRY" or "GEOGRAPHY"))
                {
                    yield return new Finding(Severity.Error, SpatialColumnCode, Category, location,
                        column == null
                            ? $"Spatial index '{index.Name}' names column '{index.Column}', which the table does not declare."
                            : $"Spatial index '{index.Name}' is on column '{index.Column}', which is {column.DataType}: a spatial index needs a geometry or geography column.");
                    continue;
                }
                family = type;
            }
            family ??= tessellation?.StartsWith("GEOGRAPHY") == true ? "GEOGRAPHY" : tessellation?.StartsWith("GEOMETRY") == true ? "GEOMETRY" : null;
            if (tessellation != null && family != null && !tessellation.StartsWith(family))
            {
                yield return new Finding(Severity.Error, SpatialFamilyCode, Category, location,
                    $"Spatial index '{index.Name}' uses {index.Tessellation} on a {family.ToLowerInvariant()} column: use {family}_GRID or {family}_AUTO_GRID.");
                continue;
            }
            if (family == "GEOMETRY" && index.BoundingBox == null)
                yield return new Finding(Severity.Error, SpatialBoundingBoxCode, Category, location,
                    $"Spatial index '{index.Name}' is on a geometry column and declares no BoundingBox, which a geometry spatial index requires.");
            else if (family == "GEOGRAPHY" && index.BoundingBox != null)
                yield return new Finding(Severity.Error, SpatialBoundingBoxCode, Category, location,
                    $"Spatial index '{index.Name}' is on a geography column, which takes no BoundingBox.");
            else if (index.BoundingBox is { } box && (box.XMax <= box.XMin || box.YMax <= box.YMin))
                yield return new Finding(Severity.Error, SpatialBoundingBoxCode, Category, location,
                    $"Spatial index '{index.Name}' has a BoundingBox with no area: XMax must exceed XMin and YMax must exceed YMin.");
            if (index.Grids != null && tessellation?.EndsWith("_AUTO_GRID") == true)
                yield return new Finding(Severity.Error, SpatialGridsCode, Category, location,
                    $"Spatial index '{index.Name}' declares Grids on {index.Tessellation}, which chooses its own: Grids apply to GEOMETRY_GRID and GEOGRAPHY_GRID.");
        }
    }

    /// <summary>
    /// A SQL Server temporal table's period columns are a pair -- one RowStart, one RowEnd, both DATETIME2 -- on a
    /// table with IsTemporal. The engine refuses every other shape, so the package is stopped here instead.
    /// </summary>
    private static readonly HashSet<string> PeriodRoles = new(StringComparer.OrdinalIgnoreCase) { "RowStart", "RowEnd" };
    private static readonly HashSet<string> LedgerRoles = new(StringComparer.OrdinalIgnoreCase)
        { "TransactionIdStart", "TransactionIdEnd", "SequenceNumberStart", "SequenceNumberEnd" };

    /// <summary>
    /// A ledger table's generated columns and view exist only on a ledger table: an append-only one has the two Start
    /// columns and no End ones, each role is one column, and SQL Server makes them BIGINT. Every other shape fails the
    /// CREATE TABLE, so it is stopped here.
    /// </summary>
    private static IEnumerable<Finding> CheckLedgerColumns(Table table, string tableLocation)
    {
        if (table is not SqlServerTable sqlTable) yield break;
        var ledger = sqlTable.Ledger?.Trim();
        var isLedger = !string.IsNullOrEmpty(ledger) && !string.Equals(ledger, "Off", StringComparison.OrdinalIgnoreCase);
        var columns = sqlTable.Columns.OfType<SqlServerColumn>().Where(c => LedgerRoles.Contains(c.GeneratedAlways?.Trim() ?? "")).ToList();
        if (sqlTable.LedgerView != null && !isLedger)
            yield return new Finding(Severity.Error, LedgerViewWithoutLedgerCode, Category, tableLocation,
                $"Table '{table.Name}' declares a LedgerView but no Ledger: a ledger view exists only beside a ledger table.");
        if (columns.Count == 0) yield break;
        if (!isLedger)
        {
            yield return new Finding(Severity.Error, LedgerColumnWithoutLedgerCode, Category, tableLocation,
                $"Column '{columns[0].Name}' declares GeneratedAlways {columns[0].GeneratedAlways}, a ledger column, but table '{table.Name}' declares no Ledger.");
            yield break;
        }
        foreach (var column in columns.Where(c => string.Equals(ledger, "AppendOnly", StringComparison.OrdinalIgnoreCase) && c.GeneratedAlways.Trim().EndsWith("End", StringComparison.OrdinalIgnoreCase)))
            yield return new Finding(Severity.Error, LedgerEndOnAppendOnlyCode, Category, tableLocation,
                $"Column '{column.Name}' declares GeneratedAlways {column.GeneratedAlways} on an append-only ledger table, which has no End columns: rows are never updated or deleted.");
        foreach (var group in columns.GroupBy(c => c.GeneratedAlways.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return new Finding(Severity.Error, LedgerRoleRepeatedCode, Category, tableLocation,
                $"Columns {string.Join(", ", group.Select(c => $"'{c.Name}'"))} all declare GeneratedAlways {group.Key}: a ledger table has one column of each role.");
        foreach (var column in columns.Where(c => !Regex.IsMatch(c.DataType ?? "", @"^\s*\[?BIGINT\]?\s*$", RegexOptions.IgnoreCase)))
            yield return new Finding(Severity.Error, LedgerColumnTypeCode, Category, tableLocation,
                $"Ledger column '{column.Name}' is {column.DataType}: SQL Server makes a ledger table's generated columns BIGINT.");
    }

    private static IEnumerable<Finding> CheckPeriodColumns(Table table, string tableLocation)
    {
        if (table is not SqlServerTable sqlTable) yield break;
        var periods = sqlTable.Columns.OfType<SqlServerColumn>().Where(c => PeriodRoles.Contains(c.GeneratedAlways?.Trim() ?? "")).ToList();
        if (periods.Count == 0) yield break;

        var byRole = periods.GroupBy(c => c.GeneratedAlways.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (role, columns) in byRole.Where(r => r.Value.Count > 1))
            yield return new Finding(Severity.Error, PeriodRoleRepeatedCode, Category, tableLocation,
                $"Columns {string.Join(", ", columns.Select(c => $"'{c.Name}'"))} all declare GeneratedAlways {role}: a table has one RowStart and one RowEnd.");
        foreach (var (have, missing) in new[] { ("RowStart", "RowEnd"), ("RowEnd", "RowStart") })
            if (byRole.TryGetValue(have, out var present) && !byRole.ContainsKey(missing))
                yield return new Finding(Severity.Error, PeriodPairIncompleteCode, Category, tableLocation,
                    $"Column '{present[0].Name}' declares GeneratedAlways {have} but no column declares {missing}: PERIOD FOR SYSTEM_TIME needs both.");
        if (!sqlTable.IsTemporal)
            yield return new Finding(Severity.Error, PeriodWithoutTemporalCode, Category, tableLocation,
                $"Table '{table.Name}' declares period columns but not IsTemporal: they are created only with system versioning, so set IsTemporal or remove GeneratedAlways.");
        foreach (var column in periods.Where(c => !Regex.IsMatch(c.DataType ?? "", @"^\s*\[?DATETIME2\]?\s*(\(\s*\d\s*\))?\s*$", RegexOptions.IgnoreCase)))
            yield return new Finding(Severity.Error, PeriodTypeCode, Category, tableLocation,
                $"Period column '{column.Name}' is {column.DataType}: a period column must be DATETIME2 or DATETIME2(n).");
    }

    /// <summary>
    /// BackfillExistingRows renders as ALTER TABLE ... WITH VALUES, which SQL Server rejects as a SYNTAX
    /// error when the column has no DEFAULT — so the deploy path only emits it alongside one. That guard
    /// keeps the batch runnable but makes the setting a silent no-op, which is the shape worth catching
    /// here: the author asked for existing rows to be populated and nothing would populate them.
    /// </summary>
    private static IEnumerable<Finding> CheckBackfill(Table table, string tableLocation)
    {
        foreach (var column in table.Columns.OfType<SqlServerColumn>()
                     .Where(c => c.BackfillExistingRows && string.IsNullOrWhiteSpace(c.Default)))
            yield return new Finding(Severity.Warning, BackfillWithoutDefaultCode, Category, tableLocation,
                $"Column '{column.Name}' sets BackfillExistingRows but has no Default, so there is no value to " +
                "apply to existing rows and the setting has no effect.");
    }

    /// <summary>
    /// A MySQL-family column that names a character set and no collation gets that character set's DEFAULT
    /// collation, which is not the table's and differs between engine versions (utf8mb4 defaults to
    /// utf8mb4_0900_ai_ci on MySQL 8, utf8mb4_uca1400_ai_ci on MariaDB 11.4, utf8mb4_general_ci on older ones).
    /// The deploy gives a column in the table's own character set the table's collation; one in another set it
    /// cannot place, so it says so here. A table that declares no collation has nothing to compare against.
    /// </summary>
    private static IEnumerable<Finding> CheckCharsetWithoutCollation(Table table, string tableLocation)
    {
        if (table is not MySqlTable { Collation: { } tableCollation } mySqlTable || string.IsNullOrWhiteSpace(tableCollation))
            yield break;
        var tableCharset = CharsetOfCollation(tableCollation.Trim(), mySqlTable.CharacterSet);
        if (tableCharset == null) yield break;

        foreach (var column in table.Columns.OfType<MySqlColumn>()
                     .Where(c => !string.IsNullOrWhiteSpace(c.CharacterSet) && string.IsNullOrWhiteSpace(c.Collation)
                                 && !string.Equals(CanonicalCharset(c.CharacterSet), tableCharset, StringComparison.OrdinalIgnoreCase)))
            yield return new Finding(Severity.Warning, CharsetWithoutCollationCode, Category, tableLocation,
                $"Column '{column.Name}' declares CharacterSet '{column.CharacterSet.Trim()}' but no Collation, so it " +
                $"gets that character set's default collation, which depends on the engine version. Add a Collation " +
                $"to choose one; the table's own '{tableCollation.Trim()}' belongs to a different character set.");
    }

    // A collation name starts with its character set's (charset names hold no underscore), except MariaDB 11.4's short
    // uca1400_* names, which take the table's character set -- or, where it declares none, the database default, which
    // validation cannot see (null). The deploy resolves both forms the same way before it compares.
    private static string CharsetOfCollation(string collation, string tableCharset)
    {
        if (collation.StartsWith("uca1400_", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(tableCharset) ? null : CanonicalCharset(tableCharset);
        var underscore = collation.IndexOf('_');
        return CanonicalCharset(underscore > 0 ? collation[..underscore] : collation);
    }

    // utf8 is utf8mb3 under its older name.
    private static string CanonicalCharset(string charset) =>
        string.Equals(charset.Trim(), "utf8", StringComparison.OrdinalIgnoreCase) ? "utf8mb3" : charset.Trim();

    /// <summary>
    /// Error, not Warning — the distinction from SS-COL-001 is the point. A BackfillExistingRows with no
    /// Default is INERT: the deploy runs, the setting simply does nothing, and a warning is proportionate.
    /// A THRESHOLD mode with no threshold is UNEVALUABLE: there is no number to compare pending changes
    /// against, so a deploy would have to invent a behaviour — alter in place, or rebuild — and either
    /// choice is a guess about what the author meant.
    /// <para>Only the table's OWN declared policy is examined. The deploy-time cascade (environment,
    /// product, template) is not visible from a package-authoring check, and a table that declares
    /// nothing here is not the level that would be at fault.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckRebuildPolicy(RebuildPolicy policy, string owner, string location)
    {
        if (policy == null) yield break;
        // Any whitespace, as the model trims it before the deploy sees it.
        var isThreshold = string.Equals(policy.Mode?.Trim(), "THRESHOLD", StringComparison.OrdinalIgnoreCase);

        if (isThreshold && policy.Threshold is not >= 1)
            yield return new Finding(Severity.Error, RebuildThresholdCode, Category, location,
                $"{owner} sets RebuildPolicy.Mode 'THRESHOLD' but no Threshold of 1 or more. " +
                "THRESHOLD needs a threshold to compare against, so the policy cannot be evaluated — set a " +
                "Threshold, or choose Mode 'ALWAYS' or 'NEVER'. A policy declared here also replaces any " +
                "inherited one, so leaving it unusable blocks rebuilds an outer level asked for.");

        // Mode defaults to NEVER, so a Threshold written without Mode 'THRESHOLD' is not a threshold -- and the
        // declared policy still replaces an inherited one whole, so {"Threshold":50} alone BLOCKS rebuilds.
        if (!isThreshold && policy.Threshold != null)
            yield return new Finding(Severity.Warning, IgnoredThresholdCode, Category, location,
                $"{owner} sets RebuildPolicy.Threshold {policy.Threshold} but Mode is '{policy.Mode}', so the " +
                "threshold is ignored. An omitted Mode defaults to NEVER, and because a policy declared here " +
                "replaces any inherited one whole, it can block rebuilds an outer level asked for. Set Mode " +
                "'THRESHOLD' to use the threshold, or remove it.");
    }
    /// <summary>
    /// Row-level security and its policies are two halves of one feature, and each half on its own
    /// fails silently in an opposite direction.
    /// <para><b>RLS with no policies denies everything.</b> PostgreSQL returns no rows to any user but
    /// the table owner, so a package that enables the flag and declares nothing else locks the table.</para>
    /// <para><b>Policies with no RLS enforce nothing.</b> The policies are created, so the package reads
    /// as secured, but PostgreSQL applies none of them until row-level security is on.</para>
    /// <para>Warning rather than Error: both are legal, deployable configurations, and policies may
    /// genuinely be managed outside the package. Refusing to deploy either would be worse than saying so.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckRowLevelSecurity(Table table, string tableLocation)
    {
        if (table is not PostgreSqlTable pgTable) yield break;

        var hasPolicies = pgTable.Policies.Count > 0;

        if (pgTable.RowLevelSecurity && !hasPolicies)
            yield return new Finding(Severity.Warning, RlsWithoutPoliciesCode, Category, tableLocation,
                $"Table '{table.Name}' sets RowLevelSecurity but declares no Policies. PostgreSQL returns " +
                "no rows to anyone except the table owner until at least one permissive policy exists, so " +
                "this locks the table rather than merely restricting it — declare a Policies entry, or " +
                "turn RowLevelSecurity off.");

        if (!pgTable.RowLevelSecurity && hasPolicies)
            yield return new Finding(Severity.Warning, PoliciesWithoutRlsCode, Category, tableLocation,
                $"Table '{table.Name}' declares Policies but does not set RowLevelSecurity. The policies " +
                "are created and then enforced against nothing, so the table is readable by anyone with " +
                "table privileges — set RowLevelSecurity to true to enforce them.");
    }


    /// <param name="columnsAreOwnedElsewhere">
    /// The template sets IndexOnlyTableQuenches, so the table's columns live outside the package and the
    /// key parts here legitimately name columns it never declares. Indexing a vendor-owned table is the
    /// whole point of that flag, so reporting its key parts as missing columns fails a package that
    /// deploys perfectly well -- and `--Validate` exits 2, which fails the user's CI gate.
    /// </param>
    private static IEnumerable<Finding> CheckIndex(Table table, Index index, string tableLocation,
        bool columnsAreOwnedElsewhere)
    {
        if (columnsAreOwnedElsewhere) yield break;

        var location = $"{tableLocation} / Index '{index.Name}'";
        var localColumnNames = ColumnNames(table);
        var keyParts = SplitNames(index.IndexColumns);

        // A primary key's columns cannot be NULL, so NULLS NOT DISTINCT decides nothing there and the engine never
        // records it on one. The deploy leaves it off the key rather than re-creating the key on every run.
        if (index is PostgreSqlIndex { NullsNotDistinct: true } && index.PrimaryKey)
            yield return new Finding(Severity.Warning, NullsNotDistinctOnKeyCode, Category, location,
                $"Index '{index.Name}' sets NullsNotDistinct on a primary key, whose columns cannot be NULL: the clause has no effect and is not applied.");

        // A temporal key (WITHOUT OVERLAPS) is a primary key or unique key; on MariaDB its last part names one of the
        // table's periods rather than a column, so that part is not a missing column.
        var withoutOverlaps = index switch
        {
            PostgreSqlIndex p => p.WithoutOverlaps,
            MariaDbIndex m => m.WithoutOverlaps,
            _ => false
        };
        var periodNames = table is MariaDbTable mariaDb
            ? mariaDb.Periods.Select(p => NormalizeIdentifier(p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastPartIsPeriod = withoutOverlaps && table is MariaDbTable && keyParts.Count > 0
                               && periodNames.Contains(NormalizeIdentifier(StripOrderingSuffix(keyParts[^1])));
        if (withoutOverlaps)
        {
            var isKey = index.PrimaryKey || (table is MariaDbTable ? index.Unique || index.UniqueConstraint : index.UniqueConstraint);
            if (!isKey)
                yield return new Finding(Severity.Error, TemporalKeyShapeCode, Category, location,
                    $"Index '{index.Name}' sets WithoutOverlaps but is not a primary key or unique " +
                    (table is MariaDbTable ? "key" : "constraint") + ": WITHOUT OVERLAPS is a clause of those alone, so the key would be created without it.");
            if (table is MariaDbTable && !lastPartIsPeriod)
                yield return new Finding(Severity.Error, TemporalKeyPeriodCode, Category, location,
                    $"Index '{index.Name}' sets WithoutOverlaps but its last IndexColumns part is not one of the table's Periods" +
                    (periodNames.Count == 0 ? " (the table declares none)" : "") + ": MariaDB declares a temporal key over a period, named last.");
        }

        // A SQL Server graph table's pseudo-columns are key parts a package can name without declaring them:
        // $node_id on a node table; $edge_id, $from_id and $to_id on an edge table.
        var graphPseudoColumns = (table as SqlServerTable)?.GraphType switch
        {
            "Node" => new[] { "$node_id" },
            "Edge" => new[] { "$edge_id", "$from_id", "$to_id" },
            _ => Array.Empty<string>()
        };

        var hasPrefixKeyParts = table is MySqlTable;
        foreach (var column in keyParts.Take(lastPartIsPeriod ? keyParts.Count - 1 : keyParts.Count).Select(StripOrderingSuffix)
                     .Select(column => hasPrefixKeyParts ? StripPrefixLength(column) : column)
                     .Where(column => !IsExpressionKeyPart(column))
                     .Where(column => !graphPseudoColumns.Contains(NormalizeIdentifier(column), StringComparer.OrdinalIgnoreCase))
                     .Where(column => !localColumnNames.Contains(NormalizeIdentifier(column))))
            yield return new Finding(Severity.Error, IndexColumnCode, Category, location,
                $"Index column '{column}' referenced in IndexColumns does not exist on table '{table.Name}'.");
    }

    /// <summary>
    /// Resolves a FK's target (schema, name). Schema precedence: RelatedTableSchema (SS/PG
    /// platform property — SchemaDefaultResolver.ResolveRelatedTableSchema always fills this
    /// during Template.Load with a concrete schema, so it's never ambiguous) if set, else a
    /// "schema." prefix parsed off RelatedTable, else the OWNING table's schema — this last
    /// default is what makes an unqualified same-schema reference work, including inside a
    /// schema template where every table's schema is the same "{{SchemaName}}" token. On MySQL,
    /// RelatedTableSchema is always null and MySqlTable.Schema is always null, so resolution
    /// collapses to Name-only identity — consistent with the (schema,name) table lookup.
    /// </summary>
    private static (string Schema, string Name) ResolveRelatedTarget(Table owningTable, ForeignKey fk)
    {
        var relatedTableSchema = (fk as IDeliverableForeignKey)?.RelatedTableSchema;
        var hasExplicitSchemaProperty = !string.IsNullOrEmpty(relatedTableSchema);

        // A dot inside a delimited name (`order.detail`) is part of the name, not a schema separator.
        var platform = owningTable switch
        {
            MySqlTable => Platform.MySQL,
            PostgreSqlTable => Platform.PostgreSQL,
            _ => Platform.SqlServer
        };
        var (prefixSchema, unqualified) = Identifier.SplitQualifiedName(fk.RelatedTable ?? "", platform);
        var name = IdentityKey(unqualified);

        var schema = hasExplicitSchemaProperty ? relatedTableSchema
            : prefixSchema ?? NormalizedSchema(owningTable);

        return (IdentityKey(schema), name);
    }

    // IDeliverableTable.Schema is resolved uniformly across platforms (SchemaDefaultResolver
    // fills "dbo"/"public"/the "{{SchemaName}}" token; MySqlTable's explicit interface
    // implementation always returns null) — matches the identity accessor DuplicationCheck uses.
    // Both sides of the identity comparison must strip identifier wrapping. SchemaDefaultResolver
    // preserves a declared Schema verbatim -- "[dbo]" stays bracketed -- but an FK that OMITS
    // RelatedTableSchema has it filled with the platform default, "dbo", unbracketed. Comparing the raw
    // strings therefore made every such FK unresolvable, which is the ordinary hand-authored shape: two
    // shipped demos reported SS-FK-002 against a table sitting in the same template. Packages that spell
    // RelatedTableSchema out explicitly matched by luck, because then both sides carry the brackets.
    private static string IdentityKey(string identifier) =>
        NormalizeIdentifier(identifier).ToLowerInvariant();

    private static (string Schema, string Name) TableKey(Table table) =>
        (NormalizedSchema(table), IdentityKey(table.Name));

    private static string NormalizedSchema(Table table) =>
        IdentityKey((table as IDeliverableTable)?.Schema ?? "");

    private static HashSet<string> ColumnNames(Table table) =>
        new(table.Columns.Select(c => NormalizeIdentifier(c.Name ?? "")), StringComparer.OrdinalIgnoreCase);

    // Mirrors SchemaSmith_StripBacktickWrapping.sql (source of truth for the backtick case — keep in
    // sync), generalized to the other two engines' wrapping so a hand-authored/hand-edited package
    // can mix quoting styles without producing a false SS-FK-*/SS-IDX-001: backtick (MySQL/MariaDB),
    // [bracket] (SQL Server), "double-quote" (PostgreSQL). Strips only a matched pair around the
    // WHOLE identifier — a lone/unbalanced quote character is left untouched, and interior content is
    // never touched (in particular, no expression key part reaches here — those are filtered out
    // before comparison). Comparison is OrdinalIgnoreCase everywhere in this class, so an unwrapped
    // PostgreSQL identifier (folds to lower case) and a "quoted" one (case-sensitive) are compared
    // case-insensitively too — a deliberate slight loosening: the failure mode this check should have
    // is a missed nit, never a false error on a valid package.
    private static string NormalizeAction(string action) =>
        System.Text.RegularExpressions.Regex.Replace((action ?? "").Trim(), @"\s+", " ").ToUpperInvariant();

    private static string NormalizeIdentifier(string identifier)
    {
        var trimmed = (identifier ?? "").Trim();
        if (trimmed.Length < 2)
            return trimmed;

        if (trimmed[0] == '`' && trimmed[^1] == '`')
            return trimmed[1..^1].Replace("``", "`");
        if (trimmed[0] == '[' && trimmed[^1] == ']')
            return trimmed[1..^1];
        if (trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];

        return trimmed;
    }

    // Mirrors SchemaSmith_NormalizeIndexColumns.sql's top-level-comma split (source of truth — keep
    // in sync, along with StripOrderingSuffix below): splits on a comma only at paren depth 0 and
    // outside a backtick-quoted span, so a functional/expression key part's own internal comma (e.g.
    // `(concat(\`a\`,\`b\`))`) isn't mistaken for a key-part boundary. Also used for FK Columns/
    // RelatedColumns — a no-op there, since FK column lists never contain parens or backticks.
    private static List<string> SplitNames(string csv)
    {
        var text = csv ?? "";
        var len = text.Length;
        var result = new List<string>();
        var pos = 0;

        while (pos < len)
        {
            var depth = 0;
            var inBacktick = false;
            var comma = -1;

            for (var scan = pos; scan < len; scan++)
            {
                var c = text[scan];
                if (c == '`')
                    inBacktick = !inBacktick;
                else if (!inBacktick && c == '(')
                    depth++;
                else if (!inBacktick && c == ')')
                    depth--;
                else if (!inBacktick && c == ',' && depth == 0)
                {
                    comma = scan;
                    break;
                }
            }
            if (comma < 0)
                comma = len;

            var part = text[pos..comma].Trim();
            if (part.Length > 0)
                result.Add(part);

            pos = comma + 1;
        }

        return result;
    }

    /// <summary>
    /// A <c>"</c> in a PostgreSQL object name is refused, because no stored form of such a name deploys.
    /// <para>
    /// The quench re-wraps a stored name as <c>'"' || Name || '"'</c> without escaping, so a bare
    /// <c>a"b</c> emits <c>"a"b"</c> — invalid DDL — while the escaped form <c>a""b</c> would emit
    /// correctly but is never what extraction writes, since extraction writes the RAW catalog name. The
    /// two directions disagree, so no consumer can be correct by construction, and the failure lands at
    /// deploy time as a syntax error that names nothing useful.
    /// </para>
    /// <para>
    /// Refusing here is deliberately the 2.7.0 answer rather than escaping the ~101 emission sites: it
    /// states the limit, fails at lint time with something actionable, and cannot break a working package
    /// because such a package has never deployed. Supporting the name properly is 2.8.0 work — see the
    /// roadmap entry — and when it lands this check is what gets deleted.
    /// </para>
    /// </summary>
    private static IEnumerable<Finding> CheckPostgreSqlQuotedIdentifier(Table table, string tableLocation)
    {
        if (table is not PostgreSqlTable pgTable) return [];
        return QuotedIdentifierFindings(NamedParts(pgTable), tableLocation)
            .Concat(IdentifierLengthFindings(NamedParts(pgTable), tableLocation));
    }

    /// <summary>
    /// PostgreSQL truncates an identifier longer than 63 bytes, with only a NOTICE. The object is created under the
    /// shorter name, the next deploy looks for the declared one, misses it, and fails creating it again. The limit is
    /// in bytes (NAMEDATALEN - 1), so a name in multi-byte characters reaches it sooner than its length suggests.
    /// </summary>
    private static IEnumerable<Finding> IdentifierLengthFindings(IEnumerable<(string Kind, string Name)> parts, string location)
    {
        foreach (var (kind, name) in parts)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var bare = name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(bare);
            if (bytes <= PostgreSqlMaxIdentifierBytes) continue;
            yield return new Finding(Severity.Error, IdentifierTooLongCode, Category, location,
                $"{kind} '{name}' is {bytes} bytes long. PostgreSQL truncates identifiers to {PostgreSqlMaxIdentifierBytes} " +
                "bytes, so it would be stored under a shorter name and the next deploy, not finding the declared one, " +
                $"would fail creating it again. Shorten it to {PostgreSqlMaxIdentifierBytes} bytes or fewer.");
        }
    }

    /// <summary>
    /// The modeled-object populations — materialized views, enum types, sequences, domain types — which
    /// reach the identical unescaped re-wrap as a table's own names and were outside the check's original
    /// table-only population.
    /// <para>Not platform-gated, because these four collections are typed to PostgreSQL-only classes: a
    /// non-PostgreSQL package cannot populate them at all, so there is no cross-engine false error to
    /// guard against the way there is for a table.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckPostgreSqlQuotedIdentifierOnModeledObjects(Template template) =>
        QuotedIdentifierFindings(ModeledObjectNamedParts(template), $"Template '{template.Name}'");

    private static IEnumerable<Finding> QuotedIdentifierFindings(
        IEnumerable<(string Kind, string Name)> parts,
        string location)
    {
        foreach (var (kind, name) in parts)
        {
            if (string.IsNullOrEmpty(name) || !name.Contains('"')) continue;
            // "{kind} '{name}'" rather than "{kind} name '{name}'": several kinds already end in the noun
            // (Table OldName, ReplicaIdentityIndex), and appending "name" to those read as "Table OldName
            // name 'old'".
            yield return new Finding(Severity.Error, QuotedIdentifierCode, Category, location,
                $"{kind} '{name}' contains a double-quote character, which PostgreSQL allows but " +
                "SchemaSmith cannot deploy: the name is re-wrapped in double quotes without escaping, so " +
                "every stored form of it produces invalid DDL. Rename the object to remove the quote.");
        }
    }

    /// <summary>
    /// Every name on a table that reaches PostgreSQL DDL as a wrapped identifier.
    /// <para>Each kind here was read at its emission site in the shipped quench scripts and carries the
    /// stored value between bare double quotes with no doubling. A name wrapped by <c>QUOTE_IDENT</c>
    /// escapes correctly and so is deliberately ABSENT — a policy name is the case that looks like an
    /// omission and is not one. Column <em>lists</em> are also out: they route through
    /// <c>QuoteColumnList</c>/<c>QuoteIndexColumnList</c>, and <c>SchemaRef</c> is read by no script.</para>
    /// </summary>
    private static IEnumerable<(string Kind, string Name)> NamedParts(PostgreSqlTable table)
    {
        yield return ("Table", table.Name);
        // Schema is resolved through IDeliverableTable, uniformly across platforms -- the same route
        // the duplicate check uses at :285, rather than a property Table itself does not carry.
        var schema = (table as IDeliverableTable)?.Schema;
        if (!string.IsNullOrEmpty(schema)) yield return ("Schema", schema);
        if (!string.IsNullOrEmpty(table.OldName)) yield return ("Table OldName", table.OldName);
        foreach (var c in table.Columns)
        {
            yield return ("Column", c.Name);
            if (!string.IsNullOrEmpty(c.OldName)) yield return ("Column OldName", c.OldName);
        }
        foreach (var i in table.Indexes) yield return ("Index", i.Name);
        foreach (var f in table.ForeignKeys)
        {
            yield return ("Foreign key", f.Name);
            // The REFERENCED table's schema and name are wrapped at ForeignKeyQuench:18 exactly as the
            // local ones are, and are NOT covered by checking the related table's own declaration: a
            // package may reference a table it does not declare, and SS-FK-002 resolves the reference
            // without ever looking at whether the stored text can be emitted.
            if (f is PostgreSqlForeignKey { RelatedTableSchema: { Length: > 0 } relatedSchema })
                yield return ("Foreign key RelatedTableSchema", relatedSchema);
            if (!string.IsNullOrEmpty(f.RelatedTable)) yield return ("Foreign key RelatedTable", f.RelatedTable);
        }
        foreach (var cc in table.CheckConstraints) yield return ("Check constraint", cc.Name);
        foreach (var ec in table.ExcludeConstraints) yield return ("Exclude constraint", ec.Name);
        foreach (var st in table.Statistics) yield return ("Statistics", st.Name);
        // A pointer, not a declaration -- so it is its own kind. When it names a declared index the index
        // reports separately, which is honest: both the CREATE and the REPLICA IDENTITY clause break.
        if (!string.IsNullOrEmpty(table.ReplicaIdentityIndex))
            yield return ("ReplicaIdentityIndex", table.ReplicaIdentityIndex);
    }

    /// <summary>Every name on a modeled non-table object that reaches PostgreSQL DDL as a wrapped identifier.</summary>
    private static IEnumerable<(string Kind, string Name)> ModeledObjectNamedParts(Template template)
    {
        foreach (var view in template.MaterializedViews)
        {
            yield return ("Materialized view", view.Name);
            if (!string.IsNullOrEmpty(view.Schema)) yield return ("Materialized view schema", view.Schema);
            // MissingMaterializedViewIndexesQuench:110 wraps an index name on a view exactly as the table
            // path does, so a view's indexes are part of this population rather than the table one.
            foreach (var index in view.Indexes) yield return ("Materialized view index", index.Name);
        }

        foreach (var enumType in template.EnumTypes)
        {
            yield return ("Enum type", enumType.Name);
            if (!string.IsNullOrEmpty(enumType.Schema)) yield return ("Enum type schema", enumType.Schema);
        }

        foreach (var sequence in template.Sequences)
        {
            yield return ("Sequence", sequence.Name);
            if (!string.IsNullOrEmpty(sequence.Schema)) yield return ("Sequence schema", sequence.Schema);
        }

        foreach (var sequence in template.MariaDbSequences)
            yield return ("Sequence", sequence.Name);

        foreach (var domain in template.DomainTypes)
        {
            yield return ("Domain type", domain.Name);
            if (!string.IsNullOrEmpty(domain.Schema)) yield return ("Domain type schema", domain.Schema);
            foreach (var check in domain.CheckConstraints)
                yield return ("Domain type check constraint", check.Name);
        }
    }

    /// <summary>
    /// PostgreSQL REPLICA IDENTITY coherence — issue #407.
    /// <para>The deploy raises on a declaration it cannot honour, but a mid-deploy failure is a worse
    /// place to learn about a typo than <c>--Validate</c>. These catch the same mistakes statically, and
    /// two of them (unknown index, non-unique index) the deploy could only report as PostgreSQL's own
    /// error against generated DDL.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckReplicaIdentity(Table table, string tableLocation)
    {
        if (table is not PostgreSqlTable pgTable) yield break;

        var mode = pgTable.ReplicaIdentity?.Trim();
        var indexName = pgTable.ReplicaIdentityIndex?.Trim();
        var wantsIndex = string.Equals(mode, "INDEX", StringComparison.OrdinalIgnoreCase);

        if (!wantsIndex)
        {
            if (!string.IsNullOrEmpty(indexName))
                yield return new Finding(Severity.Warning, ReplicaIdentityIndexIgnoredCode, Category, tableLocation,
                    $"Table '{table.Name}' names ReplicaIdentityIndex '{indexName}' but its ReplicaIdentity is " +
                    $"'{(string.IsNullOrEmpty(mode) ? "unset" : mode)}', so the index is ignored — set ReplicaIdentity " +
                    "to INDEX, or drop ReplicaIdentityIndex.");
            yield break;
        }

        if (string.IsNullOrEmpty(indexName))
        {
            yield return new Finding(Severity.Error, ReplicaIdentityIndexMissingCode, Category, tableLocation,
                $"Table '{table.Name}' sets ReplicaIdentity to INDEX but declares no ReplicaIdentityIndex. " +
                "PostgreSQL needs the name of the unique index that carries the identity.");
            yield break;
        }

        var named = table.Indexes.FirstOrDefault(i =>
            string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase));

        if (named == null)
        {
            // Only flag when the table declares indexes at all: an index-less table may legitimately
            // carry one created by a script rather than by the package.
            if (table.Indexes.Count > 0)
                yield return new Finding(Severity.Error, ReplicaIdentityIndexUnknownCode, Category, tableLocation,
                    $"Table '{table.Name}' names ReplicaIdentityIndex '{indexName}', which is not one of its " +
                    "declared Indexes. A replica identity pointing at an index that is never created fails the deploy.");
            yield break;
        }

        if (!named.Unique && !named.PrimaryKey && !named.UniqueConstraint)
            yield return new Finding(Severity.Error, ReplicaIdentityIndexNotUniqueCode, Category, tableLocation,
                $"Table '{table.Name}' names ReplicaIdentityIndex '{indexName}', which is not unique. " +
                "PostgreSQL requires a unique, non-partial index over NOT NULL columns.");
    }

    /// <summary>
    /// #417: <c>CdcFilegroup</c> only places a CDC change table, so on a table without <c>EnableCDC</c> it does
    /// nothing, and the deploy has no reason to mention it. Table-level only: a template default legitimately
    /// covers a mix of CDC and non-CDC tables.
    /// </summary>
    private static IEnumerable<Finding> CheckCdcFilegroup(Table table, string tableLocation)
    {
        if (table is not SqlServerTable ssTable || ssTable.EnableCDC == true || string.IsNullOrWhiteSpace(ssTable.CdcFilegroup)) yield break;

        yield return new Finding(Severity.Warning, CdcFilegroupInertCode, Category, tableLocation,
            $"Table '{table.Name}' sets CdcFilegroup '{ssTable.CdcFilegroup}' but not EnableCDC, so there is no change " +
            "table to place and the setting does nothing — set EnableCDC, or drop CdcFilegroup.");
    }

    /// <summary>
    /// #426: <c>CdcSupportsNetChanges</c> shapes a capture instance, so without <c>EnableCDC</c> there is none to shape.
    /// Either value is inert. Table-level only, for the same reason as <c>CdcFilegroup</c>.
    /// </summary>
    private static IEnumerable<Finding> CheckCdcSupportsNetChanges(Table table, string tableLocation)
    {
        if (table is not SqlServerTable ssTable || ssTable.EnableCDC == true || ssTable.CdcSupportsNetChanges is not { } netChanges) yield break;

        yield return new Finding(Severity.Warning, CdcFilegroupInertCode, Category, tableLocation,
            $"Table '{table.Name}' sets CdcSupportsNetChanges {(netChanges ? "true" : "false")} but not EnableCDC, so there " +
            "is no capture instance to shape and the setting does nothing — set EnableCDC, or drop CdcSupportsNetChanges.");
    }

    /// <summary>
    /// <c>CdcIndexName</c> picks the index a capture instance identifies rows by, so without <c>EnableCDC</c> it does
    /// nothing. With it, SQL Server accepts only a unique index over NOT NULL columns, and the deploy refuses anything
    /// else; checked against the declared indexes, as the deploy does, since the index may be created by it.
    /// </summary>
    private static IEnumerable<Finding> CheckCdcIndexName(Table table, string tableLocation)
    {
        if (table is not SqlServerTable ssTable || string.IsNullOrWhiteSpace(ssTable.CdcIndexName)) yield break;

        if (ssTable.EnableCDC != true)
        {
            yield return new Finding(Severity.Warning, CdcFilegroupInertCode, Category, tableLocation,
                $"Table '{table.Name}' sets CdcIndexName '{ssTable.CdcIndexName}' but not EnableCDC, so there is no " +
                "capture instance to identify rows for and the setting does nothing — set EnableCDC, or drop CdcIndexName.");
            yield break;
        }

        var problem = CdcIndexProblem(table, NormalizeIdentifier(ssTable.CdcIndexName));
        if (problem != null)
            yield return new Finding(Severity.Error, CdcIndexNameInvalidCode, Category, tableLocation,
                $"Table '{table.Name}' names CdcIndexName '{ssTable.CdcIndexName}', {problem}. CDC identifies rows by a " +
                "unique index over NOT NULL columns, and the deploy refuses anything else — name one of the table's " +
                "declared unique indexes, or drop CdcIndexName to use the primary key.");
    }

    /// <summary>
    /// SQL Server builds a full-text index only over its KEY INDEX: an unfiltered unique index on a single NOT NULL column. Anything
    /// else fails the deploy when it creates the full-text index; checked against the declared indexes, as CdcIndexName is.
    /// </summary>
    private static IEnumerable<Finding> CheckFullTextKeyIndex(Table table, string tableLocation)
    {
        if (table is not SqlServerTable ssTable) yield break;

        foreach (var fullText in ssTable.FullTextIndex.Where(f => !string.IsNullOrWhiteSpace(f.KeyIndex)))
        {
            var keyIndex = NormalizeIdentifier(fullText.KeyIndex);
            var problem = FullTextKeyIndexProblem(table, keyIndex);
            if (problem != null)
                yield return new Finding(Severity.Error, FullTextKeyIndexInvalidCode, Category, tableLocation,
                    $"Table '{table.Name}' names full-text KeyIndex '{fullText.KeyIndex}', {problem}. SQL Server builds a " +
                    "full-text index only over an unfiltered unique index on a single NOT NULL column (error 7653), so the deploy fails creating it " +
                    "— name one of the table's declared indexes that is.");
        }
    }

    private static string FullTextKeyIndexProblem(Table table, string indexName)
    {
        var index = table.Indexes.FirstOrDefault(i =>
            string.Equals(NormalizeIdentifier(i.Name), indexName, StringComparison.OrdinalIgnoreCase));
        if (index == null) return "which is not one of its declared indexes";
        if (!index.Unique && !index.PrimaryKey && !index.UniqueConstraint) return "which is not unique";

        var keyColumns = SplitNames(index.IndexColumns).Select(c => NormalizeIdentifier(StripOrderingSuffix(c))).ToList();
        if (keyColumns.Count > 1) return "which has more than one key column";
        if (index is SqlServerIndex { FilterExpression: { } filter } && !string.IsNullOrWhiteSpace(filter)) return "which has a filter";
        return table.Columns.Any(c => c.Nullable && keyColumns.Contains(NormalizeIdentifier(c.Name), StringComparer.OrdinalIgnoreCase))
            ? "which has a nullable key column"
            : null;
    }

    private static string CdcIndexProblem(Table table, string indexName)
    {
        var index = table.Indexes.FirstOrDefault(i =>
            string.Equals(NormalizeIdentifier(i.Name), indexName, StringComparison.OrdinalIgnoreCase));
        if (index == null) return "which is not one of its declared indexes";
        if (!index.Unique && !index.PrimaryKey && !index.UniqueConstraint) return "which is not unique";
        if (index.PrimaryKey) return null;

        var nullableColumns = new HashSet<string>(
            table.Columns.Where(c => c.Nullable).Select(c => NormalizeIdentifier(c.Name)), StringComparer.OrdinalIgnoreCase);
        return SplitNames(index.IndexColumns).Select(StripOrderingSuffix)
            .Any(column => nullableColumns.Contains(NormalizeIdentifier(column)))
            ? "which has a nullable key column"
            : null;
    }

    /// <summary>
    /// MariaDB per-column <c>WITHOUT SYSTEM VERSIONING</c> coherence — issue #408.
    /// <para>Verified on 11.4: MariaDB <b>accepts the clause on a table that is not system-versioned and
    /// silently discards it</b> — no error, and <c>EXTRA</c> comes back empty. So the declaration is inert,
    /// and nothing at deploy time can tell the author, because nothing failed.</para>
    /// <para>Warning rather than Error: it is legal and deployable, and a table may gain versioning later.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckSystemVersioningExclusions(Table table, string tableLocation)
    {
        if (table is not MariaDbTable mariaTable || mariaTable.IsSystemVersioned) yield break;

        foreach (var column in table.Columns.OfType<MariaDbColumn>().Where(c => c.WithoutSystemVersioning))
            yield return new Finding(Severity.Warning, VersioningExclusionInertCode, Category, tableLocation,
                $"Column '{column.Name}' on table '{table.Name}' sets WithoutSystemVersioning, but the table " +
                "does not set IsSystemVersioned. MariaDB accepts the clause here and silently discards it, so " +
                "the exclusion does nothing — set IsSystemVersioned, or drop WithoutSystemVersioning.");
    }

    /// <summary>
    /// SQL Server partition placement declared as half a pair, or contradicting a filegroup
    /// (#partitioning, K1).
    /// <para>Both are refused by the quench too, but that refusal only arrives on a live target — and the
    /// half-pair case would otherwise reach the engine as <c>ON &lt;scheme&gt;</c> with no column, whose
    /// syntax error names neither the table nor the property. Catching them at authoring time is what
    /// <c>--Validate</c> is for.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckPartitionPlacement(Table table, string tableLocation)
    {
        if (table is not SqlServerTable sqlTable) yield break;

        foreach (var f in PartitionFindings(table.Name, "Table", tableLocation,
                     sqlTable.PartitionScheme, sqlTable.PartitionColumn, sqlTable.FileGroup))
            yield return f;

        // An index carries its own placement, independently of its table's, so it is checked in its own
        // right rather than inherited.
        foreach (var index in table.Indexes.OfType<SqlServerIndex>())
            foreach (var f in PartitionFindings($"{table.Name}.{index.Name}", "Index", tableLocation,
                         index.PartitionScheme, index.PartitionColumn, index.FileGroup))
                yield return f;
    }

    private static IEnumerable<Finding> PartitionFindings(
        string name,
        string kind,
        string location,
        string scheme,
        string column,
        string fileGroup)
    {
        var hasScheme = !string.IsNullOrWhiteSpace(scheme);
        var hasColumn = !string.IsNullOrWhiteSpace(column);

        if (hasScheme != hasColumn)
            yield return new Finding(Severity.Error, PartitionHalfDeclaredCode, Category, location,
                $"{kind} '{name}' declares {(hasScheme ? "PartitionScheme without PartitionColumn" : "PartitionColumn without PartitionScheme")}. " +
                "SQL Server needs both — the ON clause names the scheme and the column its partition " +
                "function is applied to. Declare both, or neither.");

        if (hasScheme && !string.IsNullOrWhiteSpace(fileGroup))
            yield return new Finding(Severity.Error, PartitionAndFileGroupCode, Category, location,
                $"{kind} '{name}' declares both FileGroup '{fileGroup}' and PartitionScheme '{scheme}'. " +
                "It lives on one data space — declare one or the other, not both.");
    }

    /// <summary>
    /// MySQL/MariaDB compression table options that cannot be combined.
    /// <para><b>Both engines REFUSE the combination, and neither error names what is wrong.</b> Verified
    /// live: MySQL 8.0 rejects <c>COMPRESSION</c> alongside <c>ROW_FORMAT=COMPRESSED</c> with 1031
    /// ("Table storage engine ... doesn't have this option"); MariaDB 11.4 rejects <c>PAGE_COMPRESSED</c>
    /// with the same row format as errno 140 ("Wrong create options"). Both name the table and neither
    /// names the option, so without this the author gets an error that could mean almost anything.</para>
    /// <para>Error rather than Warning: the deploy cannot succeed, so there is nothing to weigh.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckCompressionOptions(Table table, string tableLocation)
    {
        if (table is not MySqlTable mySqlTable) yield break;

        var rowFormatCompressed = string.Equals(mySqlTable.RowFormat?.Trim(), "COMPRESSED",
            StringComparison.OrdinalIgnoreCase);
        var mariaTable = table as MariaDbTable;

        if (rowFormatCompressed && !string.IsNullOrWhiteSpace(mySqlTable.Compression))
            yield return new Finding(Severity.Error, CompressionConflictCode, Category, tableLocation,
                $"Table '{table.Name}' sets Compression to '{mySqlTable.Compression}' and RowFormat to " +
                "COMPRESSED. MySQL refuses that combination (error 1031) — transparent page compression " +
                "needs an uncompressed row format. Drop one of the two.");

        if (rowFormatCompressed && mariaTable is { PageCompressed: true })
            yield return new Finding(Severity.Error, CompressionConflictCode, Category, tableLocation,
                $"Table '{table.Name}' sets PageCompressed and RowFormat to COMPRESSED. MariaDB refuses " +
                "that combination (errno 140, \"Wrong create options\"). Drop one of the two.");

        if (mariaTable is { PageCompressed: false, PageCompressionLevel: not null })
            yield return new Finding(Severity.Warning, CompressionLevelInertCode, Category, tableLocation,
                $"Table '{table.Name}' sets PageCompressionLevel but not PageCompressed, so the level is " +
                "ignored — set PageCompressed, or drop the level.");
    }

    /// <summary>
    /// A memory-optimized (Hekaton) table cannot also declare disk placement (J1). A memory-optimized table
    /// lives in the MEMORY_OPTIMIZED_DATA filegroup, not a regular/FILESTREAM filegroup, and cannot be
    /// partitioned — so FileGroup / TextImageFileGroup / FileStreamFileGroup / PartitionScheme alongside
    /// MemoryOptimized is a contradiction the engine rejects with its own error. Caught here by name so the
    /// author sees which two settings conflict rather than a raw CREATE failure.
    /// </summary>
    private static IEnumerable<Finding> CheckMemoryOptimizedPlacement(Table table, string tableLocation)
    {
        if (table is not SqlServerTable sqlTable || !sqlTable.MemoryOptimized) yield break;

        foreach (var (prop, value) in new[]
                 {
                     ("FileGroup", sqlTable.FileGroup),
                     ("TextImageFileGroup", sqlTable.TextImageFileGroup),
                     ("FileStreamFileGroup", sqlTable.FileStreamFileGroup),
                     ("PartitionScheme", sqlTable.PartitionScheme),
                 })
            if (!string.IsNullOrWhiteSpace(value))
                yield return new Finding(Severity.Error, MemoryOptimizedPlacementCode, Category, tableLocation,
                    $"Table '{table.Name}' is MemoryOptimized but also declares {prop} '{value}'. A " +
                    "memory-optimized table lives in the MEMORY_OPTIMIZED_DATA filegroup and cannot be placed " +
                    "on a regular/FILESTREAM filegroup or partitioned — drop the placement, or drop MemoryOptimized.");
    }

    /// <summary>
    /// MySQL/MariaDB partitioning (#partitioning K3) internal-consistency checks — the engine equivalent of
    /// the SQL Server SS-PART rules, which this feature shipped without. Only the unambiguous inconsistencies
    /// are flagged (MySQL legitimately allows named HASH partitions, so that is NOT one):
    /// SS-PART-003 RANGE/LIST with no named Partitions (they need per-partition boundaries), and SS-PART-004
    /// a HASH/KEY partition carrying a VALUES boundary (HASH/KEY assign by hashing and have none).
    /// </summary>
    private static IEnumerable<Finding> CheckMyPartitioning(Table table, string tableLocation)
    {
        if (table is not MySqlTable mySqlTable) yield break;
        var p = mySqlTable.Partitioning;
        if (p == null || string.IsNullOrWhiteSpace(p.Method)) yield break;

        var method = p.Method.Trim().ToUpperInvariant();
        var isHashKey = method is "HASH" or "KEY" or "LINEAR HASH" or "LINEAR KEY";
        var isRangeList = method.StartsWith("RANGE", StringComparison.Ordinal) ||
                          method.StartsWith("LIST", StringComparison.Ordinal);
        var hasNamedPartitions = p.Partitions is { Count: > 0 };

        if (isRangeList && !hasNamedPartitions)
            yield return new Finding(Severity.Error, MyPartitionRangeListNoPartitionsCode, Category, tableLocation,
                $"Table '{table.Name}' partitions by {method} but declares no Partitions. RANGE/LIST need each " +
                "partition named with its boundary (VALUES LESS THAN / VALUES IN) — add Partitions, or use " +
                "HASH/KEY with a PartitionCount.");

        if (isHashKey && hasNamedPartitions)
            foreach (var part in p.Partitions.Where(pt => !string.IsNullOrWhiteSpace(pt.Values)))
                yield return new Finding(Severity.Error, MyPartitionHashBoundaryCode, Category, tableLocation,
                    $"Table '{table.Name}' partitions by {method} but partition '{part.Name}' declares a " +
                    "Values boundary. HASH/KEY assign rows by hashing and have no boundary — drop the Values, " +
                    "or use RANGE/LIST if you meant to define boundaries.");
    }

    /// <summary>
    /// A scheduled event declared BOTH declaratively and as a script (F4).
    /// <para>Promoting events to a managed type was done additively: the <c>Events/</c> folder now holds
    /// <c>.json</c> (declared, converged, droppable by absence) alongside <c>.sql</c> (scripted, re-run
    /// every deploy), so no existing package had to change. The cost of that choice is that the same
    /// event can be described twice, and the two forms fight — the scripted one drops and recreates on
    /// every deploy, undoing the convergence the declared one just performed, and which wins depends on
    /// slot ordering rather than on anything the author wrote.</para>
    /// <para>Error rather than Warning: there is no reading under which declaring an event twice is what
    /// someone meant.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckScheduledEvents(Template template)
    {
        if (template.Events.Count == 0) yield break;

        var scriptedEventNames = ScriptedNamesIn(template, "Events");

        foreach (var ev in template.Events.Where(e => scriptedEventNames.Contains(e.Name ?? "")))
            yield return new Finding(Severity.Error, DuplicateEventCode, Category,
                $"Template '{template.Name}'",
                $"Event '{ev.Name}' is declared as JSON and also scripted as a .sql file in the same " +
                "Events folder. The scripted form drops and recreates the event on every deploy, undoing " +
                "what the declared form converged — keep one.");
    }


    /// <summary>
    /// A PostgreSQL enum type, sequence or domain type declared BOTH as JSON and scripted as a .sql file
    /// in the same folder.
    /// <para>These three folders are additive by design -- <c>Enum Types/</c>, <c>Sequences/</c> and
    /// <c>Domain Types/</c> each hold declared <c>.json</c> and scripted <c>.sql</c> side by side, and a
    /// package using only one of the two is correct and common. Declaring the SAME object both ways is the
    /// problem, and until now it validated clean: the coexistence rule existed only for scheduled events
    /// (<c>SS-EVT-001</c>), even though all three of these are already shape-validated.</para>
    /// <para>Why it matters differs per type, so the messages say what the engine actually does rather than
    /// warning in the abstract:</para>
    /// <list type="bullet">
    /// <item>An enum type's scripted form is a GUARDED <c>CREATE TYPE</c> (<c>EnumTypeQuench.sql</c>), so
    /// once the type exists the guard skips and the script silently does nothing -- the declared form is
    /// what converges, and the script is dead weight that reads as if it were in charge.</item>
    /// <item>A domain type is the same trap and <c>DomainTypeQuench.sql</c> says so outright: there is no
    /// <c>CREATE OR REPLACE DOMAIN</c>, so a scripted domain is a guarded <c>CREATE DOMAIN</c>.</item>
    /// <item>For sequences the engine scripts say nothing about a scripted form (checked, not assumed), so
    /// that message claims no mechanism -- only that two authoring paths for one object is ambiguous.</item>
    /// </list>
    /// <para>Deliberately NOT claimed anywhere here: which authoring path wins when both are present. That
    /// needs SchemaQuench slot-ordering evidence which this check does not have, and guessing it in a
    /// finding message would be worse than leaving it out.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckModeledFolderObjectCoexistence(Template template)
    {
        foreach (var finding in CoexistenceFindings(
                     template, "Enum Types", DuplicateEnumTypeCode, "Enum type",
                     template.EnumTypes.Select(e => (e.Schema, e.Name)),
                     "The scripted form is a guarded CREATE TYPE, so once the type exists the script " +
                     "silently does nothing while the declared form is what converges"))
            yield return finding;

        foreach (var finding in CoexistenceFindings(
                     template, "Domain Types", DuplicateDomainTypeCode, "Domain type",
                     template.DomainTypes.Select(d => (d.Schema, d.Name)),
                     "There is no CREATE OR REPLACE DOMAIN, so the scripted form is a guarded CREATE " +
                     "DOMAIN and silently does nothing once the domain exists"))
            yield return finding;

        foreach (var finding in CoexistenceFindings(
                     template, "Sequences", DuplicateSequenceCode, "Sequence",
                     template.Sequences.Select(s => (s.Schema, s.Name)).Concat(template.MariaDbSequences.Select(s => ((string)null, s.Name))),
                     "Two authoring paths for one object leave it ambiguous which one is in charge"))
            yield return finding;
    }

    private static IEnumerable<Finding> CoexistenceFindings(
        Template template,
        string folder,
        string code,
        string noun,
        IEnumerable<(string Schema, string Name)> declaredNames,
        string consequence)
    {
        var declared = declaredNames.Where(n => !string.IsNullOrWhiteSpace(n.Name)).ToList();
        if (declared.Count == 0) yield break;

        var scripted = ScriptedNamesIn(template, folder);
        if (scripted.Count == 0) yield break;

        // A script file names its object bare ("status.sql") or schema-qualified ("public.status.sql").
        foreach (var (schema, name) in declared.Where(d => scripted.Contains(d.Name) || scripted.Contains($"{d.Schema}.{d.Name}")))
            yield return new Finding(Severity.Error, code, Category,
                $"Template '{template.Name}'",
                $"{noun} '{(string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}")}' is declared as JSON and also scripted as a .sql file in the same " +
                $"{folder} folder. {consequence} — keep one.");
    }

    // A scripted object is named by its file, and the folder that holds it is what says which kind it is. The folder
    // is the script folder's own path under the template, first segment: matching "/<folder>/" anywhere in a script's
    // full path counted a directory above the package, or a same-named subfolder of another folder, as that folder.
    private static HashSet<string> ScriptedNamesIn(Template template, string folder) =>
        template.ObjectFolders
            .Where(f => (f.FolderPath ?? "").Split('/', '\\')[0].Equals(folder, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Scripts)
            .Select(s => Path.GetFileNameWithoutExtension(s.FilePath ?? ""))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Mirrors SchemaSmith_NormalizeIndexColumns.sql's DESC/ASC suffix handling (source of truth —
    // keep in sync): a trailing " DESC" or " ASC" (case-insensitive) is ordering, not part of the
    // column name.
    private static string StripOrderingSuffix(string column)
    {
        if (column.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase))
            return column[..^5].TrimEnd();
        if (column.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase))
            return column[..^4].TrimEnd();
        return column;
    }

    // A MySQL/MariaDB prefix key part -- `Email`(20) -- names the column before its length, as extraction writes it.
    private static string StripPrefixLength(string column)
    {
        if (!column.EndsWith(')')) return column;
        var open = column.LastIndexOf('(');
        var length = open > 0 ? column[(open + 1)..^1].Trim() : "";
        if (length.Length == 0 || !length.All(char.IsAsciiDigit)) return column;
        return column[..open].TrimEnd();
    }

    // A functional/expression key part starts with '(' rather than a backtick — extraction always
    // backtick-wraps a plain column name, so this is an unambiguous discriminator (mirrors
    // SchemaSmith_NormalizeIndexColumns.sql). Validating the identifiers inside the expression would
    // need a real SQL parser and is out of scope — skip rather than false-flag.
    private static bool IsExpressionKeyPart(string column) => column.StartsWith('(');
}
