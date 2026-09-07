// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using log4net;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.Utility;

/// <summary>
/// Deploys SchemaSmith helper procedures, functions, and tables to the target database.
/// This "kindles the forge" - preparing the database for schema extraction and deployment.
/// Platform-aware: uses a per-platform descriptor list (GetKindlingScripts) to drive both execution and version-stamping.
/// </summary>
public static class ForgeKindler
{
    private static readonly ILog Log = LogFactory.GetLogger("ProgressLog");
    private const string KindleLockResource = "SchemaSmith_Kindle";

    // SQL Server legacy (XML) encoding kindle-list transforms: below the OPENJSON compat cliff the model is
    // ingested/compared as XML, so each OPENJSON/FOR JSON proc is swapped for its FOR XML PATH / .nodes() twin
    // and fn_FormatJson (used only by the JSON generate path, and itself OPENJSON-based) is dropped. The
    // {{ParseJson}}/{{TableDef}} token bodies also switch to XML — see ResolveKindleScript.
    private static readonly Dictionary<string, string> SqlServerXmlSwaps = new(StringComparer.Ordinal)
    {
        ["SchemaSmith.BootstrapTableQuench.sql"] = "SchemaSmith.BootstrapTableXmlQuench.sql",
        ["SchemaSmith.IndexOnlyQuench.sql"] = "SchemaSmith.IndexOnlyXmlQuench.sql",
        ["SchemaSmith.IndexedViewQuench.sql"] = "SchemaSmith.IndexedViewXmlQuench.sql",
        ["SchemaSmith.GenerateTableJson.sql"] = "SchemaSmith.GenerateTableXml.sql",
        ["SchemaSmith.GenerateIndexedViewJson.sql"] = "SchemaSmith.GenerateIndexedViewXml.sql",
    };

    private static readonly HashSet<string> SqlServerXmlSkips = new(StringComparer.Ordinal)
    {
        "SchemaSmith.fn_FormatJson.sql",
    };

    /// <summary>
    /// Deploy all SchemaSmith helper objects, version-gated and self-skipping. Acquires a
    /// session-scoped lock so concurrent first-arrivals serialize; if the in-DB stamp already
    /// matches the current kindle content (and not forceReKindle), returns without touching the
    /// helpers. Otherwise drops superseded PG overloads (PG only), re-kindles, and re-stamps.
    /// </summary>
    public static void KindleTheForge(IDbCommand command, Platform platform, bool forceReKindle = false,
        IngestEncoding encoding = IngestEncoding.Json, int serverMajorVersion = 0, string policy = "warn",
        bool allowReadOnlyTarget = false)
    {
        AcquireKindleLock(command, platform); // throws ArgumentException for unsupported platforms (before the try)
        try
        {
            // Footgun kill: a caller that does not supply the server version must NOT silently get the
            // serverMajorVersion == 0 ("assume OLD") branch on a modern server. That branch composes the
            // pre-2025 xml_compression reference (CONVERT(BIT, NULL)) into GenerateTableJSON and disables
            // the drift guard -- so on a real SQL Server 2025 box a version-less kindle stripped
            // XmlCompression from every extraction and never converged TurningItOff. "Not supplied" must
            // mean "detect it", never "guess old". SQL Server only: it is the sole platform whose kindle
            // tokens are version-sensitive, and TargetVersionDetector reads ProductVersion, which is safe
            // on every supported floor (2008R2+). Best-effort: an undetectable version falls back to 0,
            // i.e. the prior behaviour, so this can only ever improve correctness, never regress it. The
            // detected value flows into ComputeKindleStamp below and KindleScripts, keeping the stamp and
            // the resolved helpers consistent (a disagreement between them was the original 2025 defect).
            if (platform == Platform.SqlServer && serverMajorVersion == 0)
                serverMajorVersion = TargetVersionDetector.TryDetect(command, Platform.SqlServer)?.ServerComparable ?? 0;

            var expected = ComputeKindleStamp(platform, encoding, serverMajorVersion, policy);
            var current = ReadStamp(command, platform);
            if (!forceReKindle && string.Equals(current, expected, StringComparison.Ordinal))
            {
                Log.Info($"  Kindle stamp current ({expected[..12]}…) — skipping kindle");
                return;
            }

            // Only now is a WRITE about to happen, which is the only thing a read-only target rules out.
            // Probing here rather than up front keeps the common path at one round trip and means an
            // already-current replica never needs the question asked at all.
            //
            // Extraction opts in because it only ever reads the helpers -- an Availability Group readable
            // secondary being the case that matters, since it is the copy people are allowed to hammer.
            // Deploy does not opt in: it genuinely cannot work against a read-only database, and failing
            // here with a clear reason beats failing later on the first write.
            if (allowReadOnlyTarget && ReadOnlyTargetDetector.IsReadOnly(command, platform))
            {
                VerifyKindledOnReadOnlyTarget(command, platform, current, expected);
                return;
            }

            if (platform == Platform.PostgreSQL)
                DropSupersededPostgreSqlOverloads(command);

            KindleScripts(command, platform, encoding, serverMajorVersion, policy);
            if (platform.GetBasePlatform() == Platform.MySQL)
                CleanupMySqlStatusMessages(command);

            WriteStamp(command, platform, expected);
        }
        finally
        {
            ReleaseKindleLock(command, platform);
        }
    }

    private static void KindleScripts(IDbCommand command, Platform platform, IngestEncoding encoding,
        int serverMajorVersion, string policy)
    {
        foreach (var s in GetKindlingScripts(platform, encoding))
            KindleOneFile(command, s.FileName, platform, s.ReplaceParseJson, s.ReplaceTableDef, encoding,
                serverMajorVersion, policy);
    }

