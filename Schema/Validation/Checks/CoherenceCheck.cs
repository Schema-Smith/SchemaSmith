// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Schema.Delivery;
using Schema.Domain;
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
    private const string IndexColumnCode = "SS-IDX-001";
    private const string BackfillWithoutDefaultCode = "SS-COL-001";
    private const string RebuildThresholdCode = "SS-TBL-001";
    private const string RlsWithoutPoliciesCode = "SS-RLS-001";
    private const string PoliciesWithoutRlsCode = "SS-RLS-002";
    private const string ReplicaIdentityIndexMissingCode = "SS-RI-001";
    private const string ReplicaIdentityIndexUnknownCode = "SS-RI-002";
    private const string ReplicaIdentityIndexNotUniqueCode = "SS-RI-003";
    private const string ReplicaIdentityIndexIgnoredCode = "SS-RI-004";
    private const string VersioningExclusionInertCode = "SS-SV-001";
    private const string CompressionConflictCode = "SS-CO-001";
    private const string CompressionLevelInertCode = "SS-CO-002";
    private const string DuplicateEventCode = "SS-EVT-001";
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
        foreach (var template in ctx.Templates)
            findings.AddRange(CheckScheduledEvents(template));

        foreach (var template in ctx.Templates)
        foreach (var table in template.Tables)
        {
            var location = $"Template '{template.Name}' / Table '{table.Name}'";
            foreach (var fk in table.ForeignKeys)
                findings.AddRange(CheckForeignKey(table, fk, location, tablesByKey));

            foreach (var index in table.Indexes)
                findings.AddRange(CheckIndex(table, index, location));

            findings.AddRange(CheckBackfill(table, location));
            findings.AddRange(CheckRebuildPolicy(table, location));
            findings.AddRange(CheckRowLevelSecurity(table, location));
            findings.AddRange(CheckReplicaIdentity(table, location));
            findings.AddRange(CheckSystemVersioningExclusions(table, location));
            findings.AddRange(CheckCompressionOptions(table, location));
            findings.AddRange(CheckPartitionPlacement(table, location));
            findings.AddRange(CheckMyPartitioning(table, location));
            findings.AddRange(CheckMemoryOptimizedPlacement(table, location));
        }

        return findings;
    }

    private static IEnumerable<Finding> CheckForeignKey(
        Table table,
        ForeignKey fk,
        string tableLocation,
        IReadOnlyDictionary<(string Schema, string Name), List<Table>> tablesByKey)
    {
        var location = $"{tableLocation} / FK '{fk.Name}'";
        var localColumnNames = ColumnNames(table);
        var fkColumns = SplitNames(fk.Columns);
        var fkRelatedColumns = SplitNames(fk.RelatedColumns);

        foreach (var column in fkColumns.Where(column => !localColumnNames.Contains(NormalizeIdentifier(column))))
            yield return new Finding(Severity.Error, LocalColumnCode, Category, location,
                $"Local column '{column}' referenced in Columns does not exist on table '{table.Name}'.");

        // Cardinality is a pure string-count comparison — independent of whether the related
        // table resolves, so it always runs.
        if (fkColumns.Count != fkRelatedColumns.Count)
            yield return new Finding(Severity.Error, CardinalityCode, Category, location,
                $"Columns has {fkColumns.Count} entries but RelatedColumns has {fkRelatedColumns.Count} — FK column lists must be the same length.");

        var (schema, name) = ResolveRelatedTarget(table, fk);

        if (!tablesByKey.TryGetValue((schema, name), out var relatedTables))
        {
            yield return new Finding(Severity.Error, RelatedTableCode, Category, location,
                $"RelatedTable '{fk.RelatedTable}' does not resolve to any known table (resolved schema '{schema}').");
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
    /// Error, not Warning — the distinction from SS-COL-001 is the point. A BackfillExistingRows with no
    /// Default is INERT: the deploy runs, the setting simply does nothing, and a warning is proportionate.
    /// A THRESHOLD mode with no threshold is UNEVALUABLE: there is no number to compare pending changes
    /// against, so a deploy would have to invent a behaviour — alter in place, or rebuild — and either
    /// choice is a guess about what the author meant.
    /// <para>Only the table's OWN declared policy is examined. The deploy-time cascade (environment,
    /// product, template) is not visible from a package-authoring check, and a table that declares
    /// nothing here is not the level that would be at fault.</para>
    /// </summary>
    private static IEnumerable<Finding> CheckRebuildPolicy(Table table, string tableLocation)
    {
        var policy = table.RebuildPolicy;
        if (policy == null) yield break;
        if (!string.Equals(policy.Mode, "THRESHOLD", StringComparison.OrdinalIgnoreCase)) yield break;
        if (policy.Threshold is >= 1) yield break;

        yield return new Finding(Severity.Error, RebuildThresholdCode, Category, tableLocation,
            $"Table '{table.Name}' sets RebuildPolicy.Mode 'THRESHOLD' but no Threshold of 1 or more. " +
            "THRESHOLD needs a threshold to compare against, so the policy cannot be evaluated — set a " +
            "Threshold, or choose Mode 'ALWAYS' or 'NEVER'.");
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


    private static IEnumerable<Finding> CheckIndex(Table table, Index index, string tableLocation)
    {
        var location = $"{tableLocation} / Index '{index.Name}'";
        var localColumnNames = ColumnNames(table);

        foreach (var column in SplitNames(index.IndexColumns).Select(StripOrderingSuffix)
                     .Where(column => !IsExpressionKeyPart(column))
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

        var rawRelatedTable = fk.RelatedTable ?? "";
        var dotIndex = rawRelatedTable.IndexOf('.');
        var hasDotPrefix = dotIndex > 0;
        var prefixSchema = hasDotPrefix ? rawRelatedTable[..dotIndex] : null;
        var name = IdentityKey(hasDotPrefix ? rawRelatedTable[(dotIndex + 1)..] : rawRelatedTable);

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
    /// MySQL/MariaDB compression table options that cannot be combined.
    /// <para><b>Both engines REFUSE the combination, and neither error names what is wrong.</b> Verified
    /// live: MySQL 8.0 rejects <c>COMPRESSION</c> alongside <c>ROW_FORMAT=COMPRESSED</c> with 1031
    /// ("Table storage engine ... doesn't have this option"); MariaDB 11.4 rejects <c>PAGE_COMPRESSED</c>
    /// with the same row format as errno 140 ("Wrong create options"). Both name the table and neither
    /// names the option, so without this the author gets an error that could mean almost anything.</para>
    /// <para>Error rather than Warning: the deploy cannot succeed, so there is nothing to weigh.</para>
    /// </summary>
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

        var scriptedEventNames = template.ObjectScripts?
            .Where(s => (s.FilePath ?? "").Replace(Path.DirectorySeparatorChar, '/').Contains("/Events/", StringComparison.OrdinalIgnoreCase))
            .Select(s => Path.GetFileNameWithoutExtension(s.FilePath ?? ""))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var ev in template.Events.Where(e => scriptedEventNames.Contains(e.Name ?? "")))
            yield return new Finding(Severity.Error, DuplicateEventCode, Category,
                $"Template '{template.Name}'",
                $"Event '{ev.Name}' is declared as JSON and also scripted as a .sql file in the same " +
                "Events folder. The scripted form drops and recreates the event on every deploy, undoing " +
                "what the declared form converged — keep one.");
    }

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

    // A functional/expression key part starts with '(' rather than a backtick — extraction always
    // backtick-wraps a plain column name, so this is an unambiguous discriminator (mirrors
    // SchemaSmith_NormalizeIndexColumns.sql). Validating the identifiers inside the expression would
    // need a real SQL parser and is out of scope — skip rather than false-flag.
    private static bool IsExpressionKeyPart(string column) => column.StartsWith('(');
}
