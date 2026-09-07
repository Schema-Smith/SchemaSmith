// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

public class ForgeKindlerTests
{
    [Test]
    public void GetKindlingScriptNames_SqlServer_ReturnsExpectedScripts()
    {
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.SqlServer);
        Assert.That(scripts, Is.Not.Empty);
        Assert.That(scripts, Does.Contain("Kindling_SchemaSmith_Schema.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.BootstrapTableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_StripParenWrapping.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_StripLeadingSelect.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_StripBracketWrapping.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_SafeBracketWrap.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_ServerMajorVersion.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.PrintWithNoWait.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.RebuildTable.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MissingTableAndColumnQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ModifiedTableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ForeignKeyQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.TableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.IndexOnlyQuench.sql"));
        Assert.That(scripts, Does.Contain("Kindling_CompletedMigrationScripts_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ChangeAudit_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ProductOwnership_Table.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.fn_FormatJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.GenerateTableJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ValidateIndexedViewOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.FixupIndexedViewOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.IndexedViewQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.GenerateIndexedViewJson.sql"));
    }

    [Test]
    public void GetKindlingScriptNames_SqlServer_BootstrapAndKindlingTableComeBeforeTableQuench()
    {
        // BootstrapTableQuench creates the proc; the kindling table call uses it.
        // Both must come before TableQuench (which doesn't depend on either) so the
        // pipeline shape is: schema -> bootstrap proc -> kindling tables -> utility procs.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.SqlServer);
        var bootstrapIdx = Array.IndexOf(scripts, "SchemaSmith.BootstrapTableQuench.sql");
        var kindlingTableIdx = Array.IndexOf(scripts, "Kindling_CompletedMigrationScripts_Table.sql");
        var tableQuenchIdx = Array.IndexOf(scripts, "SchemaSmith.TableQuench.sql");

        Assert.That(bootstrapIdx, Is.GreaterThanOrEqualTo(0), "BootstrapTableQuench must be in the script list.");
        Assert.That(bootstrapIdx, Is.LessThan(kindlingTableIdx),
            "BootstrapTableQuench must be created before the kindling table that uses it.");
        Assert.That(kindlingTableIdx, Is.LessThan(tableQuenchIdx),
            "Kindling table must be created via Bootstrap before TableQuench is created.");
    }

    [Test]
    public void GetKindlingScriptNames_SqlServer_RebuildTableFollowsItsGuardAndPrecedesTheQuench()
    {
        // RebuildTable calls SchemaSmith.fn_RebuildBlockedReason to refuse a table whose live state a
        // shadow copy would destroy -- a scalar function, which unlike an EXEC'd procedure is not covered
        // by deferred name resolution, so the guard has to exist before this CREATE runs. And the quench
        // procedures are what will elect a rebuild, so the engine has to be there before they are created.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.SqlServer);
        var guardIdx = Array.IndexOf(scripts, "SchemaSmith.fn_RebuildBlockedReason.sql");
        var rebuildIdx = Array.IndexOf(scripts, "SchemaSmith.RebuildTable.sql");
        var modifiedQuenchIdx = Array.IndexOf(scripts, "SchemaSmith.ModifiedTableQuench.sql");

        Assert.That(rebuildIdx, Is.GreaterThanOrEqualTo(0), "SchemaSmith.RebuildTable.sql must be kindled.");
        Assert.That(guardIdx, Is.LessThan(rebuildIdx),
            "fn_RebuildBlockedReason must be created before RebuildTable, which calls it.");
        Assert.That(rebuildIdx, Is.LessThan(modifiedQuenchIdx),
            "RebuildTable must be created before the quench procedures that will elect a rebuild.");
    }

    [Test]
    public void GetKindlingScriptNames_PostgreSQL_ReturnsExpectedScripts()
    {
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.PostgreSQL);
        Assert.That(scripts, Is.Not.Empty);
        Assert.That(scripts, Does.Contain("Kindling_SchemaSmith_Schema.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.BootstrapTableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ExecuteOrDebug.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.QuoteColumnList.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.QuoteIndexColumnList.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.StripParenWrapping.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.StripTypeCast.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ServerVersionNum.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.StripLeadingSelect.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.RebuildBlockedReason.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.RebuildTable.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ValidateTableOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.FixupTableOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.FixupIndexOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MissingTableAndColumnQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.BuildExistingIndexesSnapshot.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ModifiedTableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ForeignKeyQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.TableQuench.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ProductOwnership_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_CompletedMigrationScripts_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ChangeAudit_Table.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.IndexOnlyQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.FormatJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.GenerateTableJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.ValidateMaterializedViewOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.FixupMaterializedViewOwnership.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MissingMaterializedViewIndexesQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith.MaterializedViewQuench.sql"));
    }

    [Test]
    public void GetKindlingScriptNames_PostgreSQL_RebuildTableFollowsItsGuardAndPrecedesTheQuench()
    {
        // PostgreSQL twin of the SQL Server ordering guard. RebuildTable calls
        // "SchemaSmith"."RebuildBlockedReason" to refuse a table whose live state a shadow copy would
        // destroy, and "SchemaSmith"."ExecuteOrDebug" to run or preview every statement it builds, so both
        // have to be kindled ahead of it. And the quench procedures are what will elect a rebuild, so the
        // engine has to be there before they are created.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.PostgreSQL);
        var guardIdx = Array.IndexOf(scripts, "SchemaSmith.RebuildBlockedReason.sql");
        var executeOrDebugIdx = Array.IndexOf(scripts, "SchemaSmith.ExecuteOrDebug.sql");
        var rebuildIdx = Array.IndexOf(scripts, "SchemaSmith.RebuildTable.sql");
        var modifiedQuenchIdx = Array.IndexOf(scripts, "SchemaSmith.ModifiedTableQuench.sql");

        Assert.That(rebuildIdx, Is.GreaterThanOrEqualTo(0), "SchemaSmith.RebuildTable.sql must be kindled.");
        Assert.That(guardIdx, Is.GreaterThanOrEqualTo(0).And.LessThan(rebuildIdx),
            "RebuildBlockedReason must be created before RebuildTable, which calls it.");
        Assert.That(executeOrDebugIdx, Is.GreaterThanOrEqualTo(0).And.LessThan(rebuildIdx),
            "ExecuteOrDebug must be created before RebuildTable, which routes every statement through it.");
        Assert.That(rebuildIdx, Is.LessThan(modifiedQuenchIdx),
            "RebuildTable must be created before the quench procedures that will elect a rebuild.");
    }

    [Test]
    public void GetKindlingScriptNames_PostgreSQL_BootstrapAndKindlingTablesComeBeforeOwnershipProcs()
    {
        // ProductOwnership table must be created before ValidateTableOwnership (which reads it).
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.PostgreSQL);
        var bootstrapIdx = Array.IndexOf(scripts, "SchemaSmith.BootstrapTableQuench.sql");
        var productOwnershipIdx = Array.IndexOf(scripts, "Kindling_ProductOwnership_Table.sql");
        var validateOwnershipIdx = Array.IndexOf(scripts, "SchemaSmith.ValidateTableOwnership.sql");

        Assert.That(bootstrapIdx, Is.LessThan(productOwnershipIdx),
            "BootstrapTableQuench must be created before the ProductOwnership kindling call.");
        Assert.That(productOwnershipIdx, Is.LessThan(validateOwnershipIdx),
            "ProductOwnership table must exist before ValidateTableOwnership proc is created.");
    }

    [Test]
    public void GetKindlingScriptNames_MySQL_ReturnsExpectedScripts()
    {
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.MySQL);
        Assert.That(scripts, Is.Not.Empty);
        Assert.That(scripts, Does.Contain("SchemaSmith_BootstrapTableQuench.sql"));
        Assert.That(scripts, Does.Contain("Kindling_CompletedMigrationScripts_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ProductOwnership_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_StatusMessages_Table.sql"));
        Assert.That(scripts, Does.Contain("Kindling_ChangeAudit_Table.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_QuoteIdentifier.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_StripBacktickWrapping.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_SafeBacktickWrap.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_StripLeadingSelect.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_ServerVersionNum.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_NormalizeIndexColumns.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_NormalizeCheckExpression.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_UpperDataType.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_GenerateTableJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_ParseTableJson.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_MissingTableAndColumnQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_ModifiedTableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_MissingIndexesAndConstraintsQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_ForeignKeyQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_IndexOnlyQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_TableQuench.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_SnapshotIndexVisibility.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_SnapshotIndexExistence.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_RebuildBlockedReason.sql"));
        Assert.That(scripts, Does.Contain("SchemaSmith_RebuildTable.sql"));
    }

    [Test]
    public void GetKindlingScriptNames_MySQL_RebuildTableFollowsItsGuardAndPrecedesTheQuench()
    {
        // MySQL twin of the SQL Server and PostgreSQL ordering guards. SchemaSmith_RebuildTable calls
        // SchemaSmith_RebuildBlockedReason to refuse a table whose live state a shadow copy would destroy
        // -- a stored FUNCTION, and unlike a CALLed procedure a function reference binds when the calling
        // procedure is created, so the guard has to exist before this CREATE runs. It also calls the
        // backtick helpers, kindled earlier still. And the quench procedures are what will elect a
        // rebuild, so the engine has to be there before they are created.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.MySQL);
        var guardIdx = Array.IndexOf(scripts, "SchemaSmith_RebuildBlockedReason.sql");
        var wrapIdx = Array.IndexOf(scripts, "SchemaSmith_SafeBacktickWrap.sql");
        var rebuildIdx = Array.IndexOf(scripts, "SchemaSmith_RebuildTable.sql");
        var modifiedQuenchIdx = Array.IndexOf(scripts, "SchemaSmith_ModifiedTableQuench.sql");

        Assert.That(rebuildIdx, Is.GreaterThanOrEqualTo(0), "SchemaSmith_RebuildTable.sql must be kindled.");
        Assert.That(guardIdx, Is.GreaterThanOrEqualTo(0).And.LessThan(rebuildIdx),
            "SchemaSmith_RebuildBlockedReason must be created before RebuildTable, which calls it.");
        Assert.That(wrapIdx, Is.GreaterThanOrEqualTo(0).And.LessThan(rebuildIdx),
            "SchemaSmith_SafeBacktickWrap must be created before RebuildTable, which quotes every identifier with it.");
        Assert.That(rebuildIdx, Is.LessThan(modifiedQuenchIdx),
            "RebuildTable must be created before the quench procedures that will elect a rebuild.");
    }

    [Test]
    public void GetKindlingScriptNames_MariaDb_RebuildTableIsKindledAndOrdered()
    {
        // MariaDb inherits the MySQL list by base-platform routing, and it is the engine where the guard
        // actually has something to say (system versioning, application-time periods) -- so assert the
        // pair is present and ordered here too rather than trusting the list-equality test alone.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.MariaDb);
        var guardIdx = Array.IndexOf(scripts, "SchemaSmith_RebuildBlockedReason.sql");
        var rebuildIdx = Array.IndexOf(scripts, "SchemaSmith_RebuildTable.sql");

        Assert.That(rebuildIdx, Is.GreaterThanOrEqualTo(0), "SchemaSmith_RebuildTable.sql must be kindled on MariaDb.");
        Assert.That(guardIdx, Is.GreaterThanOrEqualTo(0).And.LessThan(rebuildIdx),
            "The MariaDb RebuildBlockedReason variant must be created before RebuildTable, which calls it.");
    }

    [Test]
    public void GetKindlingScriptNames_MySQL_DoesNotContainDeletedAlterScript()
    {
        // BootstrapTableQuench's ADD COLUMN IF NOT EXISTS / CREATE INDEX IF NOT EXISTS pattern
        // subsumed Kindling_AlterCompletedMigrationScripts.sql. The file must be gone from the
        // ordered kindling script list.
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.MySQL);
        Assert.That(scripts, Does.Not.Contain("Kindling_AlterCompletedMigrationScripts.sql"),
            "Kindling_AlterCompletedMigrationScripts.sql was deleted by the BootstrapTableQuench refactor.");
    }

    [Test]
    public void GetKindlingScriptNames_MySQL_BootstrapPrecedesAllKindlingTables()
    {
        var scripts = ForgeKindler.GetKindlingScriptNames(Platform.MySQL);
        var bootstrapIdx = Array.IndexOf(scripts, "SchemaSmith_BootstrapTableQuench.sql");
        var completedIdx = Array.IndexOf(scripts, "Kindling_CompletedMigrationScripts_Table.sql");
        var ownershipIdx = Array.IndexOf(scripts, "Kindling_ProductOwnership_Table.sql");
        var statusIdx = Array.IndexOf(scripts, "Kindling_StatusMessages_Table.sql");
        var changeAuditIdx = Array.IndexOf(scripts, "Kindling_ChangeAudit_Table.sql");

        Assert.That(bootstrapIdx, Is.LessThan(completedIdx),
            "BootstrapTableQuench must precede CompletedMigrationScripts kindling.");
        Assert.That(bootstrapIdx, Is.LessThan(ownershipIdx),
            "BootstrapTableQuench must precede ProductOwnership kindling.");
        Assert.That(bootstrapIdx, Is.LessThan(statusIdx),
            "BootstrapTableQuench must precede StatusMessages kindling.");
        Assert.That(bootstrapIdx, Is.LessThan(changeAuditIdx),
            "BootstrapTableQuench must precede ChangeAudit kindling.");
    }

    [Test]
    public void GetKindlingScriptNames_AllPlatforms_HaveUniqueScripts()
    {
        var sqlServer = ForgeKindler.GetKindlingScriptNames(Platform.SqlServer);
        var postgres = ForgeKindler.GetKindlingScriptNames(Platform.PostgreSQL);
        var mysql = ForgeKindler.GetKindlingScriptNames(Platform.MySQL);

        // SqlServer: 28 = 27 prior + Kindling_ChangeAudit_Table (object-change audit, #243 E5)
        // + SchemaSmith.UnsupportedFeaturePolicy (version-adaptive codegen policy helper, SS-2008 floor spine)
        // + SchemaSmith.fn_SplitList (all-versions STRING_SPLIT replacement for the compat-100 XML path)
        // + SchemaSmith.DegradeUnsupportedColumnStore + SchemaSmith.DegradeUnsupportedFeatures (the emit-guard
        //   degrade spine: the former drops below-2012/2014 columnstore from #Indexes, the latter is the single
        //   choke point that neutralizes below-2016 temporal/masking/Always-Encrypted in the working set)
        // + SchemaSmith.fn_ColumnTypeArguments (single source of a column's parenthesized DataType argument,
        //   shared by GenerateTableJson/GenerateTableXml/ModifiedTableQuench — replaces the hand-copied CASE
        //   that dropped TIME(n)/DATETIMEOFFSET(n) precision).
        // + SchemaSmith.fn_NormalizeTemporalRetentionPeriod (canonicalizes a declared HISTORY_RETENTION_PERIOD
        //   to the plural unit the DDL takes, since the catalog reports it singular -- normalized once at
        //   parse so extraction and the drift compare cannot disagree).
        // + SchemaSmith.fn_NormalizeCheckExpression (folds a check expression to the form SQL Server itself
        //   stores -- spaces around operators removed, parens around a bare literal -- so a constraint
        //   written in its natural form stops comparing unequal to the engine's rendering of itself).
        // + SchemaSmith.fn_RebuildBlockedReason (names the live state that makes a table unsafe to rebuild --
        //   temporal, CDC, replication, Change Tracking -- so a rebuild can refuse and say which one. Its
        //   CREATE is assembled at kindle time because temporal_type_desc is 2016+ and a function body binds
        //   at CREATE).
        // + SchemaSmith.RebuildTable (the shadow-copy-and-swap engine: refuse-if-blocked, capture the identity
        //   counter, drop inbound foreign keys, create the shadow in declared order, copy, reseed, swap, drop.
        //   Kindled after fn_RebuildBlockedReason, which it calls to refuse, and before the quench procedures).
        // + Kindling_ProductOwnership_Table (#J1 -- ownership fallback for SQL Server memory-optimized tables,
        //   which reject the ProductName extended property; the same table-based ownership PostgreSQL/MySQL use).
        Assert.That(sqlServer.Length, Is.EqualTo(35));
        // PostgreSQL: 35 = 28 prior + Kindling_ChangeAudit_Table (#243 E5) + Kindling_ProductOwnership_IndexMigration
        // (one-owner enforcement, #270 TRANSITIONAL) + SchemaSmith.UnsupportedFeaturePolicy (version-adaptive
        // codegen policy helper) + SchemaSmith.IndexNullsNotDistinct (PG15-adaptive extraction read)
        // + SchemaSmith.ColumnCompression (PG14-adaptive attcompression read) + SchemaSmith.StatisticsExpressionColumns
        // (PG14-adaptive pg_stats_ext_exprs read) — the last two are the floor 14->12 cascade
        // + SchemaSmith.ColumnTypeArguments (the PostgreSQL twin of the SQL Server helper above — replaces the
        //   hand-copied CASE that dropped timestamptz(n)/time(n)/timetz(n) precision).
        // + SchemaSmith.RebuildBlockedReason (the PostgreSQL twin of the SQL Server helper above -- publication
        //   membership, inheritance edges and declarative partitioning are the states a shadow-copy-and-swap
        //   would silently sever).
        // + SchemaSmith.RebuildTable (the PostgreSQL shadow-copy-and-swap engine: refuse-if-blocked, capture
        //   the sequence position AND its name, drop inbound foreign keys, create the shadow in declared
        //   order, copy, restore the sequence, swap, drop, put the sequence name back. Kindled after
        //   RebuildBlockedReason, which it calls to refuse, and before the quench procedures).
        // + SchemaSmith.ReplicaIdentityQuench (#407 -- applies a declared REPLICA IDENTITY. Its own procedure
        //   rather than a clause in ModifiedTableQuench's attribute fixup because the USING INDEX form names an
        //   index, and ModifiedTableQuench runs BEFORE MissingIndexesAndConstraintsQuench -- so on a table's
        //   first deploy that index does not exist yet. Kindled after MissingIndexesAndConstraintsQuench, the
        //   same dependency SQL Server's ChangeTrackingQuench has on a primary key).
        // +2 = SchemaSmith.EnumTypeQuench + SchemaSmith.GenerateEnumTypeJson (F5 -- enum types promoted
        //   from scripted to MANAGED. The scripted form was a guarded CREATE TYPE, so once the type
        //   existed a value-list edit did nothing at all, silently and forever).
        // +2 = SchemaSmith.SequenceQuench + SchemaSmith.GenerateSequenceJson (F5).
        // +2 = SchemaSmith.DomainTypeQuench + SchemaSmith.GenerateDomainTypeJson (F5 -- domain types
        //   promoted for the same reason as enums: a scripted domain is a guarded CREATE DOMAIN, and
        //   once the domain exists that guard skips, so an edited CHECK never lands. Unlike an enum,
        //   ALTER DOMAIN converges constraints/default/NOT NULL without touching a dependent column.
        Assert.That(postgres.Length, Is.EqualTo(44));
        // MySQL: 36 = 27 prior (22 base + five MariaDB-compat helpers, all #351: SchemaSmith_IndexIsVisible
        // (IS_VISIBLE/IGNORED), SchemaSmith_StripIntDisplayWidth, SchemaSmith_NormalizeColumnDefault,
        // SchemaSmith_DropCheckClause, SchemaSmith_IndexInvisibleClause) + eight MySQL-5.7/MariaDB-10.2 floor
        // helpers: SchemaSmith_JsonScalarInt + SchemaSmith_JsonScalarStr (null-safe JSON payload reads for the
        // version-agnostic JSON_EXTRACT parse), SchemaSmith_UnsupportedFeaturePolicy (MySQL policy spine),
        // SchemaSmith_SupportsCheckConstraints (CHECK-availability predicate, MySQL 8.0.16 / MariaDB),
        // SchemaSmith_SupportsRenameColumn + SchemaSmith_SupportsRenameIndex (RENAME COLUMN needs MySQL 8.0 /
        // MariaDB 10.5.2; RENAME INDEX needs MariaDB 10.5.2 — below, emit CHANGE COLUMN / drop-recreate),
        // SchemaSmith_BuildIndexRenameClause (the version-adaptive index-rename clause builder), and
        // SchemaSmith_SupportsDescendingIndex (DESC key parts need MySQL 8.0 / MariaDB 10.8 — below, degrade to
        // ascending idempotently) + SchemaSmith_SupportsInvisibleIndex (INVISIBLE needs MySQL 8.0 / MariaDB 10.6
        // — below, degrade to visible; the emit-guard invisible-index slice).
        // +1 = SchemaSmith_SnapshotIndexVisibility (per-engine index-visibility snapshot procedure; MySQL reads
        // IS_VISIBLE, the MariaDb override reads IGNORED — replaces the per-row SchemaSmith_IndexIsVisible call in
        // the MissingIndexes/IndexOnly modified-index detection with a one-scan snapshot + join).
        // +1 = SchemaSmith_SnapshotIndexExistence (engine-agnostic index-existence snapshot procedure; refreshes
        // _SchemaSmith_IdxExist at each IndexOnlyQuench create/ownership point, replacing the per-declared-index
        // live existence reads with a one-scan snapshot).
        // +1 = SchemaSmith_SupportsFunctionalIndex (functional/expression-index availability predicate, MySQL
        // 8.0.13; MariaDB has no equivalent form — always 0. Gates the EXPRESSION-column read in
        // GenerateTableJson and both _SchemaSmith_IdxDetectSnap builds).
        // +1 = SchemaSmith_SupportsDefaultExpression (column DEFAULT-expression availability predicate,
        // MySQL 8.0.13; MariaDB has supported it since 10.2.1 — at/below the floor, always 1. Gates the
        // column-skip degrade in MissingTableAndColumnQuench / ModifiedTableQuench).
        // +1 = SchemaSmith_SupportsInvisibleColumn (invisible-column availability predicate, MySQL 8.0.23 /
        // MariaDB 10.3 — the INVISIBLE keyword itself is the same on both engines, only the introduction
        // version differs. Gates the INVISIBLE clause ParseTableJson bakes into ColumnScript and the
        // modified-column visibility compare in ModifiedTableQuench).
        // +1 = SchemaSmith_SupportsColumnSrid (column-SRID availability predicate, MySQL 8.0.3 only —
        // MariaDB has no equivalent attribute at any version, always 0. Gates the SRID clause
        // ParseTableJson bakes into ColumnScript and the modified-column SRID compare in ModifiedTableQuench).
        // +1 = SchemaSmith_ColumnSrid (per-column live SRID reader; MySQL reads INFORMATION_SCHEMA.COLUMNS.
        // SRS_ID, the MariaDb override always returns NULL since that column does not exist there at all —
        // isolates the divergence out of GenerateTableJson / ModifiedTableQuench, same shape as
        // SchemaSmith_IndexIsVisible).
        // +1 = SchemaSmith_ColumnOnUpdateClause (extracts + normalizes a column's `ON UPDATE
        // CURRENT_TIMESTAMP[(n)]` auto-refresh clause from EXTRA; no MariaDb override needed — it reuses
        // SchemaSmith_NormalizeColumnDefault for the engine-divergent case/paren folding. Unlike
        // Invisible/Srid above, the clause predates both engines' floors, so no SchemaSmith_Supports...
        // gate exists for it).
        // +1 = SchemaSmith_IndexHasFunctionalKeyPart (declared-side detector for a functional/expression
        // key part in an index's column list; gates the create/modify emit sites in
        // MissingIndexesAndConstraintsQuench / IndexOnlyQuench below SchemaSmith_SupportsFunctionalIndex()'s
        // floor, closing the emit-side gap that function's own read-only gating left open).
        // +1 = SchemaSmith_NumericDefaultsEqual (compares a decimal column's default BY VALUE: the engine
        // stores it at the column's scale, so a declared 0 comes back 0.00 and re-ALTERed the column on
        // every deploy; scoped to decimal/numeric so string defaults keep comparing as text).
        // +1 = SchemaSmith_RebuildBlockedReason (names the live state that makes a table unsafe to rebuild.
        // The MySQL body is deliberately always-NULL -- MySQL has none of these concepts -- and the MariaDb
        // override detects system versioning and application-time periods, the two states a
        // shadow-copy-and-swap would silently destroy there).
        // +1 = SchemaSmith_RebuildTable (the MySQL/MariaDB shadow-copy-and-swap engine: refuse-if-blocked,
        // capture the AUTO_INCREMENT counter, create the shadow in declared order, copy, reseed, swap with
        // a single atomic RENAME TABLE, then drop the inbound foreign keys and the old table. Kindled after
        // SchemaSmith_RebuildBlockedReason, which it calls to refuse, and before the quench procedures.
        // Unlike its two siblings it gets no transaction -- MySQL DDL is not transactional -- so the
        // reversible work runs first and the destructive step follows the atomic swap).
        // +1 = SchemaSmith_IsSystemTimePeriodColumn (answers whether a column is a system-versioned
        // table's engine-owned row-start/row-end column, so extraction can exclude it. Isolated in a
        // function with an always-0 MySQL definition and a real MariaDb override, because the catalog
        // columns behind it do not exist on MySQL and column resolution inside a routine is deferred to
        // execution -- a static reference would CREATE cleanly on MySQL and fail at every CALL).
        // +1 = SchemaSmith_SetSystemVersioningAlterHistory (applies the operator opt-in for altering a
        // system-versioned table. A procedure with a MySQL no-op and a MariaDb override, because MySQL
        // refuses to CREATE a routine that merely mentions @@system_versioning_alter_history -- ERROR
        // 1193 at create time, even inside an unreachable branch).
        // +1 = SchemaSmith_TablePeriodsJson (reads a MariaDB table's application-time periods. MySQL
        // stub returns '[]'; the MariaDb override wraps the INFORMATION_SCHEMA.PERIODS read in a
        // /*M!110400 */ version comment, because that catalog is 11.4+ and an unknown TABLE is rejected
        // when the routine is CREATED -- so the reference must be invisible to the parser, not merely
        // unreached).
        // +1 = SchemaSmith_SupportsSystemVersioning (#408 -- gates the per-column
        //   WITHOUT SYSTEM VERSIONING clause. MariaDB 10.3.4+; an unconditional 0 on MySQL, which has no
        //   system versioning at any version, the same shape as SupportsApplicationTimePeriods).
        // +1 = SchemaSmith_CreateOption (the CREATE_OPTIONS parser -- COMPRESSION, KEY_BLOCK_SIZE and
        //   MariaDB PAGE_COMPRESSED/_LEVEL all surface only in that one free-text column, so they share
        //   one reader. LOCATE-based, not regex: REGEXP_SUBSTR does not exist on the MySQL 5.7 floor.
        // +2 = SchemaSmith_EventMatches + SchemaSmith_EventQuench (#F4 -- scheduled events promoted from
        //   a scripted-object folder to a MANAGED type. EventMatches must kindle FIRST; the quench calls
        //   it, and converging an event is DROP + CREATE, so a false "changed" resets its schedule.
        // +1 = SchemaSmith_GenerateEventJson (the catalog-to-package translation for events, kindled
        //   like GenerateTableJson rather than living inline in SchemaTongs -- which is what lets it be
        //   certified against a live server instead of only through the whole cast pipeline).
        // +1 = SchemaSmith_NormalizePartitionExpression (#partitioning K3 -- the declared-vs-live compare
        //   for a partition expression. Its own helper because the engines disagree about what the catalog
        //   returns: MySQL 5.7 echoes the user's text, every other supported engine rewrites it, so a
        //   literal compare would refuse a package extracted on the floor and deployed above it).
        // +1 = SchemaSmith_TablePartitioningJson (#partitioning K3 -- the catalog-to-package read. One
        //   shared definition rather than the MySQL-stub/MariaDb-override shape TablePeriodsJson needs,
        //   because INFORMATION_SCHEMA.PARTITIONS exists on every supported version of both engines).
        // +1 = SchemaSmith_TableTablespace (F2b -- the MySQL general-tablespace placement read. MySQL-only
        //   (MariaDb has no general tablespaces); a PROCEDURE with dynamic SQL because INNODB_TABLES is
        //   8.0+-only and MySQL disallows PREPARE in a FUNCTION. This line and the count were BOTH missed
        //   when F2b landed -- the script was registered but this assertion stayed at 59 -- so F2c corrects
        //   the arithmetic to include both.).
        // +1 = SchemaSmith_TableDataDirectory (F2c -- the DATA DIRECTORY placement read, both engines.
        //   MySQL's body needs the same PROCEDURE/dynamic-SQL shape as SchemaSmith_TableTablespace above
        //   (INNODB_DATAFILES is 8.0+-only); the MariaDb per-file override reads CREATE_OPTIONS instead --
        //   one net script name added to this shared list either way, since the override resolves through
        //   ResourceLoader rather than adding a second list entry).
        Assert.That(mysql.Length, Is.EqualTo(61));
    }

    [Test]
    public void GetKindlingScripts_SqlServerXmlEncoding_SwapsProcsAndDropsFormatJson()
    {
        var json = ForgeKindler.GetKindlingScripts(Platform.SqlServer, IngestEncoding.Json)
            .Select(s => s.FileName).ToArray();
        var xml = ForgeKindler.GetKindlingScripts(Platform.SqlServer, IngestEncoding.Xml)
            .Select(s => s.FileName).ToArray();

        Assert.Multiple(() =>
        {
            // The five OPENJSON/FOR JSON procs are swapped for their XML twins...
            foreach (var (jsonFile, xmlFile) in new[]
                     {
                         ("SchemaSmith.BootstrapTableQuench.sql", "SchemaSmith.BootstrapTableXmlQuench.sql"),
                         ("SchemaSmith.IndexOnlyQuench.sql", "SchemaSmith.IndexOnlyXmlQuench.sql"),
                         ("SchemaSmith.IndexedViewQuench.sql", "SchemaSmith.IndexedViewXmlQuench.sql"),
                         ("SchemaSmith.GenerateTableJson.sql", "SchemaSmith.GenerateTableXml.sql"),
                         ("SchemaSmith.GenerateIndexedViewJson.sql", "SchemaSmith.GenerateIndexedViewXml.sql"),
                     })
            {
                Assert.That(xml, Does.Contain(xmlFile).And.Not.Contain(jsonFile), $"{jsonFile} -> {xmlFile}");
            }

            // ...fn_FormatJson (JSON-only, itself OPENJSON-based) is dropped...
            Assert.That(json, Does.Contain("SchemaSmith.fn_FormatJson.sql"));
            Assert.That(xml, Does.Not.Contain("SchemaSmith.fn_FormatJson.sql"));

            // ...so the XML list is one shorter, and every name is unique.
            Assert.That(xml, Has.Length.EqualTo(json.Length - 1));
            Assert.That(xml, Is.Unique);

            // The two stamps differ, so switching a database's encoding always re-kindles.
            Assert.That(ForgeKindler.ComputeKindleStamp(Platform.SqlServer, IngestEncoding.Xml),
                Is.Not.EqualTo(ForgeKindler.ComputeKindleStamp(Platform.SqlServer, IngestEncoding.Json)));
        });
    }

    [Test]
    public void ResolveKindleScript_SqlServerXml_InlinesXmlParseAndXmlTableDef()
    {
        // TableQuench carries {{ParseJson}}: under Xml it inlines the .nodes()-based parse (no OPENJSON), so
        // the proc CREATEs below the compat-130 cliff.
        var tableQuench = ForgeKindler.ResolveKindleScript("SchemaSmith.TableQuench.sql", Platform.SqlServer,
            replaceParseJson: true, replaceTableDef: false, IngestEncoding.Xml);
        // No executable OPENJSON(...) — the XML parse binds via .nodes()/.value(), so TableQuench CREATEs
        // below compat 130 (prose mentions of "OPENJSON" in comments are fine — match the call form).
        Assert.That(tableQuench, Does.Contain("Parse Tables from Xml").And.Not.Contain("OPENJSON("));

        // A _Table kindling script carries {{TableDef}}: under Xml it becomes a <Table> element the XML
        // bootstrap can shred, not raw JSON.
        var changeAudit = ForgeKindler.ResolveKindleScript("Kindling_ChangeAudit_Table.sql", Platform.SqlServer,
            replaceParseJson: false, replaceTableDef: true, IngestEncoding.Xml);
        Assert.That(changeAudit, Does.Contain("<Table>").And.Not.Contain("{{TableDef}}"));
    }

    [Test]
    public void ResolveKindleScript_SqlServerHelpers_BakeVersionAndPolicy_DroppingSessionContext()
    {
        // SS-2008 floor: fn_ServerMajorVersion / UnsupportedFeaturePolicy bake the C#-detected version + the
        // resolved policy at kindle time, so they CREATE on a genuine pre-2016 binary where SESSION_CONTEXT
        // (the former transport) does not exist.
        var fn = ForgeKindler.ResolveKindleScript("SchemaSmith.fn_ServerMajorVersion.sql", Platform.SqlServer,
            replaceParseJson: false, replaceTableDef: false, IngestEncoding.Json, serverMajorVersion: 15);
        Assert.Multiple(() =>
        {
            Assert.That(fn, Does.Contain("NULLIF(15, 0)"), "the detected version must be baked as a literal");
            Assert.That(fn, Does.Not.Contain("{{ServerMajorVersion}}"), "the version token must be substituted");
            Assert.That(fn, Does.Not.Contain("SESSION_CONTEXT("), "the 2016+ transport read must be gone");
        });

        var policyFn = ForgeKindler.ResolveKindleScript("SchemaSmith.UnsupportedFeaturePolicy.sql", Platform.SqlServer,
            replaceParseJson: false, replaceTableDef: false, IngestEncoding.Json, policy: "fail");
        Assert.Multiple(() =>
        {
            Assert.That(policyFn, Does.Contain("'fail'"), "the resolved policy must be baked");
            Assert.That(policyFn, Does.Not.Contain("{{UnsupportedPolicy}}"), "the policy token must be substituted");
            Assert.That(policyFn, Does.Not.Contain("SESSION_CONTEXT("), "the 2016+ transport read must be gone");
        });
    }

    [Test]
    public void ComputeKindleStamp_SqlServer_VariesByBakedVersionAndPolicy()
    {
        // Baking the version + policy into the helper bodies makes the stamp server-version + policy scoped:
        // a different detected version or policy re-kindles (correct — the resolved helper text differs).
        var v15Warn = ForgeKindler.ComputeKindleStamp(Platform.SqlServer, IngestEncoding.Json, serverMajorVersion: 15, policy: "warn");
        var v10Warn = ForgeKindler.ComputeKindleStamp(Platform.SqlServer, IngestEncoding.Json, serverMajorVersion: 10, policy: "warn");
        var v15Fail = ForgeKindler.ComputeKindleStamp(Platform.SqlServer, IngestEncoding.Json, serverMajorVersion: 15, policy: "fail");
        Assert.Multiple(() =>
        {
            Assert.That(v15Warn, Is.Not.EqualTo(v10Warn), "a different detected version must change the stamp");
            Assert.That(v15Warn, Is.Not.EqualTo(v15Fail), "a different policy must change the stamp");
        });
    }

    [Test]
    public void GetKindlingScriptNames_MariaDb_MatchesMySql()
    {
        // MariaDb is a MySQL variant: it inherits the MySQL kindling list via base-platform
        // routing; per-file MariaDb overrides (if any) still resolve through ResourceLoader.
        Assert.That(ForgeKindler.GetKindlingScriptNames(Platform.MariaDb),
                    Is.EqualTo(ForgeKindler.GetKindlingScriptNames(Platform.MySQL)));
    }

    [Test]
    public void GetKindlingScripts_KindleStamp_FollowsBootstrapAndCarriesTableDef()
    {
        foreach (var platform in new[] { Platform.SqlServer, Platform.PostgreSQL, Platform.MySQL })
        {
            var scripts = ForgeKindler.GetKindlingScripts(platform);
            var names = scripts.Select(s => s.FileName).ToArray();
            var bootstrapIdx = Array.FindIndex(names, n => n.Contains("BootstrapTableQuench"));
            var stampIdx = Array.IndexOf(names, "Kindling_KindleStamp_Table.sql");

            Assert.That(stampIdx, Is.GreaterThanOrEqualTo(0), $"KindleStamp must be kindled on {platform}.");
            Assert.That(bootstrapIdx, Is.LessThan(stampIdx),
                $"BootstrapTableQuench must precede the KindleStamp table call on {platform}.");
            Assert.That(scripts[stampIdx].ReplaceTableDef, Is.True,
                $"KindleStamp table call must substitute its sibling JSON on {platform}.");
        }
    }

    [Test]
    public void KindleTheForge_ThrowsForUnsupportedPlatform()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        // Cast an invalid int to Platform to test the default case
        var invalidPlatform = (Platform)99;
        Assert.Throws<ArgumentException>(() => ForgeKindler.KindleTheForge(mockCmd, invalidPlatform));
    }

    [Test]
    public void KindleOneFile_WrapsExceptionWithFileName()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        // This will fail because the embedded resource won't be found for a fake file name
        var ex = Assert.Throws<Exception>(() =>
            ForgeKindler.KindleOneFile(mockCmd, "NonExistentScript.sql", Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("Error occurred while kindling 'NonExistentScript.sql'"));
    }

    [Test]
    public void KindleOneFile_ThrowsWhenScriptNotFound()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var ex = Assert.Throws<Exception>(() =>
            ForgeKindler.KindleOneFile(mockCmd, "NotAReal_Script_File.sql", Platform.PostgreSQL));
        Assert.That(ex.Message, Does.Contain("kindling"));
    }

    [Test]
    public void GetSiblingTableDefJson_SqlServer_LoadsCompletedMigrationScriptsJson()
    {
        // The sibling JSON for Kindling_CompletedMigrationScripts_Table.sql lives next to
        // it under Schema/Scripts/SqlServer/Kindling_CompletedMigrationScripts.json.
        var json = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_CompletedMigrationScripts_Table.sql", Platform.SqlServer);
        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("\"Schema\""));
        Assert.That(json, Does.Contain("[CompletedMigrationScripts]"));
        Assert.That(json, Does.Contain("[IX_CompletedMigrationScripts_Slot_Scope]"),
            "Secondary index from Commit B must live in the shared JSON.");
    }

    [Test]
    public void GetSiblingTableDefJson_PostgreSQL_LoadsCompletedMigrationScriptsJson()
    {
        var json = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_CompletedMigrationScripts_Table.sql", Platform.PostgreSQL);
        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("CompletedMigrationScripts"));
        Assert.That(json, Does.Contain("ix_completedmigrationscripts_slot_scope"),
            "Secondary index from Commit B must live in the shared JSON.");
    }

    [Test]
    public void GetSiblingTableDefJson_PostgreSQL_LoadsProductOwnershipJson()
    {
        var json = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_ProductOwnership_Table.sql", Platform.PostgreSQL);
        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("ProductOwnership"));
        Assert.That(json, Does.Contain("template_name"),
            "Slice-3 template_name column must live in the shared JSON.");
    }

    [Test]
    public void GetSiblingTableDefJson_MySQL_LoadsAllKindlingJsons()
    {
        var completed = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_CompletedMigrationScripts_Table.sql", Platform.MySQL);
        Assert.That(completed, Does.Contain("SchemaSmith_CompletedMigrationScripts"));
        Assert.That(completed, Does.Contain("ix_completedmigrationscripts_slot_scope"),
            "Commit B's secondary index must live in the shared JSON (not in a deleted Alter script).");

        var ownership = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_ProductOwnership_Table.sql", Platform.MySQL);
        Assert.That(ownership, Does.Contain("SchemaSmith_ProductOwnership"));

        var status = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_StatusMessages_Table.sql", Platform.MySQL);
        Assert.That(status, Does.Contain("SchemaSmith_StatusMessages"));

        var changeAudit = ForgeKindler.GetSiblingTableDefJson(
            "Kindling_ChangeAudit_Table.sql", Platform.MySQL);
        Assert.That(changeAudit, Does.Contain("SchemaSmith_ChangeAudit"));
    }

    [Test]
    public void GetSiblingTableDefJson_ThrowsWhenJsonMissing()
    {
        // Any kindling script whose name doesn't match a real sibling JSON resource should
        // throw (the message names both files so the maintainer can fix it fast).
        var ex = Assert.Throws<Exception>(() =>
            ForgeKindler.GetSiblingTableDefJson("Kindling_NoSuchThing_Table.sql", Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("Kindling_NoSuchThing.json"));
    }

    [Test]
    public void GetKindlingScripts_SqlServer_FlagsTableQuenchForParseJsonSubstitution()
    {
        var scripts = ForgeKindler.GetKindlingScripts(Platform.SqlServer);
        var tableQuench = scripts.Single(s => s.FileName == "SchemaSmith.TableQuench.sql");
        Assert.That(tableQuench.ReplaceParseJson, Is.True);
        Assert.That(tableQuench.ReplaceTableDef, Is.False);

        var kindlingTable = scripts.Single(s => s.FileName == "Kindling_CompletedMigrationScripts_Table.sql");
        Assert.That(kindlingTable.ReplaceTableDef, Is.True);
        Assert.That(kindlingTable.ReplaceParseJson, Is.False);
    }

    [Test]
    public void GetKindlingScripts_TableQuenchParseJsonFlag_IsSetForPgButNotMySql()
    {
        var pg = ForgeKindler.GetKindlingScripts(Platform.PostgreSQL)
            .Single(s => s.FileName == "SchemaSmith.TableQuench.sql");
        Assert.That(pg.ReplaceParseJson, Is.True, "PostgreSQL TableQuench embeds the ParseJson body.");
        Assert.That(pg.ReplaceTableDef, Is.False);

        var mysql = ForgeKindler.GetKindlingScripts(Platform.MySQL)
            .Single(s => s.FileName == "SchemaSmith_TableQuench.sql");
        Assert.That(mysql.ReplaceParseJson, Is.False, "MySQL TableQuench has no ParseJson token.");
        Assert.That(mysql.ReplaceTableDef, Is.False);
    }

    [Test]
    public void GetKindlingScriptNames_IsDerivedFromDescriptors()
    {
        foreach (var platform in new[] { Platform.SqlServer, Platform.PostgreSQL, Platform.MySQL })
        {
            var names = ForgeKindler.GetKindlingScriptNames(platform);
            var descriptorNames = ForgeKindler.GetKindlingScripts(platform).Select(s => s.FileName).ToArray();
            Assert.That(names, Is.EqualTo(descriptorNames), $"Names must derive from descriptors for {platform}.");
        }
    }

    [Test]
    public void KindleOneFile_WithTableDefToken_PullsContentFromSiblingJsonResource()
    {
        // Smoke test: when replaceTableDefToken is true, the loader must substitute the
        // sibling JSON content. We exercise this by attempting to kindle the real
        // Kindling_CompletedMigrationScripts_Table.sql against a mock command; the call
        // builds the SQL string but EXEC fails (no real DB) — we catch the wrapping
        // exception and assert the JSON content was substituted in.
        var mockCmd = Substitute.For<IDbCommand>();
        string capturedSql = null;
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => capturedSql = mockCmd.CommandText);

        ForgeKindler.KindleOneFile(mockCmd, "Kindling_CompletedMigrationScripts_Table.sql",
            Platform.SqlServer, replaceTableDefToken: true);

        Assert.That(capturedSql, Is.Not.Null);
        Assert.That(capturedSql, Does.Not.Contain("{{TableDef}}"),
            "Token must be substituted out of the executed SQL.");
        Assert.That(capturedSql, Does.Contain("[CompletedMigrationScripts]"),
            "Substituted SQL must contain the table name from the sibling JSON.");
        Assert.That(capturedSql, Does.Contain("[IX_CompletedMigrationScripts_Slot_Scope]"),
            "Substituted SQL must include the Commit-B secondary index name from the JSON.");
    }

    [Test]
    public void ResolveKindleScript_TableQuench_EmbedsParseJsonSource()
    {
        var resolved = ForgeKindler.ResolveKindleScript(
            "SchemaSmith.TableQuench.sql", Platform.SqlServer, replaceParseJson: true, replaceTableDef: false);

        Assert.That(resolved, Does.Not.Contain("{{ParseJson}}"), "Token must be substituted out.");
        // A distinctive line that only exists in ParseTableJsonIntoTempTables.sql:
        Assert.That(resolved, Does.Contain("Parse Tables from Json"),
            "Resolved TableQuench must contain the ParseJson source body, so a change there changes the hash.");
    }

    [Test]
    public void ComputeKindleStamp_IsDeterministicAndPlatformSpecific()
    {
        var sql1 = ForgeKindler.ComputeKindleStamp(Platform.SqlServer);
        var sql2 = ForgeKindler.ComputeKindleStamp(Platform.SqlServer);
        var pg = ForgeKindler.ComputeKindleStamp(Platform.PostgreSQL);

        Assert.That(sql1, Is.EqualTo(sql2), "Same platform must produce the same stamp on repeated calls.");
        Assert.That(sql1, Has.Length.EqualTo(64), "SHA-256 hex is 64 chars.");
        Assert.That(sql1, Does.Match("^[0-9a-f]{64}$"), "Stamp must be lowercase hex only (safe to inline in SQL).");
        Assert.That(sql1, Is.Not.EqualTo(pg), "Different platforms kindle different content -> different stamp.");
    }

    [Test]
    public void ComputeKindleStamp_EqualsHashOfConcatenatedResolvedScripts()
    {
        var sb = new StringBuilder();
        foreach (var s in ForgeKindler.GetKindlingScripts(Platform.PostgreSQL))
            sb.Append(ForgeKindler.ResolveKindleScript(s.FileName, Platform.PostgreSQL, s.ReplaceParseJson, s.ReplaceTableDef));
        using var sha = SHA256.Create();
        var expected = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();

        Assert.That(ForgeKindler.ComputeKindleStamp(Platform.PostgreSQL), Is.EqualTo(expected),
            "Stamp must be the hash of the resolved kindle scripts concatenated in kindle order.");
    }

    [Test]
    public void ReadStamp_SqlServer_ReturnsScalarValue()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        mockCmd.ExecuteScalar().Returns("abc123");
        var result = ForgeKindler.ReadStamp(mockCmd, Platform.SqlServer);
        Assert.That(result, Is.EqualTo("abc123"));
        Assert.That(mockCmd.CommandText, Does.Contain("KindleStamp"));
    }

    [Test]
    public void ReadStamp_ReturnsNull_WhenScalarIsDbNullOrNull()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        mockCmd.ExecuteScalar().Returns(DBNull.Value);
        Assert.That(ForgeKindler.ReadStamp(mockCmd, Platform.PostgreSQL), Is.Null);

        mockCmd.ExecuteScalar().Returns((object)null);
        Assert.That(ForgeKindler.ReadStamp(mockCmd, Platform.PostgreSQL), Is.Null);
    }

    [Test]
    public void WriteStamp_MySQL_IssuesDeleteThenInsertWithStamp()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));

        ForgeKindler.WriteStamp(mockCmd, Platform.MySQL, "deadbeef");

        Assert.That(executed, Has.Count.EqualTo(2));
        Assert.That(executed[0], Does.StartWith("DELETE FROM SchemaSmith_KindleStamp"));
        Assert.That(executed[1], Does.Contain("INSERT INTO SchemaSmith_KindleStamp").And.Contains("'deadbeef'"));
    }

    [Test]
    public void WriteStamp_SqlServer_UsesBracketedIdentifiersAndUtcDate()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));

        ForgeKindler.WriteStamp(mockCmd, Platform.SqlServer, "abc");

        Assert.That(executed, Has.Count.EqualTo(2));
        Assert.That(executed[0], Is.EqualTo("DELETE FROM [SchemaSmith].[KindleStamp]"));
        Assert.That(executed[1], Does.Contain("INSERT INTO [SchemaSmith].[KindleStamp]")
            .And.Contain("'abc'").And.Contain("GETUTCDATE()"));
    }

    [Test]
    public void WriteStamp_PostgreSQL_UsesQuotedIdentifiersAndNow()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));

        ForgeKindler.WriteStamp(mockCmd, Platform.PostgreSQL, "abc");

        Assert.That(executed, Has.Count.EqualTo(2));
        Assert.That(executed[0], Is.EqualTo("DELETE FROM \"SchemaSmith\".\"KindleStamp\""));
        Assert.That(executed[1], Does.Contain("INSERT INTO \"SchemaSmith\".\"KindleStamp\"")
            .And.Contain("'abc'").And.Contain("NOW()"));
    }

    [Test]
    public void WriteStamp_ThrowsForEmptyStamp()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        Assert.Throws<ArgumentException>(() => ForgeKindler.WriteStamp(mockCmd, Platform.SqlServer, ""));
        Assert.Throws<ArgumentException>(() => ForgeKindler.WriteStamp(mockCmd, Platform.SqlServer, null));
    }

    [Test]
    public void ReadStamp_UsesPlatformAppropriateGuardQuery()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        mockCmd.ExecuteScalar().Returns(DBNull.Value);

        // PG existence check uses pg_class/pg_namespace (NOT a static KindleStamp reference, which PG
        // would validate at parse time and fail on a fresh DB). With the table absent (DBNull here),
        // ReadStamp returns before issuing the second SELECT, so CommandText is the catalog probe.
        ForgeKindler.ReadStamp(mockCmd, Platform.PostgreSQL);
        Assert.That(mockCmd.CommandText, Does.Contain("pg_catalog.pg_class").And.Contain("'KindleStamp'").And.Contain("'SchemaSmith'"));

        ForgeKindler.ReadStamp(mockCmd, Platform.MySQL);
        Assert.That(mockCmd.CommandText, Does.Contain("information_schema.tables").And.Contain("SchemaSmith_KindleStamp"));
    }

    [Test]
    public void AcquireKindleLock_SqlServer_RequestsSessionExclusiveApplock()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        string captured = null;
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => captured = mockCmd.CommandText);
        ForgeKindler.AcquireKindleLock(mockCmd, Platform.SqlServer);
        Assert.That(captured, Does.Contain("sp_getapplock").And.Contains("'Session'").And.Contains("'Exclusive'"));
    }

    [Test]
    public void AcquireKindleLock_ThrowsForUnsupportedPlatform()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        Assert.Throws<ArgumentException>(() => ForgeKindler.AcquireKindleLock(mockCmd, (Platform)99));
    }

    [Test]
    public void GetMySqlKindleLockName_FitsUnder64Chars_EvenForLongDatabaseNames()
    {
        // MySQL GET_LOCK names are capped at 64 chars (error 4163). The hashed key must stay under
        // the cap for ANY database name — test DBs use timestamp+guid suffixes that easily exceed
        // it when concatenated naively. Also assert determinism: same DB name -> same key.
        var longDb = "GenerateTableJson_Test_20260528_072842_2ad48a96_extra_padding_for_safety";
        var mockCmd = Substitute.For<IDbCommand>();
        var mockConn = Substitute.For<IDbConnection>();
        mockConn.Database.Returns(longDb);
        mockCmd.Connection.Returns(mockConn);

        var name1 = ForgeKindler.GetMySqlKindleLockName(mockCmd);
        var name2 = ForgeKindler.GetMySqlKindleLockName(mockCmd);

        Assert.That(name1, Has.Length.LessThanOrEqualTo(64),
            $"MySQL lock name must fit MySQL's 64-char cap. Got {name1.Length}: '{name1}'.");
        Assert.That(name1, Is.EqualTo(name2), "Hashed key must be deterministic per database name.");
        Assert.That(name1, Does.StartWith("SchemaSmith_Kindle_"),
            "Key must keep the SchemaSmith_Kindle_ prefix for diagnosability.");
    }

    [Test]
    public void DropSupersededPostgreSqlOverloads_DropsTheAuditedSignatures()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));

        ForgeKindler.DropSupersededPostgreSqlOverloads(mockCmd);

        Assert.That(executed, Has.Count.EqualTo(7));
        Assert.That(executed, Has.All.StartsWith("DROP PROCEDURE IF EXISTS"));
        Assert.That(executed.Any(s => s.Contains("\"ValidateTableOwnership\"(varchar, boolean)")));
        Assert.That(executed.Any(s => s.Contains("\"FixupTableOwnership\"(varchar)")));
        Assert.That(executed.Any(s => s.Contains("\"ValidateMaterializedViewOwnership\"(varchar, boolean)")));
        Assert.That(executed.Any(s => s.Contains("\"FixupMaterializedViewOwnership\"(varchar)")));
        Assert.That(executed.Any(s => s.Contains("\"FixupIndexOwnership\"(varchar)")));
        // The pre-RebuildPolicy arities. Without these two an already-kindled database keeps the old
        // signature as a second overload and every existing named-argument call resolves as ambiguous.
        Assert.That(executed.Any(s => s.Contains("\"ModifiedTableQuench\"(boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean)")),
            "The 10-boolean ModifiedTableQuench must be dropped, or the 13-argument replacement is ambiguous with it.");
        Assert.That(executed.Any(s => s.Contains("\"TableQuench\"(varchar, text, boolean, boolean, boolean, boolean)")),
            "The 6-argument TableQuench must be dropped, or the 9-argument replacement is ambiguous with it.");
    }

    [Test]
    public void KindleTheForge_SkipsKindle_WhenStampMatches()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));
        mockCmd.ExecuteScalar().Returns(ForgeKindler.ComputeKindleStamp(Platform.SqlServer));

        ForgeKindler.KindleTheForge(mockCmd, Platform.SqlServer);

        Assert.That(executed.Any(s => s.Contains("sp_getapplock")), "Lock must be acquired.");
        Assert.That(executed.Any(s => s.Contains("CREATE")), Is.False, "Skip path must run no kindling DDL.");
        Assert.That(executed.Any(s => s.Contains("INSERT INTO [SchemaSmith].[KindleStamp]")), Is.False, "Skip path must not re-stamp.");
        Assert.That(executed.Any(s => s.Contains("sp_releaseapplock")), "Lock must be released.");
    }

    [Test]
    public void KindleTheForge_Kindles_WhenStampMissing()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));
        mockCmd.ExecuteScalar().Returns(DBNull.Value); // no stamp -> fresh install

        ForgeKindler.KindleTheForge(mockCmd, Platform.SqlServer);

        Assert.That(executed.Any(s => s.Contains("CREATE")), "Kindle path must run kindling DDL.");
        Assert.That(executed.Any(s => s.Contains("INSERT INTO [SchemaSmith].[KindleStamp]")), "Kindle path must re-stamp.");
        Assert.That(executed.Any(s => s.Contains("sp_releaseapplock")), "Lock must be released.");
    }

    [Test]
    public void KindleTheForge_Rekindles_WhenForceReKindleEvenIfStampMatches()
    {
        var mockCmd = Substitute.For<IDbCommand>();
        var executed = new System.Collections.Generic.List<string>();
        mockCmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(mockCmd.CommandText));
        mockCmd.ExecuteScalar().Returns(ForgeKindler.ComputeKindleStamp(Platform.SqlServer));

        ForgeKindler.KindleTheForge(mockCmd, Platform.SqlServer, forceReKindle: true);

        Assert.That(executed.Any(s => s.Contains("CREATE")), "Force must kindle even when the stamp matches.");
        Assert.That(executed.Any(s => s.Contains("INSERT INTO [SchemaSmith].[KindleStamp]")), "Force must re-stamp.");
    }
}