    // MySQL only: clear orphaned status rows from crashed sessions. Operational, not schema
    // content — runs after the table set is created and is excluded from the version stamp.
    private static void CleanupMySqlStatusMessages(IDbCommand command)
    {
        try
        {
            command.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE CreatedAt < DATE_SUB(NOW(3), INTERVAL 1 HOUR)";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Ignore if table doesn't exist yet
        }
    }

    /// <summary>
    /// Load a kindling script and apply the kindle-time token substitutions. This is the single
    /// resolution path — KindleOneFile executes the result, ComputeKindleStamp hashes it. Resolved
    /// here: {{ParseJson}} and {{TableDef}} (static resources), plus {{ServerMajorVersion}} and
    /// {{UnsupportedPolicy}} (the C#-detected server version + resolved run policy baked into the two
    /// SQL Server helper functions — see the note below). No iteration/schema-scoped token (e.g.
    /// {{SchemaName}}) is ever substituted in the kindle path, so the stamp is content + server-version
    /// + policy scoped: identical across databases of the same server and policy, differing only when
    /// a kindled script body, the detected server version, or the policy changes.
    /// </summary>
    internal static string ResolveKindleScript(string fileName, Platform platform,
        bool replaceParseJson, bool replaceTableDef, IngestEncoding encoding = IngestEncoding.Json,
        int serverMajorVersion = 0, string policy = "warn")
    {
        var script = ResourceLoader.Load(fileName, platform)
            ?? throw new Exception($"Script '{fileName}' not found for platform '{platform}'.");
        if (replaceParseJson)
            script = script.Replace("{{ParseJson}}", encoding == IngestEncoding.Xml
                ? GetParseTableXmlScript(platform)
                : GetParseTableJsonScript(platform));
        if (replaceTableDef)
        {
            var tableDefJson = GetSiblingTableDefJson(fileName, platform);
            // Legacy encoding: the bootstrap proc takes a single <Table> XML element, so convert the sibling
            // JSON object rather than shipping raw JSON the OPENJSON-free bootstrap can't shred.
            script = script.Replace("{{TableDef}}", encoding == IngestEncoding.Xml
                ? ModelXmlSerializer.ToIngestXmlObject(tableDefJson, "Table")
                : tableDefJson);
        }
        // SQL Server 2008-floor helpers bake the C#-detected server major version and the resolved
        // unsupported-feature policy at kindle time rather than reading SESSION_CONTEXT (2016+, which
        // would fail to CREATE the functions on a genuine pre-2016 binary). The tokens appear only in
        // the two SQL Server helper functions, so these replaces are a no-op on every other script.
        script = script.Replace("{{ServerMajorVersion}}", serverMajorVersion.ToString(CultureInfo.InvariantCulture))
                       .Replace("{{UnsupportedPolicy}}", policy);

        // {{XmlCompressionRead}} is a COLUMN REFERENCE, not a value, and that is why it is resolved here
        // rather than guarded at runtime. sys.partitions.xml_compression does not exist before SQL Server
        // 2025 -- verified on 2022 CU25, where the column lives only on sys.internal_partitions -- and a
        // procedure naming a column the server does not have fails to CREATE, taking the whole kindle
        // down. A runtime IF cannot save it because binding happens first. Composing the reference out at
        // kindle time is the same move LedgerViewFilter() makes in C# for sys.tables.ledger_view_id, and
        // it is why this needs no sp_executesql staging.
        //
        // 0 means "version not detected", which happens on paths that never probed. Treat that as OLD:
        // emitting NULL loses a value, emitting an unbindable column loses the entire kindle.
        // TWO tokens, and they MUST be resolved together. {{XmlCompressionCanRead}} is the guard that
        // decides whether the comparison runs at all, and it is baked here rather than tested at runtime
        // with fn_ServerMajorVersion() -- because those two can DISAGREE. fn_ServerMajorVersion falls
        // back to SERVERPROPERTY when no version was baked, so on a caller that kindles without passing
        // one, a runtime guard says "2025, compare away" while the token above resolved to NULL. The
        // comparison then reads NULL as "currently off" and REBUILDS every declared-ON table on every
        // deploy, while a declared-OFF table never converges. Both were observed on a real 2025 server
        // before this was split into two tokens. Deriving both from the same value makes that
        // disagreement impossible by construction.
        var xmlCompressionReadable = serverMajorVersion >= 17;
        script = script.Replace("{{XmlCompressionRead}}",
                            xmlCompressionReadable ? "p.xml_compression" : "CONVERT(BIT, NULL)")
                       .Replace("{{XmlCompressionCanRead}}", xmlCompressionReadable ? "1" : "0");

        return script;
    }

    /// <summary>
    /// Execute a single SQL script from embedded resources for the specified platform.
    /// Optionally substitutes {{ParseJson}} with the platform's ParseTableJson script body,
    /// and / or {{TableDef}} with the sibling .json resource (same base name as the script).
    /// </summary>
    public static void KindleOneFile(IDbCommand command, string fileName, Platform platform,
        bool replaceParseJsonToken = false, bool replaceTableDefToken = false,
        IngestEncoding encoding = IngestEncoding.Json, int serverMajorVersion = 0, string policy = "warn")
    {
        try
        {
            var script = ResolveKindleScript(fileName, platform, replaceParseJsonToken, replaceTableDefToken,
                encoding, serverMajorVersion, policy);

            if (platform.GetBasePlatform() == Platform.MySQL)
            {
                foreach (var batch in BatchSplitter.Split(script))
                {
                    command.CommandText = batch;
                    command.ExecuteNonQuery();
                }
            }
            else if (platform == Platform.PostgreSQL)
            {
                foreach (var statement in PostgreSqlStatementSplitter.Split(script))
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }
            }
            else if (platform.GetBasePlatform() == Platform.SqlServer)
            {
                // Split on GO so a kindled object can use the pre-2016 idempotent create form
                // (IF OBJECT_ID(...) DROP; GO; CREATE …) instead of the 2016 SP1 CREATE OR ALTER. A script
                // with no GO yields a single batch, so this is a no-op for any script that hasn't adopted it.
                foreach (var batch in SqlServerBatchSplitter.Split(script))
                {
                    command.CommandText = batch;
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                command.CommandText = script;
                command.ExecuteNonQuery();
            }
        }
        catch (Exception e)
        {
            throw new Exception($"Error occurred while kindling '{fileName}'. {e.Message}", e);
        }
    }

    /// <summary>
    /// Get the ParseTableJson script for the specified platform.
    /// </summary>
    public static string GetParseTableJsonScript(Platform platform)
    {
        return ResourceLoader.Load("ParseTableJsonIntoTempTables.sql", platform)
            ?? throw new Exception($"ParseTableJsonIntoTempTables.sql not found for platform '{platform}'.");
    }

    /// <summary>
    /// Get the XML-ingest twin of the ParseTableJson script (selected below the OPENJSON compat cliff).
    /// </summary>
    public static string GetParseTableXmlScript(Platform platform)
    {
        return ResourceLoader.Load("ParseTableXmlIntoTempTables.sql", platform)
            ?? throw new Exception($"ParseTableXmlIntoTempTables.sql not found for platform '{platform}'.");
    }

    /// <summary>
    /// Load the sibling .json resource for a kindling _Table.sql script. Convention: the JSON
    /// file shares the script's base name. For "Kindling_CompletedMigrationScripts_Table.sql"
    /// the resolver strips the "_Table.sql" suffix and loads "Kindling_CompletedMigrationScripts.json".
    /// </summary>
    internal static string GetSiblingTableDefJson(string scriptFileName, Platform platform)
    {
        var baseName = Path.GetFileNameWithoutExtension(scriptFileName);
        const string tableSuffix = "_Table";
        if (baseName.EndsWith(tableSuffix, StringComparison.Ordinal))
            baseName = baseName.Substring(0, baseName.Length - tableSuffix.Length);
        var jsonName = baseName + ".json";

        return ResourceLoader.Load(jsonName, platform)
            ?? throw new Exception($"Sibling JSON '{jsonName}' not found for kindling script '{scriptFileName}' on platform '{platform}'.");
    }

    /// <summary>
    /// One kindling script plus the token-substitution flags it needs. Single source of truth
    /// for the kindle order — both the executor (KindleScripts) and the version-stamp
    /// (ComputeKindleStamp) iterate this list, so the deployed text and the hashed text can
    /// never drift.
    /// </summary>
    internal readonly record struct KindleScript(string FileName, bool ReplaceParseJson = false, bool ReplaceTableDef = false);

    internal static KindleScript[] GetKindlingScripts(Platform platform, IngestEncoding encoding = IngestEncoding.Json)
    {
        // Route on base platform so variants (e.g. MariaDb -> MySQL) inherit the base kindling
        // list; each script still resolves through ResourceLoader's per-file variant fallback.
        KindleScript[] scripts = platform.GetBasePlatform() switch
        {
            Platform.SqlServer =>
            [
                new("Kindling_SchemaSmith_Schema.sql"),
                new("SchemaSmith.BootstrapTableQuench.sql"),
                new("Kindling_KindleStamp_Table.sql", ReplaceTableDef: true),
                // Ownership fallback for tables that cannot carry the ProductName extended property
                // (memory-optimized tables reject it). Regular tables still use the extended property;
                // this mirrors the table-based ownership PostgreSQL and MySQL use for every table.
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith.fn_StripParenWrapping.sql"),
                new("SchemaSmith.fn_NormalizeCheckExpression.sql"),
                new("SchemaSmith.fn_ColumnTypeArguments.sql"),
                new("SchemaSmith.fn_StripLeadingSelect.sql"),
                new("SchemaSmith.fn_StripBracketWrapping.sql"),
                new("SchemaSmith.fn_SafeBracketWrap.sql"),
                new("SchemaSmith.fn_SplitList.sql"),
                new("SchemaSmith.fn_ServerMajorVersion.sql"),
                new("SchemaSmith.fn_NormalizeTemporalRetentionPeriod.sql"),
                // Must follow fn_ServerMajorVersion: its CREATE is version-gated at kindle time and calls it.
                new("SchemaSmith.fn_RebuildBlockedReason.sql"),
                new("SchemaSmith.UnsupportedFeaturePolicy.sql"),
                new("SchemaSmith.DegradeUnsupportedColumnStore.sql"),
                new("SchemaSmith.DegradeUnsupportedFeatures.sql"),
                new("SchemaSmith.PrintWithNoWait.sql"),
                // Must follow fn_RebuildBlockedReason (it calls it to refuse) and PrintWithNoWait (its
                // WhatIf output), and precede the quench procedures that will elect a rebuild.
                new("SchemaSmith.RebuildTable.sql"),
                new("SchemaSmith.MissingTableAndColumnQuench.sql"),
                new("SchemaSmith.ModifiedTableQuench.sql"),
                new("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"),
                // Must follow MissingIndexesAndConstraintsQuench: enabling change tracking requires a
                // primary key, which a table created in the same run does not have until that pass.
                new("SchemaSmith.ChangeTrackingQuench.sql"),
                new("SchemaSmith.FileStreamColumnQuench.sql"),
                new("SchemaSmith.ForeignKeyQuench.sql"),
                new("SchemaSmith.TableQuench.sql", ReplaceParseJson: true),
                new("SchemaSmith.IndexOnlyQuench.sql"),
                new("SchemaSmith.fn_FormatJson.sql"),
                new("SchemaSmith.GenerateTableJson.sql"),
                new("SchemaSmith.ValidateIndexedViewOwnership.sql"),
                new("SchemaSmith.FixupIndexedViewOwnership.sql"),
                new("SchemaSmith.IndexedViewQuench.sql"),
                new("SchemaSmith.GenerateIndexedViewJson.sql"),
            ],
            Platform.PostgreSQL =>
            [
                new("Kindling_SchemaSmith_Schema.sql"),
                new("SchemaSmith.BootstrapTableQuench.sql"),
                new("Kindling_KindleStamp_Table.sql", ReplaceTableDef: true),
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                // TRANSITIONAL: tighten the ProductOwnership unique key to enforce one-owner-per-object
                // (runs after the table exists; BootstrapTableQuench can't reconcile a changed index).
                new("Kindling_ProductOwnership_IndexMigration.sql"),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith.ExecuteOrDebug.sql"),
                new("SchemaSmith.QuoteColumnList.sql"),
                new("SchemaSmith.QuoteIndexColumnList.sql"),
                new("SchemaSmith.StripParenWrapping.sql"),
                new("SchemaSmith.ColumnTypeArguments.sql"),
                new("SchemaSmith.StripTypeCast.sql"),
                new("SchemaSmith.ServerVersionNum.sql"),
                new("SchemaSmith.UnsupportedFeaturePolicy.sql"),
                new("SchemaSmith.IndexNullsNotDistinct.sql"),
                new("SchemaSmith.ColumnCompression.sql"),
                new("SchemaSmith.StatisticsExpressionColumns.sql"),
                new("SchemaSmith.StripLeadingSelect.sql"),
                new("SchemaSmith.RebuildBlockedReason.sql"),
                // Must follow RebuildBlockedReason (it calls it to refuse) and ExecuteOrDebug (its
                // execute/preview path), and precede the quench procedures that will elect a rebuild.
                new("SchemaSmith.RebuildTable.sql"),
                new("SchemaSmith.ValidateTableOwnership.sql"),
                new("SchemaSmith.FixupTableOwnership.sql"),
                new("SchemaSmith.FixupIndexOwnership.sql"),
                new("SchemaSmith.MissingTableAndColumnQuench.sql"),
                new("SchemaSmith.BuildExistingIndexesSnapshot.sql"),
                new("SchemaSmith.ModifiedTableQuench.sql"),
                new("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"),
                // Must follow MissingIndexesAndConstraintsQuench: REPLICA IDENTITY USING INDEX names an
                // index, which a table created in the same run does not have until that pass.
                new("SchemaSmith.ReplicaIdentityQuench.sql"),
                new("SchemaSmith.ForeignKeyQuench.sql"),
                new("SchemaSmith.TableQuench.sql", ReplaceParseJson: true),
                new("SchemaSmith.IndexOnlyQuench.sql"),
                new("SchemaSmith.FormatJson.sql"),
                new("SchemaSmith.GenerateTableJson.sql"),
                new("SchemaSmith.ValidateMaterializedViewOwnership.sql"),
                new("SchemaSmith.FixupMaterializedViewOwnership.sql"),
                new("SchemaSmith.MissingMaterializedViewIndexesQuench.sql"),
                new("SchemaSmith.MaterializedViewQuench.sql"),
                // Enum types as a MANAGED type (F5). Replaces a guarded CREATE TYPE whose value-list
                // edits were a permanent silent no-op once the type existed.
                // Domain types (F5). BEFORE the enum pair only by convention -- neither depends on the
                // other -- but both must follow StripParenWrapping, which the extraction function calls.
                new("SchemaSmith.DomainTypeQuench.sql"),
                new("SchemaSmith.GenerateDomainTypeJson.sql"),
                new("SchemaSmith.EnumTypeQuench.sql"),
                new("SchemaSmith.GenerateEnumTypeJson.sql"),
                new("SchemaSmith.SequenceQuench.sql"),
                new("SchemaSmith.GenerateSequenceJson.sql"),
            ],
            Platform.MySQL =>
            [
                // Scalar JSON helpers must kindle BEFORE BootstrapTableQuench: the Kindling_*_Table
                // scripts execute Bootstrap at kindle time, and Bootstrap (like ParseTableJson) calls
                // these helpers to shred its table definition.
                new("SchemaSmith_JsonScalarInt.sql"),
                new("SchemaSmith_JsonScalarStr.sql"),
                new("SchemaSmith_BootstrapTableQuench.sql"),
                new("Kindling_KindleStamp_Table.sql", ReplaceTableDef: true),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                new("Kindling_StatusMessages_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith_QuoteIdentifier.sql"),
                new("SchemaSmith_StripBacktickWrapping.sql"),
                new("SchemaSmith_SafeBacktickWrap.sql"),
                new("SchemaSmith_StripLeadingSelect.sql"),
                new("SchemaSmith_ServerVersionNum.sql"),
                new("SchemaSmith_UnsupportedFeaturePolicy.sql"),
                new("SchemaSmith_SupportsCheckConstraints.sql"),
                new("SchemaSmith_SupportsRenameColumn.sql"),
                new("SchemaSmith_SupportsRenameIndex.sql"),
                new("SchemaSmith_BuildIndexRenameClause.sql"),
                new("SchemaSmith_SupportsDescendingIndex.sql"),
                new("SchemaSmith_SupportsInvisibleIndex.sql"),
                new("SchemaSmith_SupportsFunctionalIndex.sql"),
                new("SchemaSmith_SupportsDefaultExpression.sql"),
                new("SchemaSmith_SupportsInvisibleColumn.sql"),
                new("SchemaSmith_SupportsColumnSrid.sql"),
                // Parses one option out of INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS -- the single blob
                // COMPRESSION, KEY_BLOCK_SIZE and MariaDB's PAGE_COMPRESSED family all live in. LOCATE
                // based rather than regex: REGEXP_SUBSTR does not exist on the MySQL 5.7 floor.
                new("SchemaSmith_CreateOption.sql"),
                // Scheduled events as a MANAGED type. EventMatches is the "has anything actually
                // changed" predicate and must precede EventQuench, which calls it -- converging an event
                // means DROP + CREATE, so a false "changed" would reset its schedule on every deploy.
                new("SchemaSmith_EventMatches.sql"),
                new("SchemaSmith_EventQuench.sql"),
                new("SchemaSmith_GenerateEventJson.sql"),
                new("SchemaSmith_SupportsApplicationTimePeriods.sql"),
                // Gates the per-column WITHOUT SYSTEM VERSIONING clause (#408). MariaDB-only, like the
                // period gate above it; calls SchemaSmith_ServerVersionNum, kindled earlier.
                new("SchemaSmith_SupportsSystemVersioning.sql"),
                new("SchemaSmith_NormalizeIndexColumns.sql"),
                new("SchemaSmith_IndexHasFunctionalKeyPart.sql"),
                new("SchemaSmith_NormalizeCheckExpression.sql"),
                // Canonicalises a partition expression before comparing declared against live
                // (#partitioning, K3) -- MySQL 5.7 echoes the text the user wrote while every other
                // supported engine rewrites it, so a literal compare would be engine-specific.
                new("SchemaSmith_NormalizePartitionExpression.sql"),
                new("SchemaSmith_UpperDataType.sql"),
                new("SchemaSmith_StripIntDisplayWidth.sql"),
                new("SchemaSmith_NormalizeColumnDefault.sql"),
                new("SchemaSmith_NumericDefaultsEqual.sql"),
                new("SchemaSmith_ColumnOnUpdateClause.sql"),
                new("SchemaSmith_IndexIsVisible.sql"),
                new("SchemaSmith_ColumnSrid.sql"),
                new("SchemaSmith_IsSystemTimePeriodColumn.sql"),
                new("SchemaSmith_SetSystemVersioningAlterHistory.sql"),
                new("SchemaSmith_TablePeriodsJson.sql"),
                // The catalog-to-package read for partitioning (#partitioning, K3). ONE shared
                // definition, unlike its Periods sibling: INFORMATION_SCHEMA.PARTITIONS exists on every
                // supported version of both engines, so no MariaDb override is needed.
                new("SchemaSmith_TablePartitioningJson.sql"),
                new("SchemaSmith_SnapshotIndexVisibility.sql"),
                new("SchemaSmith_SnapshotIndexExistence.sql"),
                new("SchemaSmith_DropCheckClause.sql"),
                new("SchemaSmith_IndexInvisibleClause.sql"),
                new("SchemaSmith_RebuildBlockedReason.sql"),
                // Must follow SchemaSmith_RebuildBlockedReason (it calls it to refuse) and the identifier
                // helpers above, and precede the quench procedures that will elect a rebuild.
                new("SchemaSmith_RebuildTable.sql"),
                // The general-tablespace placement read (F2b). A PROCEDURE with an OUT param, not a
                // function -- its MySQL body reaches INNODB_TABLES/INNODB_TABLESPACES only through dynamic
                // SQL (PREPARE/EXECUTE), which MySQL disallows inside a stored FUNCTION and which is the
                // only way to keep those MySQL-8.0+-only view names from binding at CREATE time and
                // breaking kindle on the MySQL 5.7 floor; MariaDb's override sets the OUT param NULL
                // unconditionally. Must precede BOTH GenerateTableJson (CALLs it for extraction) and
                // ModifiedTableQuench (CALLs it in the tablespace-move refuse guard), which is why it sits
                // just ahead of the former.
                new("SchemaSmith_TableTablespace.sql"),
                // The DATA DIRECTORY placement read (F2c), both engines -- unlike SchemaSmith_TableTablespace
                // above (MySQL-only), the MariaDb per-file override here resolves through the same
                // ResourceLoader variant fallback. Must precede BOTH GenerateTableJson (CALLs it for
                // extraction) and ModifiedTableQuench (CALLs it in the move-refuse guard), same ordering
                // reason as its sibling immediately above.
                new("SchemaSmith_TableDataDirectory.sql"),
                new("SchemaSmith_GenerateTableJson.sql"),
                new("SchemaSmith_ParseTableJson.sql"),
                new("SchemaSmith_MissingTableAndColumnQuench.sql"),
                new("SchemaSmith_ModifiedTableQuench.sql"),
                new("SchemaSmith_MissingIndexesAndConstraintsQuench.sql"),
                new("SchemaSmith_ForeignKeyQuench.sql"),
                new("SchemaSmith_IndexOnlyQuench.sql"),
                new("SchemaSmith_TableQuench.sql"),
            ],
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };

        // SQL Server legacy (XML) encoding: swap the OPENJSON/FOR JSON procs for their XML twins and drop
        // the JSON-only helpers, preserving each descriptor's token-substitution flags.
        if (encoding == IngestEncoding.Xml && platform.GetBasePlatform() == Platform.SqlServer)
            scripts = scripts
                .Where(s => !SqlServerXmlSkips.Contains(s.FileName))
                .Select(s => SqlServerXmlSwaps.TryGetValue(s.FileName, out var xmlFile) ? s with { FileName = xmlFile } : s)
                .ToArray();

        return scripts;
    }

    /// <summary>
    /// Returns the list of kindling script names for the specified platform.
    /// Useful for testing and diagnostics.
    /// </summary>
    internal static string[] GetKindlingScriptNames(Platform platform)
        => GetKindlingScripts(platform).Select(s => s.FileName).ToArray();

    /// <summary>
    /// Read the current kindle stamp, or null if the marker table doesn't exist yet (fresh install)
    /// or holds no row. Uses a guard so a missing table returns null rather than raising an error.
    ///
    /// PostgreSQL note: the original CASE WHEN to_regclass(...) ELSE (SELECT ... FROM KindleStamp) END
    /// approach fails at PARSE TIME on a fresh database — PG validates all table references in the
    /// query text regardless of which CASE branch will execute. We avoid the static table reference
    /// by querying pg_class/pg_namespace instead, and only issuing the second SELECT when the table
    /// is confirmed to exist.
    /// </summary>
    /// <summary>
    /// Does the kindle-stamp store exist at all? Distinguishes "never kindled here" (a hard error on a
    /// read-only target) from "kindled, but currency unknown" (a warning). ReadStamp collapses both to
    /// null, which is right for its own callers and wrong for this one.
    /// </summary>
    internal static bool KindleStampStoreExists(IDbCommand command, Platform platform)
    {
        command.CommandText = platform.GetBasePlatform() switch
        {
            Platform.SqlServer =>
                "SELECT CASE WHEN OBJECT_ID('[SchemaSmith].[KindleStamp]', 'U') IS NULL THEN 0 ELSE 1 END",
            Platform.PostgreSQL =>
                "SELECT COUNT(*) FROM pg_catalog.pg_class c " +
                "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = 'SchemaSmith' AND c.relname = 'KindleStamp' AND c.relkind = 'r'",
            Platform.MySQL =>
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = DATABASE() AND table_name = 'SchemaSmith_KindleStamp'",
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };
        var raw = command.ExecuteScalar();
        return raw != null && raw != DBNull.Value && Convert.ToInt64(raw) > 0;
    }

    /// <summary>What a read-only target's helper objects are, relative to this build.</summary>
    public enum ReadOnlyKindleState
    {
        /// <summary>Never kindled here. Nothing to extract with — a hard error.</summary>
        NotKindled,
        /// <summary>Kindled, but the stamp cannot be read. Usable, currency unknown — warn and say so.</summary>
        Unverifiable,
        /// <summary>Kindled at a different version than this build. Usable but possibly incomplete — warn.</summary>
        Stale,
        /// <summary>Kindled and matching this build. Proceed silently.</summary>
        Current
    }

    /// <summary>
    /// Decide what a read-only target's helpers are, kept separate from the I/O so the four outcomes can
    /// be tested without a database. <paramref name="readStamp"/> is deferred because it is only worth a
    /// round trip once the store is known to exist.
    /// </summary>
    internal static ReadOnlyKindleState ClassifyReadOnlyKindle(bool storeExists, Func<string> readStamp, string expectedStamp)
    {
        if (!storeExists) return ReadOnlyKindleState.NotKindled;

        var current = readStamp();
        if (string.IsNullOrEmpty(current)) return ReadOnlyKindleState.Unverifiable;

        return string.Equals(current, expectedStamp, StringComparison.Ordinal)
            ? ReadOnlyKindleState.Current
            : ReadOnlyKindleState.Stale;
    }

    /// <summary>
    /// A read-only target cannot be kindled, so confirm it is usable instead.
    /// <para>Three outcomes, and the split is deliberate. Helpers <b>missing</b> is a hard error: there
    /// is nothing to extract with, and proceeding would fail later with a confusing "could not find
    /// stored procedure" or, worse, quietly produce nothing. Helpers <b>stale</b> is a warning rather
    /// than an error, because refusing would make a secondary useless the moment the primary is one
    /// version ahead — which is most of the time. Helpers present but <b>unverifiable</b> is also a
    /// warning, and says exactly that rather than implying everything is fine.</para>
    /// </summary>
    internal static void VerifyKindledOnReadOnlyTarget(IDbCommand command, Platform platform, string currentStamp, string expectedStamp)
    {
        var state = ClassifyReadOnlyKindle(KindleStampStoreExists(command, platform),
                                           () => currentStamp, expectedStamp);

        if (state == ReadOnlyKindleState.NotKindled)
            throw new InvalidOperationException(
                "The SchemaSmith helper objects are not present on this read-only database, and they cannot be "
                + "created here. Kindle on the primary (any deploy or extraction against a writable copy does "
                + "it) and let the change reach this replica, then re-run. SchemaSmith does not attempt the "
                + "install because a read-only target rejects every write it would need.");

        if (state == ReadOnlyKindleState.Unverifiable)
        {
            Log.Warn("  Read-only target: kindling was SKIPPED and its currency could NOT be verified — the "
                     + "SchemaSmith helper objects present here MIGHT be out of date, and an extraction taken "
                     + "with helpers older than this build can be silently wrong. Verify against the primary "
                     + "if the result looks unexpected.");
            return;
        }

        if (state == ReadOnlyKindleState.Stale)
        {
            Log.Warn($"  Read-only target: kindling was SKIPPED and the helper objects here are OUT OF DATE "
                     + $"(found {Abbreviate(currentStamp)}, this build expects {Abbreviate(expectedStamp)}). Extraction "
                     + "will proceed against them and may not reflect everything this build understands. Kindle on "
                     + "the primary and let it reach this replica to clear the warning.");
            return;
        }

        Log.Info($"  Read-only target: kindling skipped; helper objects are current ({Abbreviate(expectedStamp)})");
    }

    private static string Abbreviate(string stamp) =>
        string.IsNullOrEmpty(stamp) ? "(none)" : (stamp.Length <= 12 ? stamp : stamp[..12] + "…");

    internal static string ReadStamp(IDbCommand command, Platform platform)
    {
        if (platform == Platform.PostgreSQL)
        {
            // First: check existence via catalog (no static reference to KindleStamp that PG would
            // validate at parse time). Returns 1 if the table exists, 0 otherwise.
            command.CommandText =
                "SELECT COUNT(*) FROM pg_catalog.pg_class c " +
                "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = 'SchemaSmith' AND c.relname = 'KindleStamp' AND c.relkind = 'r'";
            var existsScalar = command.ExecuteScalar();
            var exists = existsScalar != null && existsScalar != DBNull.Value && Convert.ToInt64(existsScalar) > 0;
            if (!exists) return null;

            // Table confirmed present — safe to reference it directly.
            command.CommandText = "SELECT \"Stamp\" FROM \"SchemaSmith\".\"KindleStamp\" LIMIT 1";
            var pgValue = command.ExecuteScalar();
            return pgValue == null || pgValue == DBNull.Value ? null : pgValue.ToString();
        }

        if (platform.GetBasePlatform() == Platform.MySQL)
        {
            // MySQL validates all table references at parse time — even inside a subquery behind a
            // WHERE EXISTS guard — so a single-statement existence-gated read raises 1146 on a fresh
            // install before SchemaSmith_KindleStamp has been created. Use the same two-query pattern
            // as the PostgreSQL path: confirm existence via information_schema first, then read.
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = DATABASE() AND table_name = 'SchemaSmith_KindleStamp'";
            var mysqlExistsScalar = command.ExecuteScalar();
            var mysqlExists = mysqlExistsScalar != null && mysqlExistsScalar != DBNull.Value
                              && Convert.ToInt64(mysqlExistsScalar) > 0;
            if (!mysqlExists) return null;

            command.CommandText = "SELECT Stamp FROM SchemaSmith_KindleStamp LIMIT 1";
            var mysqlValue = command.ExecuteScalar();
            return mysqlValue == null || mysqlValue == DBNull.Value ? null : mysqlValue.ToString();
        }

        command.CommandText = platform.GetBasePlatform() switch
        {
            Platform.SqlServer =>
                "IF OBJECT_ID('[SchemaSmith].[KindleStamp]', 'U') IS NULL SELECT CAST(NULL AS VARCHAR(64)) " +
                "ELSE SELECT TOP 1 [Stamp] FROM [SchemaSmith].[KindleStamp]",
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : value.ToString();
    }

    /// <summary>
    /// Replace the single stamp row with the supplied value. Caller holds the kindle lock, so a
    /// plain DELETE + INSERT is safe (single writer). The stamp is [0-9a-f]{64} — injection-safe to inline.
    /// </summary>
    internal static void WriteStamp(IDbCommand command, Platform platform, string stamp)
    {
        if (string.IsNullOrEmpty(stamp))
            throw new ArgumentException("Stamp must be a non-empty SHA-256 hex string.", nameof(stamp));

        var (deleteSql, insertSql) = platform.GetBasePlatform() switch
        {
            Platform.SqlServer => (
                "DELETE FROM [SchemaSmith].[KindleStamp]",
                $"INSERT INTO [SchemaSmith].[KindleStamp] ([Stamp], [UpdatedUtc]) VALUES ('{stamp}', GETUTCDATE())"),
            Platform.PostgreSQL => (
                "DELETE FROM \"SchemaSmith\".\"KindleStamp\"",
                $"INSERT INTO \"SchemaSmith\".\"KindleStamp\" (\"Stamp\", \"UpdatedUtc\") VALUES ('{stamp}', NOW())"),
            Platform.MySQL => (
                "DELETE FROM SchemaSmith_KindleStamp",
                $"INSERT INTO SchemaSmith_KindleStamp (Stamp, UpdatedUtc) VALUES ('{stamp}', NOW())"),
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };
        command.CommandText = deleteSql;
        command.ExecuteNonQuery();
        command.CommandText = insertSql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// SHA-256 (lowercase hex) of the resolved kindling scripts concatenated in kindle order.
    /// Scoped to (platform, encoding, server major version, policy) — it cannot depend on
    /// iteration/schema/database scope, so the stamp is identical across databases of the same server
    /// and policy. Any change to a kindled script, a token source (e.g. ParseTableJsonIntoTempTables.sql),
    /// the detected server version, or the run policy changes the resolved text and therefore the stamp
    /// -> the next kindle re-runs automatically (alternating version/policy across runs re-kindles; cheap).
    /// </summary>
    public static string ComputeKindleStamp(Platform platform, IngestEncoding encoding = IngestEncoding.Json,
        int serverMajorVersion = 0, string policy = "warn")
    {
        var sb = new StringBuilder();
        // The XML encoding swaps in different script bodies (and drops fn_FormatJson), so the concatenated
        // resolved text — and therefore the stamp — already differs from Json; no extra discriminator needed.
        foreach (var s in GetKindlingScripts(platform, encoding))
            sb.Append(ResolveKindleScript(s.FileName, platform, s.ReplaceParseJson, s.ReplaceTableDef, encoding,
                serverMajorVersion, policy));

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Acquire a SESSION-scoped lock guarding the whole (multi-statement, autocommit) kindle.
    /// Session — not transaction — scope is required: the kindle is many autocommit statements and
    /// MySQL DDL forces an implicit commit, so a transaction-scoped lock would release mid-kindle.
    /// Key folds in the database name where the lock namespace is broader than one DB (PG advisory
    /// locks are cluster-global; MySQL GET_LOCK names are server-global). Released in KindleTheForge's
    /// finally on the same connection.
    /// </summary>
    internal static void AcquireKindleLock(IDbCommand command, Platform platform)
    {
        switch (platform.GetBasePlatform())
        {
            case Platform.SqlServer:
                command.CommandText =
                    "DECLARE @r INT; " +
                    $"EXEC @r = sp_getapplock @Resource = '{KindleLockResource}', " +
                    "@LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; " +
                    // RAISERROR (not THROW) so the guard CREATEs/runs on the SQL Server 2008 floor — THROW is
                    // 2012+. Severity 16 surfaces as a SqlException to the caller, matching THROW's abort intent.
                    "IF @r < 0 RAISERROR('Could not acquire the SchemaSmith kindle lock.', 16, 1);";
                command.ExecuteNonQuery();
                break;
            case Platform.PostgreSQL:
                // PG advisory locks have no native wait-cap argument (unlike SQL Server's
                // @LockTimeout=60000 and MySQL's GET_LOCK(...,60)). A wedged holder will block
                // this acquire indefinitely. Worth a roadmap follow-up to add a try-loop with
                // backoff; for now the kindle is fast and contention is rare, so the asymmetry is
                // acceptable. Documented so a future reader doesn't think it's an oversight.
                command.CommandText =
                    $"SELECT pg_advisory_lock(hashtext(current_database() || ':{KindleLockResource}'))";
                command.ExecuteNonQuery();
                break;
            case Platform.MySQL:
                command.CommandText = $"SELECT GET_LOCK('{GetMySqlKindleLockName(command)}', 60)";
                var got = command.ExecuteScalar();
                if (got == null || got == DBNull.Value || Convert.ToInt64(got) != 1)
                    throw new TimeoutException("Could not acquire the SchemaSmith kindle lock (MySQL GET_LOCK timed out).");
                break;
            default:
                throw new ArgumentException($"Unsupported platform for kindling: {platform}", nameof(platform));
        }
    }

    /// <summary>
    /// Drop the prior signatures of the five PostgreSQL ownership procs that gained
    /// p_TemplateName/p_SchemaName params in schema-templates. PostgreSQL overloads by signature,
    /// so CREATE OR REPLACE of the new signature would otherwise ADD a second overload on an
    /// upgraded database and make every call ambiguous (42725). IF EXISTS makes this a no-op on
    /// fresh installs. Runs once per stamp change, under the kindle lock — no "doesn't exist" window.
    /// </summary>
    internal static void DropSupersededPostgreSqlOverloads(IDbCommand command)
    {
        string[] drops =
        [
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ValidateTableOwnership\"(varchar, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"FixupTableOwnership\"(varchar)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ValidateMaterializedViewOwnership\"(varchar, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"FixupMaterializedViewOwnership\"(varchar)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"FixupIndexOwnership\"(varchar)",
            // The pre-RebuildPolicy arities. PostgreSQL keys a procedure by its argument TYPES, so
            // CREATE OR REPLACE with three extra defaulted parameters leaves the old signature in place
            // as a second overload — and every existing call site passes named arguments that BOTH
            // overloads can satisfy, which resolves as "procedure is not unique" rather than picking one.
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ModifiedTableQuench\"(boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"TableQuench\"(varchar, text, boolean, boolean, boolean, boolean)",
        ];
        foreach (var sql in drops)
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Release the session-scoped kindle lock. Best-effort; failures here must not mask a kindle error.</summary>
    internal static void ReleaseKindleLock(IDbCommand command, Platform platform)
    {
        try
        {
            command.CommandText = platform.GetBasePlatform() switch
            {
                Platform.SqlServer => $"IF APPLOCK_MODE('public', '{KindleLockResource}', 'Session') <> 'NoLock' " +
                                      $"EXEC sp_releaseapplock @Resource = '{KindleLockResource}', @LockOwner = 'Session';",
                Platform.PostgreSQL => $"SELECT pg_advisory_unlock(hashtext(current_database() || ':{KindleLockResource}'))",
                Platform.MySQL => $"SELECT RELEASE_LOCK('{GetMySqlKindleLockName(command)}')",
                _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
            };
            command.ExecuteNonQuery();
        }
        catch
        {
            // The session ending releases all three lock types anyway; never let release mask a kindle failure.
        }
    }

    /// <summary>
    /// MySQL GET_LOCK names are server-global AND capped at 64 characters. Hash the current
    /// database name into a fixed-length key so long DB names (test DBs with timestamp+guid
    /// suffixes, or any user with a verbose naming convention) still fit under the cap and stay
    /// unique per database. Exposed internally so integration tests can probe the same key.
    /// </summary>
    internal static string GetMySqlKindleLockName(IDbCommand command)
    {
        var dbName = command.Connection?.Database ?? "";
        var dbHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dbName)))[..16].ToLowerInvariant();
        return $"{KindleLockResource}_{dbHash}"; // 19 + 1 + 16 = 36 chars, well under MySQL's 64
    }
}
