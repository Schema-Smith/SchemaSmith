// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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
            // A stored routine keeps the sql_mode it was created under, so the kindle session's mode is every kindled
            // procedure's mode for good. Kindle under the parse-neutral form of the server's mode, and stamp it, so a
            // server whose mode changes re-kindles rather than reusing routines created under another.
            using var sqlMode = platform.GetBasePlatform() == Platform.MySQL ? MySqlSessionSettings.UseParseNeutralSqlMode(command) : null;
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

            bool? xmlCompressionReadable = platform == Platform.SqlServer ? ReportsXmlCompression(command) : null;
            bool? checkConstraintsReadable = platform == Platform.MariaDb ? ReportsCheckConstraints(command) : null;
            var expected = ComputeKindleStamp(platform, encoding, serverMajorVersion, policy, sqlMode?.Mode, xmlCompressionReadable,
                checkConstraintsReadable);
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

            KindleScripts(command, platform, encoding, serverMajorVersion, policy, xmlCompressionReadable, checkConstraintsReadable);
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
        int serverMajorVersion, string policy, bool? xmlCompressionReadable, bool? checkConstraintsReadable)
    {
        foreach (var s in GetKindlingScripts(platform, encoding))
            KindleOneFile(command, s.FileName, platform, s.ReplaceParseJson, s.ReplaceTableDef, encoding,
                serverMajorVersion, policy, xmlCompressionReadable, checkConstraintsReadable);
    }

    // Whether sys.partitions carries xml_compression on this server. COL_LENGTH reads metadata only, so the question is
    // safe on every supported version; a server that cannot answer is treated as not reporting it.
    public static bool ReportsXmlCompression(IDbCommand command)
    {
        try
        {
            command.CommandText = "SELECT CASE WHEN COL_LENGTH('sys.partitions', 'xml_compression') IS NULL THEN 0 ELSE 1 END";
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) == "1";
        }
        catch (DbException)
        {
            return false;
        }
    }

    // Whether INFORMATION_SCHEMA carries CHECK_CONSTRAINTS, which MariaDB added in 10.2.22. Asked of the server rather than
    // read from the version, as for XML compression; a server that cannot answer is treated as having it, the kindle's
    // behaviour before the question was asked.
    public static bool ReportsCheckConstraints(IDbCommand command)
    {
        try
        {
            command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'CHECK_CONSTRAINTS'";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
        catch (DbException)
        {
            return true;
        }
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
        int serverMajorVersion = 0, string policy = "warn", bool? xmlCompressionReadable = null, bool? checkConstraintsReadable = null)
    {
        // MariaDB's column-level check reads INFORMATION_SCHEMA.CHECK_CONSTRAINTS, and a function naming a view the server
        // lacks fails to CREATE. Below 10.2.22 it takes the MySQL body, which reads nothing.
        var source = platform == Platform.MariaDb && checkConstraintsReadable == false && fileName == "SchemaSmith_ColumnLevelCheck.sql"
            ? Platform.MySQL
            : platform;
        var script = ResourceLoader.Load(fileName, source)
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
        // rather than guarded at runtime. sys.partitions.xml_compression is missing on 2022 CU25 (the column
        // lives only on sys.internal_partitions) and present on 2022 CU26 and 2025, so the kindle asks the
        // server (ReportsXmlCompression) rather than inferring it from the major version -- and a
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
        script = script.Replace("{{CdcRotateTable}}", SqlServerCdcRotateTable)
                       .Replace("{{RenderableIndexTypes}}", SqlServerRenderableIndexTypes)
                       .Replace("{{CollectSelectiveXmlIndexes}}", SqlServerCollectSelectiveXmlIndexes)
                       .Replace("{{ParseSelectiveXmlIndexesJson}}", SqlServerParseSelectiveXmlIndexesJson)
                       .Replace("{{ParseSelectiveXmlIndexesXml}}", SqlServerParseSelectiveXmlIndexesXml)
                       .Replace("{{CollectJsonIndexes}}", SqlServerCollectJsonIndexes)
                       .Replace("{{JsonIndexesTable}}", SqlServerJsonIndexesTable)
                       .Replace("{{ParseJsonIndexesJson}}", SqlServerParseJsonIndexesJson)
                       .Replace("{{ParseJsonIndexesXml}}", SqlServerParseJsonIndexesXml)
                       .Replace("{{DetectJsonIndexChanges}}", SqlServerDetectJsonIndexChanges)
                       .Replace("{{CreateMissingJsonIndexes}}", SqlServerCreateMissingJsonIndexes);

        var xmlReadable = xmlCompressionReadable ?? serverMajorVersion >= 17;
        script = script.Replace("{{XmlCompressionRead}}",
                            xmlReadable ? "p.xml_compression" : "CONVERT(BIT, NULL)")
                       .Replace("{{XmlCompressionCanRead}}", xmlReadable ? "1" : "0");

        // The ordered-columnstore key arrived with 2022 (sys.index_columns.column_store_order_ordinal), and naming the column
        // on an older server fails the CREATE as xml_compression does, so the read is composed out at kindle time.
        // Two tokens from one value, as for XML compression: the degrade clears a declared ORDER wherever the read cannot see
        // one, so an index is never created ordered where the next deploy would read it as unordered and re-create it.
        var columnStoreOrderReadable = serverMajorVersion >= 16;
        script = script.Replace("{{ColumnStoreOrderList}}", columnStoreOrderReadable ? SqlServerColumnStoreOrderList : "CONVERT(NVARCHAR(MAX), NULL)")
                       .Replace("{{ColumnStoreOrderCanRead}}", columnStoreOrderReadable ? "1" : "0");

        return script;
    }

    /// <summary>
    /// Execute a single SQL script from embedded resources for the specified platform.
    /// Optionally substitutes {{ParseJson}} with the platform's ParseTableJson script body,
    /// and / or {{TableDef}} with the sibling .json resource (same base name as the script).
    /// </summary>
    public static void KindleOneFile(IDbCommand command, string fileName, Platform platform,
        bool replaceParseJsonToken = false, bool replaceTableDefToken = false,
        IngestEncoding encoding = IngestEncoding.Json, int serverMajorVersion = 0, string policy = "warn",
        bool? xmlCompressionReadable = null, bool? checkConstraintsReadable = null)
    {
        try
        {
            var script = ResolveKindleScript(fileName, platform, replaceParseJsonToken, replaceTableDefToken,
                encoding, serverMajorVersion, policy, xmlCompressionReadable, checkConstraintsReadable);

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
        // Resolved here as well as in the kindle chain: SchemaQuench inlines this script into its own batches.
        return (ResourceLoader.Load("ParseTableJsonIntoTempTables.sql", platform)
            ?? throw new Exception($"ParseTableJsonIntoTempTables.sql not found for platform '{platform}'."))
            .Replace("{{ParseSelectiveXmlIndexesJson}}", SqlServerParseSelectiveXmlIndexesJson)
            .Replace("{{JsonIndexesTable}}", SqlServerJsonIndexesTable)
            .Replace("{{ParseJsonIndexesJson}}", SqlServerParseJsonIndexesJson);
    }

    /// <summary>
    /// The line ParseTableJsonIntoTempTables.sql carries between the block that creates the working-set
    /// temp tables and the block that fills them. Kept in one place because both halves are identified
    /// by it and a rename that missed one side would silently produce an empty phase.
    /// </summary>
    private const string IngestSplitMarker = "-- ===== INGEST SPLIT =====";

    /// <summary>
    /// Split the ParseTableJson script at the INGEST SPLIT marker.
    /// <para>
    /// The point of the split is that the payload can then be a real parameter. ADO.NET sends any
    /// parameterized command through sp_executesql, which runs in a NESTED scope — a temp table created
    /// there dies when the call returns, so the whole script could never be parameterized as one batch.
    /// Creating the tables first, in an unparameterized batch, puts them in the session scope, and the
    /// nested scope can freely INSERT into tables that already exist.
    /// </para>
    /// <para>
    /// What that buys: the model stops being escaped into the command text. A large product shipped ~20MB
    /// of JSON as a string literal that SQL Server had to parse as part of the batch on every work unit,
    /// which is both the parse and a plan-cache entry keyed by the whole 20MB. As a parameter it is sent
    /// as data and the batch text is the script alone.
    /// </para>
    /// <para>
    /// Callers that do not care (tests, and any path happy to inline) can keep using
    /// <see cref="GetParseTableJsonScript"/>, which still returns the script whole — the marker is a
    /// comment, so the undivided script behaves exactly as it always did.
    /// </para>
    /// </summary>
    public static (string CreateTables, string FillTables) GetParseTableJsonPhases(Platform platform)
    {
        var script = GetParseTableJsonScript(platform);
        var at = script.IndexOf(IngestSplitMarker, StringComparison.Ordinal);
        if (at < 0)
            throw new Exception(
                $"ParseTableJsonIntoTempTables.sql for platform '{platform}' has no '{IngestSplitMarker}' marker, " +
                "so the working-set creation cannot be separated from the fill. The two-phase ingest requires it.");

        return (script.Substring(0, at), script.Substring(at + IngestSplitMarker.Length));
    }

    /// <summary>
    /// Get the XML-ingest twin of the ParseTableJson script (selected below the OPENJSON compat cliff).
    /// </summary>
    public static string GetParseTableXmlScript(Platform platform)
    {
        return (ResourceLoader.Load("ParseTableXmlIntoTempTables.sql", platform)
            ?? throw new Exception($"ParseTableXmlIntoTempTables.sql not found for platform '{platform}'."))
            .Replace("{{ParseSelectiveXmlIndexesXml}}", SqlServerParseSelectiveXmlIndexesXml)
            .Replace("{{JsonIndexesTable}}", SqlServerJsonIndexesTable)
            .Replace("{{ParseJsonIndexesXml}}", SqlServerParseJsonIndexesXml);
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
    // The CDC rotations ModifiedTableQuench decides and CdcQuench applies. A temp table created inside a procedure dies
    // when it returns, so whoever runs both creates it first: TableQuench, the deploy session, or ModifiedTableQuench
    // when called on its own. One definition for all three, kindled in as {{CdcRotateTable}}.
    internal const string SqlServerCdcRotateTable =
        "CREATE TABLE #CdcRotate ([Schema] NVARCHAR(256) COLLATE DATABASE_DEFAULT, [TableName] NVARCHAR(256) COLLATE DATABASE_DEFAULT, " +
        "OldCaptureInstance NVARCHAR(256) COLLATE DATABASE_DEFAULT, NewFilegroup NVARCHAR(256) COLLATE DATABASE_DEFAULT, NewNetChanges BIT, " +
        "NewIndexName NVARCHAR(256) COLLATE DATABASE_DEFAULT, Reason NVARCHAR(20) COLLATE DATABASE_DEFAULT)";

    // The sys.indexes kinds a package can declare: clustered, nonclustered, both columnstores and memory-optimized hash.
    // Anything else (spatial, JSON, vector) is left out of extraction and of the live scans, which would otherwise
    // render it as a plain index: an undeployable package, or an index the deploy drops as unknown.
    public const string SqlServerRenderableIndexTypes = "1, 2, 5, 6, 7";

    // A columnstore index's ORDER columns, in sort order, or NULL when it has none. Reads the caller's sys.indexes as si.
    internal const string SqlServerColumnStoreOrderList =
        "(SELECT STUFF((SELECT ',' + QUOTENAME(COL_NAME(oc.[object_id], oc.column_id)) COLLATE DATABASE_DEFAULT " +
        "FROM sys.index_columns oc WHERE oc.[object_id] = si.[object_id] AND oc.index_id = si.index_id AND oc.column_store_order_ordinal > 0 " +
        "ORDER BY oc.column_store_order_ordinal FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, ''))";

    // A selective XML index (2012 SP1) is compared as its rendered WITH XMLNAMESPACES ... FOR (...) clause, built by this one
    // renderer from the declared paths and from the catalog's, so the compare is a string compare. Paths and namespaces are
    // sorted, so neither order matters. In the match, {0} is the paths or namespaces alias.
    private static string SelectiveXmlForClause(string paths, string namespaces, string match) =>
        "ISNULL(' WITH XMLNAMESPACES (' + STUFF((SELECT ', ' + CASE WHEN n.[Prefix] IS NULL THEN 'DEFAULT ' ELSE '' END + '''' + REPLACE(n.[Uri], '''', '''''') + '''' + ISNULL(' AS ' + n.[Prefix], '') " +
        $"FROM {namespaces} n WHERE {match.Replace("{0}", "n")} ORDER BY n.[Prefix] COLLATE Latin1_General_BIN2 " +
        "FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') + ')', '') + " +
        "' FOR (' + STUFF((SELECT ', ' + QUOTENAME(p.[Name]) + ' = ''' + REPLACE(p.[Path], '''', '''''') + '''' + " +
        "CASE WHEN p.[Type] IS NULL THEN '' WHEN p.[Type] = 'node()' OR p.[Type] LIKE 'xs:%' THEN ' AS XQUERY ''' + p.[Type] + '''' ELSE ' AS SQL ' + p.[Type] END + " +
        "CASE WHEN p.[MaxLength] > 0 THEN ' MAXLENGTH(' + CAST(p.[MaxLength] AS NVARCHAR(10)) + ')' ELSE '' END + " +
        "CASE WHEN p.[Singleton] = 1 THEN ' SINGLETON' ELSE '' END " +
        $"FROM {paths} p WHERE {match.Replace("{0}", "p")} ORDER BY p.[Name] COLLATE Latin1_General_BIN2 " +
        "FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') + ')'";

    // A SQL-typed path as the declared side normalizes it: upper case, no spaces, the engine's default length or precision.
    private const string SqlServerSelectiveXmlLiveSqlType =
        "UPPER(TYPE_NAME(p.user_type_id)) + CASE WHEN TYPE_NAME(p.user_type_id) IN ('nvarchar', 'nchar') THEN '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length / 2 AS VARCHAR(10)) END + ')' " +
        "WHEN TYPE_NAME(p.user_type_id) IN ('varchar', 'char', 'varbinary', 'binary') THEN '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length AS VARCHAR(10)) END + ')' " +
        "WHEN TYPE_NAME(p.user_type_id) IN ('decimal', 'numeric') THEN '(' + CAST(p.[precision] AS VARCHAR(10)) + ',' + CAST(p.scale AS VARCHAR(10)) + ')' " +
        "WHEN TYPE_NAME(p.user_type_id) IN ('datetime2', 'time', 'datetimeoffset') THEN '(' + CAST(p.scale AS VARCHAR(10)) + ')' ELSE '' END";

    // The catalog side: every selective XML index in the database, its paths and namespaces in the declared vocabulary, and
    // the KindPrefix / KindSuffix the live script is built from. xml_index_type and the selective views arrived with them
    // (2012 SP1), so a server without the column has none, and the reads are dynamic so the procedure still installs there.
    internal static readonly string SqlServerCollectSelectiveXmlIndexes =
        "IF OBJECT_ID('tempdb..#SelectiveXmlIndexes') IS NOT NULL DROP TABLE #SelectiveXmlIndexes\n" +
        "CREATE TABLE #SelectiveXmlIndexes ([object_id] INT, index_id INT, [ForPath] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [UsingIndex] NVARCHAR(200) COLLATE DATABASE_DEFAULT, " +
        "[KindPrefix] NVARCHAR(20) COLLATE DATABASE_DEFAULT, [KindSuffix] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT)\n" +
        "IF OBJECT_ID('tempdb..#SelectiveXmlPaths') IS NOT NULL DROP TABLE #SelectiveXmlPaths\n" +
        "CREATE TABLE #SelectiveXmlPaths ([object_id] INT, index_id INT, path_id INT, [Name] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [Path] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT, " +
        "[Type] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [MaxLength] INT, [Singleton] BIT)\n" +
        "IF OBJECT_ID('tempdb..#SelectiveXmlNamespaces') IS NOT NULL DROP TABLE #SelectiveXmlNamespaces\n" +
        "CREATE TABLE #SelectiveXmlNamespaces ([object_id] INT, index_id INT, [Prefix] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [Uri] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT)\n" +
        "IF COL_LENGTH('sys.xml_indexes', 'xml_index_type') IS NOT NULL\n" +
        "  EXEC('" + (
            "INSERT #SelectiveXmlIndexes ([object_id], index_id, [ForPath], [UsingIndex]) " +
            "SELECT x.[object_id], x.index_id, sp.[name], u.[name] FROM sys.xml_indexes x " +
            "LEFT JOIN sys.xml_indexes u ON u.[object_id] = x.[object_id] AND u.index_id = x.using_xml_index_id " +
            "LEFT JOIN sys.selective_xml_index_paths sp ON sp.[object_id] = x.[object_id] AND sp.index_id = x.using_xml_index_id AND sp.path_id = x.path_id " +
            "WHERE x.xml_index_type IN (2, 3); " +
            "INSERT #SelectiveXmlPaths ([object_id], index_id, path_id, [Name], [Path], [Type], [MaxLength], [Singleton]) " +
            "SELECT p.[object_id], p.index_id, p.path_id, p.[name], p.[path], " +
            "CASE WHEN p.is_node = 1 THEN 'node()' WHEN p.path_type_desc = 'SQL' THEN " + SqlServerSelectiveXmlLiveSqlType + " " +
            "WHEN p.is_xquery_type_inferred = 1 THEN NULL ELSE p.xquery_type_description END, " +
            "CASE WHEN p.xquery_max_length > 0 AND p.is_xquery_max_length_inferred = 0 THEN p.xquery_max_length END, p.is_singleton " +
            "FROM sys.selective_xml_index_paths p JOIN sys.xml_indexes x ON x.[object_id] = p.[object_id] AND x.index_id = p.index_id AND x.xml_index_type = 2; " +
            "INSERT #SelectiveXmlNamespaces ([object_id], index_id, [Prefix], [Uri]) " +
            "SELECT n.[object_id], n.index_id, CASE WHEN n.is_default_uri = 1 THEN NULL ELSE n.prefix END, n.uri FROM sys.selective_xml_index_namespaces n").Replace("'", "''") + "')\n" +
        "UPDATE s SET [KindPrefix] = CASE WHEN s.[ForPath] IS NULL THEN 'SELECTIVE ' ELSE '' END,\n" +
        "             [KindSuffix] = CASE WHEN s.[ForPath] IS NOT NULL THEN ' USING XML INDEX ' + QUOTENAME(s.[UsingIndex]) + ' FOR (' + QUOTENAME(s.[ForPath]) + ')'\n" +
        "                                 ELSE " + SelectiveXmlForClause("#SelectiveXmlPaths", "#SelectiveXmlNamespaces", "{0}.[object_id] = s.[object_id] AND {0}.index_id = s.index_id") + " END\n" +
        "  FROM #SelectiveXmlIndexes s";

    // The declared side, after #XmlIndexes is parsed (its Selective column holds the declaration as JSON or as XML, by tier):
    // the paths and namespaces into #XmlIndexPaths / #XmlIndexNamespaces, SQL types normalized as the catalog side renders
    // them, IsPrimary settled (a selective index is a primary, a ForPath one a secondary), and the KindPrefix / KindSuffix
    // every create and compare site renders the index with.
    private const string SqlServerSelectiveXmlTables =
        "IF OBJECT_ID('tempdb..#XmlIndexPaths') IS NOT NULL DROP TABLE #XmlIndexPaths\n" +
        "CREATE TABLE #XmlIndexPaths ([_RowId] BIGINT, [Name] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [Path] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT, " +
        "[Type] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [MaxLength] INT, [Singleton] BIT)\n" +
        "IF OBJECT_ID('tempdb..#XmlIndexNamespaces') IS NOT NULL DROP TABLE #XmlIndexNamespaces\n" +
        "CREATE TABLE #XmlIndexNamespaces ([_RowId] BIGINT, [Prefix] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [Uri] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT)\n";

    private const string SqlServerParseSelectiveXmlJson =
        "INSERT INTO #XmlIndexPaths ([_RowId], [Name], [Path], [Type], [MaxLength], [Singleton])\n" +
        "SELECT x.[_RowId], SchemaSmith.fn_StripBracketWrapping(sp.[Name]), sp.[Path], NULLIF(LTRIM(RTRIM(sp.[Type])), ''), sp.[MaxLength], ISNULL(sp.[Singleton], 0)\n" +
        "  FROM #XmlIndexes x\n" +
        "  CROSS APPLY OPENJSON(x.[Selective], '$.Paths') WITH ([Name] NVARCHAR(200) '$.Name', [Path] NVARCHAR(MAX) '$.Path', [Type] NVARCHAR(200) '$.Type', " +
        "[MaxLength] INT '$.MaxLength', [Singleton] BIT '$.SingletonCardinality') sp\n" +
        "  WHERE x.[Selective] IS NOT NULL\n" +
        "INSERT INTO #XmlIndexNamespaces ([_RowId], [Prefix], [Uri])\n" +
        "SELECT x.[_RowId], NULLIF(sn.[Prefix], ''), sn.[Uri]\n" +
        "  FROM #XmlIndexes x\n" +
        "  CROSS APPLY OPENJSON(x.[Selective], '$.Namespaces') WITH ([Prefix] NVARCHAR(200) '$.Prefix', [Uri] NVARCHAR(MAX) '$.Uri') sn\n" +
        "  WHERE x.[Selective] IS NOT NULL\n";

    private const string SqlServerParseSelectiveXmlXml =
        "INSERT INTO #XmlIndexPaths ([_RowId], [Name], [Path], [Type], [MaxLength], [Singleton])\n" +
        "SELECT x.[_RowId], SchemaSmith.fn_StripBracketWrapping(sp.value('(Name/text())[1]', 'NVARCHAR(200)')), sp.value('(Path/text())[1]', 'NVARCHAR(MAX)'),\n" +
        "       NULLIF(LTRIM(RTRIM(sp.value('(Type/text())[1]', 'NVARCHAR(200)'))), ''), sp.value('(MaxLength/text())[1]', 'INT'),\n" +
        "       CASE LOWER(sp.value('(SingletonCardinality/text())[1]', 'VARCHAR(8)')) WHEN 'true' THEN 1 ELSE 0 END\n" +
        "  FROM #XmlIndexes x\n" +
        "  CROSS APPLY x.[Selective].nodes('/Selective/Paths') AS SP(sp)\n" +
        "  WHERE x.[Selective] IS NOT NULL\n" +
        "INSERT INTO #XmlIndexNamespaces ([_RowId], [Prefix], [Uri])\n" +
        "SELECT x.[_RowId], NULLIF(sn.value('(Prefix/text())[1]', 'NVARCHAR(200)'), ''), sn.value('(Uri/text())[1]', 'NVARCHAR(MAX)')\n" +
        "  FROM #XmlIndexes x\n" +
        "  CROSS APPLY x.[Selective].nodes('/Selective/Namespaces') AS SN(sn)\n" +
        "  WHERE x.[Selective] IS NOT NULL\n";

    private static readonly string SqlServerRenderDeclaredXmlIndexes =
        "UPDATE #XmlIndexPaths SET [Type] = UPPER(REPLACE(REPLACE(REPLACE(REPLACE([Type], ' ', ''), CHAR(9), ''), CHAR(13), ''), CHAR(10), '')) " +
        "WHERE [Type] <> 'node()' AND [Type] NOT LIKE 'xs:%'\n" +
        "UPDATE #XmlIndexPaths SET [Type] = [Type] + CASE WHEN [Type] IN ('NVARCHAR', 'NCHAR', 'VARCHAR', 'CHAR', 'VARBINARY', 'BINARY') THEN '(1)' " +
        "WHEN [Type] IN ('DECIMAL', 'NUMERIC') THEN '(18,0)' WHEN [Type] IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET') THEN '(7)' ELSE '' END " +
        "WHERE CHARINDEX('(', [Type]) = 0 AND [Type] NOT LIKE 'xs:%'\n" +
        "UPDATE #XmlIndexPaths SET [Type] = REPLACE([Type], ')', ',0)') WHERE ([Type] LIKE 'DECIMAL(%' OR [Type] LIKE 'NUMERIC(%') AND CHARINDEX(',', [Type]) = 0\n" +
        "UPDATE #XmlIndexes SET [IsPrimary] = CASE WHEN [ForPath] IS NOT NULL THEN 0 WHEN [Selective] IS NOT NULL THEN 1 ELSE [IsPrimary] END\n" +
        "UPDATE x SET [KindPrefix] = CASE WHEN x.[ForPath] IS NOT NULL THEN '' WHEN x.[Selective] IS NOT NULL THEN 'SELECTIVE ' WHEN x.[IsPrimary] = 1 THEN 'PRIMARY ' ELSE '' END,\n" +
        "             [KindSuffix] = CASE WHEN x.[ForPath] IS NOT NULL THEN ' USING XML INDEX ' + x.[PrimaryIndex] + ' FOR (' + QUOTENAME(SchemaSmith.fn_StripBracketWrapping(x.[ForPath])) + ')'\n" +
        "                                 WHEN x.[Selective] IS NOT NULL THEN " + SelectiveXmlForClause("#XmlIndexPaths", "#XmlIndexNamespaces", "{0}.[_RowId] = x.[_RowId]") + "\n" +
        "                                 WHEN x.[IsPrimary] = 0 THEN ' USING XML INDEX ' + x.[PrimaryIndex] + ' FOR ' + x.[SecondaryIndexType]\n" +
        "                                 ELSE '' END\n" +
        "  FROM #XmlIndexes x";

    internal static readonly string SqlServerParseSelectiveXmlIndexesJson = SqlServerSelectiveXmlTables + SqlServerParseSelectiveXmlJson + SqlServerRenderDeclaredXmlIndexes;
    internal static readonly string SqlServerParseSelectiveXmlIndexesXml = SqlServerSelectiveXmlTables + SqlServerParseSelectiveXmlXml + SqlServerRenderDeclaredXmlIndexes;

    // JSON indexes (2025): one json column and a set of SQL/JSON paths. Both sides render the FOR list the same way -- every
    // path quoted, sorted binary (JSON keys are case-sensitive), none meaning the whole document -- so one binary comparison
    // of the rendered list finds any change, and the create writes the same text.
    private static string JsonIndexForClause(string pathsFrom) =>
        "' FOR (' + ISNULL(STUFF((SELECT ', N''' + REPLACE(p.[Path], '''', '''''') + '''' FROM " + pathsFrom +
        " AND RTRIM(p.[Path]) <> '' ORDER BY p.[Path] COLLATE Latin1_General_BIN2 FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''), 'N''$''') + ')'";

    // The catalog side: every JSON index in the database. sys.json_indexes arrived with them, so the reads are dynamic and
    // the procedures still install on a server without it, where the list is simply empty.
    internal static readonly string SqlServerCollectJsonIndexes =
        "IF OBJECT_ID('tempdb..#LiveJsonIndexes') IS NOT NULL DROP TABLE #LiveJsonIndexes\n" +
        "CREATE TABLE #LiveJsonIndexes ([object_id] INT, index_id INT, [Name] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [Column] NVARCHAR(200) COLLATE DATABASE_DEFAULT, " +
        "[OptimizeForArraySearch] BIT, [FillFactor] TINYINT, [ForClause] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT, [Compression] NVARCHAR(60) COLLATE DATABASE_DEFAULT)\n" +
        "IF OBJECT_ID('tempdb..#LiveJsonIndexPaths') IS NOT NULL DROP TABLE #LiveJsonIndexPaths\n" +
        "CREATE TABLE #LiveJsonIndexPaths ([object_id] INT, index_id INT, [Path] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT)\n" +
        "IF OBJECT_ID('sys.json_indexes') IS NOT NULL\n" +
        "  EXEC('" + (
            "INSERT #LiveJsonIndexes ([object_id], index_id, [Name], [Column], [OptimizeForArraySearch], [FillFactor]) " +
            "SELECT j.[object_id], j.index_id, j.[name], QUOTENAME(COL_NAME(ic.[object_id], ic.column_id)), j.optimize_for_array_search, NULLIF(j.fill_factor, 0) " +
            "FROM sys.json_indexes j JOIN sys.index_columns ic ON ic.[object_id] = j.[object_id] AND ic.index_id = j.index_id; " +
            "INSERT #LiveJsonIndexPaths ([object_id], index_id, [Path]) SELECT p.[object_id], p.index_id, p.[path] FROM sys.json_index_paths p").Replace("'", "''") + "')\n" +
        "UPDATE l SET [ForClause] = " + JsonIndexForClause("#LiveJsonIndexPaths p WHERE p.[object_id] = l.[object_id] AND p.index_id = l.index_id") + "\n" +
        "  FROM #LiveJsonIndexes l\n" +
        // DATA_COMPRESSION lands on the index's internal table, not on the index.
        "UPDATE l SET [Compression] = (SELECT MAX(p.data_compression_desc) FROM sys.internal_tables it JOIN sys.partitions p ON p.[object_id] = it.[object_id] AND p.index_id = 1\n" +
        "                              WHERE it.parent_object_id = l.[object_id] AND it.parent_minor_id = l.index_id)\n" +
        "  FROM #LiveJsonIndexes l";

    // The declared side's working set. On the JSON tier it is created before the ingest split, so it outlives the fill.
    internal const string SqlServerJsonIndexesTable =
        "IF OBJECT_ID('tempdb..#JsonIndexes') IS NOT NULL DROP TABLE #JsonIndexes\n" +
        "CREATE TABLE #JsonIndexes ([_RowId] BIGINT NULL, [Schema] NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL, [TableName] NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL, " +
        "[IndexName] NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL, [Column] NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL, [Paths] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, " +
        "[ForClause] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, [OptimizeForArraySearch] BIT NULL, [FillFactor] TINYINT NULL, [CompressionType] NVARCHAR(100) COLLATE DATABASE_DEFAULT NULL, " +
        "[ShouldApplyExpression] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL, [VariantName] NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL)\n";

    // NORMALIZE, then the ShouldApplyExpression filter, for either tier's rows.
    private const string SqlServerNormalizeJsonIndexes =
        "UPDATE #JsonIndexes SET [IndexName] = SchemaSmith.fn_SafeBracketWrap([IndexName]), [Column] = SchemaSmith.fn_SafeBracketWrap([Column]),\n" +
        "       [OptimizeForArraySearch] = ISNULL([OptimizeForArraySearch], 0), [FillFactor] = NULLIF([FillFactor], 0), [CompressionType] = UPPER(NULLIF(RTRIM([CompressionType]), ''))\n" +
        "SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #JsonIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX))\n" +
        "                         FROM #JsonIndexes WITH (NOLOCK)\n" +
        "                         WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''\n" +
        "                         FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')\n" +
        "EXEC(@v_SQL)\n";

    internal static readonly string SqlServerParseJsonIndexesJson =
        "RAISERROR('Parse JSON Indexes from Json', 10, 100) WITH NOWAIT\n" +
        "INSERT INTO #JsonIndexes ([_RowId], [Schema], [TableName], [IndexName], [Column], [Paths], [OptimizeForArraySearch], [FillFactor], [CompressionType], [ShouldApplyExpression], [VariantName])\n" +
        "SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), t.[Schema], t.[Name], j.[IndexName], j.[Column], j.[Paths], j.[OptimizeForArraySearch], j.[FillFactor], j.[CompressionType], j.[ShouldApplyExpression], j.[VariantName]\n" +
        "  FROM #TableDefinitions t WITH (NOLOCK)\n" +
        "  CROSS APPLY OPENJSON(t.[JsonIndexes]) WITH ([IndexName] NVARCHAR(500) '$.Name', [Column] NVARCHAR(500) '$.Column', [Paths] NVARCHAR(MAX) '$.Paths' AS JSON, " +
        "[OptimizeForArraySearch] BIT '$.OptimizeForArraySearch', [FillFactor] TINYINT '$.FillFactor', [CompressionType] NVARCHAR(100) '$.CompressionType', [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression', " +
        "[VariantName] NVARCHAR(128) '$.VariantName') j\n" +
        "UPDATE j SET [ForClause] = " + JsonIndexForClause("OPENJSON(j.[Paths]) WITH ([Path] NVARCHAR(MAX) '$') p WHERE 1 = 1") + ", [Paths] = NULL FROM #JsonIndexes j\n" +
        SqlServerNormalizeJsonIndexes;

    internal static readonly string SqlServerParseJsonIndexesXml =
        "RAISERROR('Parse JSON Indexes from Xml', 10, 100) WITH NOWAIT\n" +
        "INSERT INTO #JsonIndexes ([_RowId], [Schema], [TableName], [IndexName], [Column], [ForClause], [OptimizeForArraySearch], [FillFactor], [CompressionType], [ShouldApplyExpression], [VariantName])\n" +
        "SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), t.[Schema], t.[Name], jx.value('(Name/text())[1]', 'NVARCHAR(500)'), jx.value('(Column/text())[1]', 'NVARCHAR(500)'),\n" +
        "       " + JsonIndexForClause("(SELECT n.value('.', 'NVARCHAR(MAX)') AS [Path] FROM jx.nodes('Paths') AS P(n)) p WHERE 1 = 1") + ",\n" +
        "       jx.value('(OptimizeForArraySearch/text())[1]', 'BIT'), jx.value('(FillFactor/text())[1]', 'TINYINT'), jx.value('(CompressionType/text())[1]', 'NVARCHAR(100)'),\n" +
        "       jx.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'), jx.value('(VariantName/text())[1]', 'NVARCHAR(128)')\n" +
        "  FROM #TableDefinitions t WITH (NOLOCK)\n" +
        "  CROSS APPLY t.[TableXml].nodes('/Table/JsonIndexes') AS Y(jx)\n" +
        SqlServerNormalizeJsonIndexes;

    // A declared index whose column, paths or array-search option differ from the live one, or whose declared fill factor
    // or compression does, is re-created. Needs {{CollectJsonIndexes}} first.
    internal const string SqlServerDetectJsonIndexChanges =
        "IF OBJECT_ID('tempdb..#JsonIndexChanges') IS NOT NULL DROP TABLE #JsonIndexChanges\n" +
        "SELECT j.[Schema], j.[TableName], j.[IndexName]\n" +
        "  INTO #JsonIndexChanges\n" +
        "  FROM #JsonIndexes j WITH (NOLOCK)\n" +
        "  JOIN #LiveJsonIndexes l WITH (NOLOCK) ON l.[object_id] = OBJECT_ID(j.[Schema] + '.' + j.[TableName]) AND l.[Name] = SchemaSmith.fn_StripBracketWrapping(j.[IndexName])\n" +
        "  WHERE l.[Column] <> j.[Column] COLLATE DATABASE_DEFAULT\n" +
        "     OR l.[ForClause] <> j.[ForClause] COLLATE Latin1_General_BIN2\n" +
        "     OR l.[OptimizeForArraySearch] <> j.[OptimizeForArraySearch]\n" +
        "     OR (j.[FillFactor] IS NOT NULL AND j.[FillFactor] <> ISNULL(l.[FillFactor], 100))\n" +
        "     OR (j.[CompressionType] IS NOT NULL AND j.[CompressionType] <> ISNULL(l.[Compression], 'NONE') COLLATE DATABASE_DEFAULT)\n";

    // Sets @v_SQL to one CREATE JSON INDEX per declared index the table does not have; each caller runs it its own way.
    internal const string SqlServerCreateMissingJsonIndexes =
        "RAISERROR('Add Missing JSON Indexes', 10, 100) WITH NOWAIT\n" +
        "SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Creating JSON index ' + j.[Schema] + '.' + j.[TableName] + '.' + j.[IndexName] +\n" +
        "                                CASE WHEN RTRIM(ISNULL(j.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(j.[VariantName]), '''', '''''') + ')' ELSE '' END +\n" +
        "                                CASE WHEN ol.[Form] = 'unavailable' THEN ' (offline: ' + ol.[Reason] + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +\n" +
        "                                'CREATE JSON INDEX ' + j.[IndexName] + ' ON ' + j.[Schema] + '.' + j.[TableName] + ' (' + j.[Column] + ')' + j.[ForClause] +\n" +
        "                                CASE WHEN j.[OptimizeForArraySearch] = 1 OR j.[FillFactor] IS NOT NULL OR j.[CompressionType] IS NOT NULL\n" +
        "                                     THEN ' WITH (' + STUFF(CASE WHEN j.[OptimizeForArraySearch] = 1 THEN ', OPTIMIZE_FOR_ARRAY_SEARCH = ON' ELSE '' END +\n" +
        "                                                            ISNULL(', FILLFACTOR = ' + CONVERT(NVARCHAR(10), j.[FillFactor]), '') +\n" +
        "                                                            ISNULL(', DATA_COMPRESSION = ' + j.[CompressionType], ''), 1, 2, '') + ')' ELSE '' END + ';' + CHAR(13) + CHAR(10) +\n" +
        "                                'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode) VALUES (@@SPID, ''index'', ''' + REPLACE(j.[Schema] + '.' + j.[TableName] + '.' + j.[IndexName], '''', '''''') + ''', ''created'', ' +\n" +
        "                                CASE WHEN ol.[Form] IS NULL THEN 'NULL' ELSE '''' + ol.[Form] + '''' END + ');' AS NVARCHAR(MAX))\n" +
        "  FROM #JsonIndexes j WITH (NOLOCK)\n" +
        // The caller's #OnlineForms: a JSON index has no online build, so under OnlineApply it is recorded as built offline.
        "  LEFT JOIN #OnlineForms ol WITH (NOLOCK) ON ol.[Kind] = 'jsonIndex' AND ol.[Schema] = j.[Schema] AND ol.[TableName] = j.[TableName]\n" +
        "                                         AND ol.[IndexName] = SchemaSmith.fn_StripBracketWrapping(j.[IndexName])\n" +
        "  WHERE OBJECT_ID(j.[Schema] + '.' + j.[TableName]) IS NOT NULL\n" +
        "    AND INDEXPROPERTY(OBJECT_ID(j.[Schema] + '.' + j.[TableName]), SchemaSmith.fn_StripBracketWrapping(j.[IndexName]), 'IndexID') IS NULL\n" +
        "  ORDER BY j.[Schema], j.[TableName], j.[IndexName]\n" +
        "  FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')\n";

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
                new("Kindling_ScriptStamp_Table.sql", ReplaceTableDef: true),
                // Ownership fallback for tables that cannot carry the ProductName extended property
                // (memory-optimized tables reject it). Regular tables still use the extended property;
                // this mirrors the table-based ownership PostgreSQL and MySQL use for every table.
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                // #242: authored-vs-canonical expression mapping, so a re-read of an engine-rewritten
                // expression stops reading as a change. Kindled like the other bootstrap tables.
                new("Kindling_ExpressionMap_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith.fn_StripParenWrapping.sql"),
                new("SchemaSmith.fn_NormalizeCheckExpression.sql"),
                // #242: the authored-vs-canonical decision and the pass that records it.
                new("SchemaSmith.fn_ExpressionMapUnchanged.sql"),
                new("SchemaSmith.fn_ExpressionMapEffective.sql"),
                new("SchemaSmith.ExpressionMapRecord.sql"),
                // #508: an unrecorded expression is checked on a scratch copy before it is re-applied.
                new("SchemaSmith.ExpressionMapAdopt.sql"),
                new("SchemaSmith.fn_ColumnTypeArguments.sql"),
                new("SchemaSmith.fn_StripLeadingSelect.sql"),
                new("SchemaSmith.fn_StripBracketWrapping.sql"),
                new("SchemaSmith.fn_NormalizeDataType.sql"),
                new("SchemaSmith.fn_SafeBracketWrap.sql"),
                // Full-text columns are not always ONE identifier ([Doc] TYPE COLUMN [DocType]), so the
                // split has to happen inside the wrap. Must follow fn_SafeBracketWrap, which it calls.
                new("SchemaSmith.fn_SafeBracketWrapFullTextColumn.sql"),
                new("SchemaSmith.fn_SplitList.sql"),
                new("SchemaSmith.fn_ServerMajorVersion.sql"),
                new("SchemaSmith.fn_NormalizeTemporalRetentionPeriod.sql"),
                // Must follow fn_ServerMajorVersion: its CREATE is version-gated at kindle time and calls it.
                new("SchemaSmith.fn_RebuildBlockedReason.sql"),
                new("SchemaSmith.UnsupportedFeaturePolicy.sql"),
                new("SchemaSmith.fn_EnterpriseFeaturesUnavailable.sql"),
                new("SchemaSmith.fn_FullTextInstalled.sql"),
                new("SchemaSmith.PrintDowngrade.sql"),
                new("SchemaSmith.fn_OnlineOperationsAvailable.sql"),
                new("SchemaSmith.fn_OnlineIndexForm.sql"),
                new("SchemaSmith.fn_OnlineAlterColumnForm.sql"),
                new("SchemaSmith.StageOnlineForms.sql"),
                new("SchemaSmith.ResolvePausedIndexBuilds.sql"),
                new("SchemaSmith.RefuseUnmetOnlineRequire.sql"),
                new("SchemaSmith.RebuildOptionOnlyIndexChanges.sql"),
                new("SchemaSmith.DegradeUnsupportedColumnStore.sql"),
                new("SchemaSmith.DegradeUnsupportedFullText.sql"),
                new("SchemaSmith.DegradeUnsupportedSpatialIndexes.sql"),
                new("SchemaSmith.DegradeUnsupportedSelectiveXmlIndexes.sql"),
                new("SchemaSmith.DegradeUnsupportedJsonIndexes.sql"),
                new("SchemaSmith.DegradeDatabaseToggles.sql"),
                new("SchemaSmith.DegradeUnsupportedFeatures.sql"),
                // Fills the caller's #GraphKeyParts: the graph columns behind a $from_id / $to_id key part (2017+, dynamic).
                new("SchemaSmith.StageGraphKeyParts.sql"),
                // Moves declared period columns from #Columns onto #Tables; both parse tiers CALL it, so it precedes them.
                new("SchemaSmith.LiftPeriodColumns.sql"),
                // Records a ledger table's declared generated columns; both parse tiers CALL it, so it precedes them.
                new("SchemaSmith.LiftLedgerColumns.sql"),
                new("SchemaSmith.PrintWithNoWait.sql"),
                new("SchemaSmith.RecordWouldChanges.sql"),
                new("SchemaSmith.CdcPreflight.sql"),
                // What a drop or a swap takes off an object and the package does not carry. Must follow
                // PrintWithNoWait (the WhatIf output) and precede RebuildTable and the view quench, which call the
                // pair around the drop.
                new("SchemaSmith.CapturePermissions.sql"),
                new("SchemaSmith.ReapplyPermissions.sql"),
                // A changed module's signatures, captured before the ALTER that strips them and applied again after.
                new("SchemaSmith.CaptureSignatures.sql"),
                new("SchemaSmith.ReapplySignatures.sql"),
                // Must follow fn_RebuildBlockedReason (it calls it to refuse) and PrintWithNoWait (its
                // WhatIf output), and precede the quench procedures that will elect a rebuild.
                new("SchemaSmith.RebuildTable.sql"),
                new("SchemaSmith.ValidateTableOwnership.sql"),
                new("SchemaSmith.MissingTableAndColumnQuench.sql"),
                // Must precede ModifiedTableQuench, which CALLs it. Split out of that procedure because
                // SQL Server compiles a procedure's statements whether or not they run and caches the plan
                // per object PER DATABASE -- 328 lines of rare-attribute validation were being compiled on
                // every first deploy to every database for a feature set most packages never touch.
                new("SchemaSmith.ValidateDeclaredTableAttributes.sql"),
                // Must precede ModifiedTableQuench, which CALLs it, for the same compile-cost reason.
                new("SchemaSmith.CdcIndexGuard.sql"),
                new("SchemaSmith.ModifiedTableQuench.sql"),
                // A temporal table's period columns; MissingIndexesAndConstraintsQuench CALLs it before its index pass.
                new("SchemaSmith.PeriodColumnQuench.sql"),
                new("SchemaSmith.LedgerQuench.sql"),
                new("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"),
                // Declared descriptions (MS_Description), converged once every table, column and index exists.
                // Must precede TableQuench and IndexOnlyQuench, which CALL it.
                new("SchemaSmith.ExtendedPropertyQuench.sql"),
                // Must follow MissingIndexesAndConstraintsQuench: enabling change tracking requires a
                // primary key, which a table created in the same run does not have until that pass.
                new("SchemaSmith.ChangeTrackingQuench.sql"),
                new("SchemaSmith.FileStreamColumnQuench.sql"),
                new("SchemaSmith.CdcQuench.sql"),
                new("SchemaSmith.ForeignKeyQuench.sql"),
                // Edge constraints (2019), after the foreign keys and once every node table exists; floor-safe (dynamic reads).
                new("SchemaSmith.EdgeConstraintQuench.sql"),
                new("SchemaSmith.ConstraintStateQuench.sql"),
                // A declared index's Disabled state, converged once the index exists. Must precede TableQuench and
                // IndexOnlyQuench, which CALL it.
                new("SchemaSmith.IndexStateQuench.sql"),
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
                new("Kindling_ScriptStamp_Table.sql", ReplaceTableDef: true),
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                // #242: authored-vs-canonical expression mapping, so a re-read of an engine-rewritten
                // expression stops reading as a change. Kindled like the other bootstrap tables.
                new("Kindling_ExpressionMap_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith.ExecuteOrDebug.sql"),
                new("SchemaSmith.RecordWouldChanges.sql"),
                new("SchemaSmith.QuoteColumnList.sql"),
                new("SchemaSmith.QuoteIndexColumnList.sql"),
                new("SchemaSmith.QuoteName.sql"),
                // #242: declared index and statistics definitions compared in the form the catalog reads back.
                // SQL-language functions are validated at creation, so the splitter and StripParenWrapping
                // must be kindled before the statistics forms that call them.
                new("SchemaSmith.SplitTopLevelList.sql"),
                // One reading of an index key's expression, COLLATE, operator class and parameters, behind the
                // declared-side normaliser and quoter and the live-side rendering.
                new("SchemaSmith.IndexKeyForms.sql"),
                new("SchemaSmith.NormalizeIndexColumnList.sql"),
                new("SchemaSmith.StripParenWrapping.sql"),
                new("SchemaSmith.StatisticsDefinitionForms.sql"),
                // A declared statistics target (13+), converged after the create pass by MissingIndexesAndConstraintsQuench
                // and IndexOnlyQuench, which CALL it; below 13 it routes the declaration through the policy.
                new("SchemaSmith.StatisticsTargetQuench.sql"),
                // #242: the authored-vs-canonical decision and the pass that records it.
                new("SchemaSmith.ExpressionMapUnchanged.sql"),
                new("SchemaSmith.ExpressionMapRecord.sql"),
                // #508: an unrecorded expression is checked on a scratch copy before it is re-applied.
                new("SchemaSmith.ExpressionMapAdopt.sql"),
                // Read by ModifiedTableQuench: a declared identity sequence option that differs from the live sequence.
                new("SchemaSmith.IdentityOptionChanges.sql"),
                new("SchemaSmith.ColumnTypeArguments.sql"),
                // Calls ColumnTypeArguments and QuoteName, and is called by extraction and the table quench.
                new("SchemaSmith.ColumnDataType.sql"),
                new("SchemaSmith.StripTypeCast.sql"),
                new("SchemaSmith.ServerVersionNum.sql"),
                new("SchemaSmith.UnsupportedFeaturePolicy.sql"),
                new("SchemaSmith.IndexNullsNotDistinct.sql"),
                // The engine's fill factor per access method, so an index declared without one compares against what
                // the engine actually uses; and the one rendering of an index's WITH clause every create arm shares.
                new("SchemaSmith.IndexFillFactorDefault.sql"),
                new("SchemaSmith.IndexWithClause.sql"),
                // PG18-adaptive conperiod read: a primary key or unique constraint declared WITHOUT OVERLAPS.
                new("SchemaSmith.ConstraintWithoutOverlaps.sql"),
                // PG15-adaptive confdelsetcols read: the columns an ON DELETE SET NULL (col, ...) applies to.
                new("SchemaSmith.ForeignKeySetNullColumns.sql"),
                new("SchemaSmith.ConstraintEnforced.sql"),
                new("SchemaSmith.CheckPredicate.sql"),
                new("SchemaSmith.ColumnCompression.sql"),
                new("SchemaSmith.StatisticsExpressionColumns.sql"),
                new("SchemaSmith.StripLeadingSelect.sql"),
                new("SchemaSmith.RebuildBlockedReason.sql"),
                // What a drop or a swap takes off an object and the package does not carry. Must follow
                // ExecuteOrDebug and precede RebuildTable and MaterializedViewQuench, which call the pair.
                new("SchemaSmith.CapturePermissions.sql"),
                new("SchemaSmith.ReapplyPermissions.sql"),
                // Must follow RebuildBlockedReason (it calls it to refuse) and ExecuteOrDebug (its
                // execute/preview path), and precede the quench procedures that will elect a rebuild.
                new("SchemaSmith.RebuildTable.sql"),
                new("SchemaSmith.ValidateTableOwnership.sql"),
                new("SchemaSmith.FixupTableOwnership.sql"),
                new("SchemaSmith.FixupIndexOwnership.sql"),
                // OnlineApply: the form a statement takes, the queue its CONCURRENTLY statements wait in until the
                // procedure returns, and the REQUIRE refusal. Read by the table and index quenches below.
                new("SchemaSmith.OnlineApplyForm.sql"),
                new("SchemaSmith.DeferOnlineWork.sql"),
                new("SchemaSmith.RefuseUnmetOnlineRequire.sql"),
                new("SchemaSmith.MissingTableAndColumnQuench.sql"),
                new("SchemaSmith.BuildExistingIndexesSnapshot.sql"),
                new("SchemaSmith.ModifiedTableQuench.sql"),
                new("SchemaSmith.MissingIndexesAndConstraintsQuench.sql"),
                // Declared descriptions (COMMENT ON), converged once every table, column and index exists.
                // Must precede TableQuench and IndexOnlyQuench, which CALL it.
                new("SchemaSmith.DescriptionQuench.sql"),
                // Must follow MissingIndexesAndConstraintsQuench: REPLICA IDENTITY USING INDEX names an
                // index, which a table created in the same run does not have until that pass.
                new("SchemaSmith.ReplicaIdentityQuench.sql"),
                new("SchemaSmith.ForeignKeyQuench.sql"),
                new("SchemaSmith.ConstraintStateQuench.sql"),
                new("SchemaSmith.TableQuench.sql", ReplaceParseJson: true),
                // Same {{ParseJson}} body as TableQuench, exposed on its own so SchemaQuench can hand the
                // model over as an argument instead of escaping it into an anonymous DO block.
                new("SchemaSmith.ParseTableJson.sql", ReplaceParseJson: true),
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
                // Name keys: every comparison of a name from the package with a name from the catalog goes
                // through one of these, Bootstrap's included. IdentifierKey for database, table and view names
                // (folded only when lower_case_table_names >= 1); NameKeyCI for everything the engine treats
                // case-insensitively on every setting.
                new("SchemaSmith_IdentifierKey.sql"),
                new("SchemaSmith_NameKeyCI.sql"),
                new("SchemaSmith_BootstrapTableQuench.sql"),
                new("Kindling_KindleStamp_Table.sql", ReplaceTableDef: true),
                new("Kindling_ScriptStamp_Table.sql", ReplaceTableDef: true),
                new("Kindling_CompletedMigrationScripts_Table.sql", ReplaceTableDef: true),
                new("Kindling_ProductOwnership_Table.sql", ReplaceTableDef: true),
                new("Kindling_StatusMessages_Table.sql", ReplaceTableDef: true),
                new("Kindling_ChangeAudit_Table.sql", ReplaceTableDef: true),
                // #242: authored-vs-canonical expression mapping, so a re-read of an engine-rewritten
                // expression stops reading as a change. Kindled like the other bootstrap tables.
                new("Kindling_ExpressionMap_Table.sql", ReplaceTableDef: true),
                new("SchemaSmith_QuoteIdentifier.sql"),
                new("SchemaSmith_StripBacktickWrapping.sql"),
                new("SchemaSmith_SafeBacktickWrap.sql"),
                new("SchemaSmith_StripLeadingSelect.sql"),
                new("SchemaSmith_ServerVersionNum.sql"),
                new("SchemaSmith_ServerVersionPatchNum.sql"),
                new("SchemaSmith_CheckTableJoin.sql"),
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
                // Declared sequences (MariaDB 10.3+): the MariaDb override converges them, the MySQL file refuses them.
                new("SchemaSmith_SequenceQuench.sql"),
                new("SchemaSmith_GenerateEventJson.sql"),
                new("SchemaSmith_SupportsApplicationTimePeriods.sql"),
                // Gates the per-column WITHOUT SYSTEM VERSIONING clause (#408). MariaDB-only, like the
                // period gate above it; calls SchemaSmith_ServerVersionNum, kindled earlier.
                new("SchemaSmith_SupportsSystemVersioning.sql"),
                new("SchemaSmith_NormalizeIndexColumns.sql"),
                // The one declared-vs-live index-kind rule both index-change detectors call.
                new("SchemaSmith_IndexTypesMatch.sql"),
                new("SchemaSmith_IndexHasFunctionalKeyPart.sql"),
                // Calls IndexHasFunctionalKeyPart, SupportsFunctionalIndex and ServerVersionPatchNum, above.
                new("SchemaSmith_IndexKeyPartsUnsupported.sql"),
                // Calls IndexHasFunctionalKeyPart, above; read by ParseTableJson.
                new("SchemaSmith_StripFullLengthPrefixes.sql"),
                // Calls IndexHasFunctionalKeyPart, NameKeyCI and IdentifierKey; read by ParseTableJson.
                new("SchemaSmith_RespellIndexColumns.sql"),
                new("SchemaSmith_NormalizeCheckExpression.sql"),
                // #242: the authored-vs-canonical decision and the pass that records it.
                new("SchemaSmith_ExpressionMapUnchanged.sql"),
                new("SchemaSmith_ExpressionMapRecord.sql"),
                // #508: an unrecorded expression is checked on a scratch copy before it is re-applied.
                new("SchemaSmith_ExpressionMapAdopt.sql"),
                // Canonicalises a partition expression before comparing declared against live
                // (#partitioning, K3) -- MySQL 5.7 echoes the text the user wrote while every other
                // supported engine rewrites it, so a literal compare would be engine-specific.
                new("SchemaSmith_NormalizePartitionExpression.sql"),
                new("SchemaSmith_UpperDataType.sql"),
                // Read by ParseTableJson: ENUM/SET members lose trailing spaces at CREATE.
                new("SchemaSmith_TrimEnumMembers.sql"),
                new("SchemaSmith_StripIntDisplayWidth.sql"),
                new("SchemaSmith_NormalizeColumnDefault.sql"),
                // The DESIRED-side twin of the line above, so both sides of the default comparison fold
                // the same way. Must follow it: it calls it.
                new("SchemaSmith_NormalizeDeclaredDefault.sql"),
                new("SchemaSmith_NumericDefaultsEqual.sql"),
                // #520: a default is a literal or an expression by the catalog's own account, and an expression default
                // compares through the engine's own rendering. In call order: the last calls the first two.
                new("SchemaSmith_IsExpressionDefault.sql"),
                new("SchemaSmith_ExpressionDefaultText.sql"),
                new("SchemaSmith_CleanCheckClause.sql"),
                new("SchemaSmith_ExpressionDefaultsEqual.sql"),
                new("SchemaSmith_ColumnOnUpdateClause.sql"),
                new("SchemaSmith_IndexIsVisible.sql"),
                new("SchemaSmith_ColumnSrid.sql"),
                // NULL on MySQL; the MariaDb override reads the check MariaDB names after the column (its column-level check).
                new("SchemaSmith_ColumnLevelCheck.sql"),
                // The check MariaDB adds to a JSON column itself (NULL on MySQL and below MariaDB 10.4.3), and whether a
                // live column is that shape: the generator folds it to JSON, the parse compares a declared JSON with it.
                new("SchemaSmith_JsonAliasCheck.sql"),
                new("SchemaSmith_IsJsonAliasColumn.sql"),
                // Temporal keys: the gate (MariaDB 10.5.3; MySQL never) and the period a key is declared over (NULL on
                // MySQL; the MariaDb override reads KEY_PERIOD_USAGE from 11.4).
                new("SchemaSmith_SupportsWithoutOverlaps.sql"),
                new("SchemaSmith_SupportsVectorIndex.sql"),
                new("SchemaSmith_IndexPeriodJson.sql"),
                new("SchemaSmith_IsSystemTimePeriodColumn.sql"),
                // MySQL 8.0.30+'s generated invisible primary key (my_row_id): neither extracted nor dropped (0 on MariaDB).
                new("SchemaSmith_IsGeneratedInvisiblePrimaryKey.sql"),
                // A declared system-time period column's role, and the gate for adding explicit ones to an implicit table (11.7).
                new("SchemaSmith_SystemTimePeriodRole.sql"),
                // Read by ModifiedTableQuench's generated-status passes: a VIRTUAL <-> STORED switch is a drop and re-add.
                new("SchemaSmith_GeneratedKindChanged.sql"),
                // Read by ModifiedTableQuench STEP 2.9: a type change takes down the foreign keys on its column first (MA-054).
                new("SchemaSmith_ColumnTypeDiffers.sql"),
                // Read by the same step: a widening the engine makes under the key leaves the key in place.
                new("SchemaSmith_TypeChangeKeepsForeignKey.sql"),
                // Read by the index detectors: a case-only respelling a foreign key blocks is a no-op on these MariaDB patches.
                new("SchemaSmith_CaseRespellKeepsOldSpelling.sql"),
                new("SchemaSmith_SupportsExplicitSystemTimeConversion.sql"),
                new("SchemaSmith_SetSystemVersioningAlterHistory.sql"),
                // KEEP around a new versioned table's index work only (MySQL no-op, MariaDb override); the index pass CALLs it.
                new("SchemaSmith_KeepHistoryForNewVersionedTable.sql"),
                new("SchemaSmith_TablePeriodsJson.sql"),
                // The catalog-to-package read for partitioning (#partitioning, K3). ONE shared
                // definition, unlike its Periods sibling: INFORMATION_SCHEMA.PARTITIONS exists on every
                // supported version of both engines, so no MariaDb override is needed.
                new("SchemaSmith_TablePartitioningJson.sql"),
                // SYSTEM_TIME partitioning (MariaDB): the count/STARTS and AUTO gates, and whether a declaration can be created.
                new("SchemaSmith_SupportsSystemTimePartitionCount.sql"),
                new("SchemaSmith_SupportsSystemTimeAutoPartitions.sql"),
                new("SchemaSmith_SupportsGeneratedColumnCollation.sql"),
                new("SchemaSmith_SystemTimePartitionable.sql"),
                new("SchemaSmith_SnapshotIndexVisibility.sql"),
                new("SchemaSmith_SnapshotIndexExistence.sql"),
                new("SchemaSmith_DropCheckClause.sql"),
                new("SchemaSmith_IndexInvisibleClause.sql"),
                new("SchemaSmith_RebuildBlockedReason.sql"),
                // The grants a DROP takes off a routine, captured and put back around a script that drops it.
                new("SchemaSmith_CapturePermissions.sql"),
                new("SchemaSmith_ReapplyPermissions.sql"),
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
                // InnoDB's filename-encoded spelling of a name, which both placement reads below match on.
                new("SchemaSmith_InnodbName.sql"),
                new("SchemaSmith_TableTablespace.sql"),
                // The DATA DIRECTORY placement read (F2c), both engines -- unlike SchemaSmith_TableTablespace
                // above (MySQL-only), the MariaDb per-file override here resolves through the same
                // ResourceLoader variant fallback. Must precede BOTH GenerateTableJson (CALLs it for
                // extraction) and ModifiedTableQuench (CALLs it in the move-refuse guard), same ordering
                // reason as its sibling immediately above.
                new("SchemaSmith_TableDataDirectory.sql"),
                new("SchemaSmith_GenerateTableJson.sql"),
                new("SchemaSmith_CatalogSnapshot.sql"),
                new("SchemaSmith_ParseTableJson.sql"),
                new("SchemaSmith_RefuseUnsupportedIndexes.sql"),
                // A VECTOR index where the engine has none is skipped with a downgrade; called beside the refusals.
                new("SchemaSmith_DegradeVectorIndexes.sql"),
                new("SchemaSmith_DegradeUnsupportedColumnFeatures.sql"),
                // OnlineApply: each table's mode and its added columns' positions, and the INSTANT / INPLACE, LOCK=NONE
                // ladder every converted ALTER runs through. Called by the table and index quenches below.
                new("SchemaSmith_StageOnlineApply.sql"),
                new("SchemaSmith_ExecuteOnline.sql"),
                new("SchemaSmith_ValidateTableOwnership.sql"),
                new("SchemaSmith_MissingTableAndColumnQuench.sql"),
                // #530: a table's FullTextIndexes, created and re-created by both index paths, not only the index-only one.
                new("SchemaSmith_DropModifiedFullTextIndexes.sql"),
                new("SchemaSmith_CreateMissingFullTextIndexes.sql"),
                new("SchemaSmith_ModifiedTableQuench.sql"),
                // An existing table's declared primary key, added or replaced; both index paths call it.
                new("SchemaSmith_ConvergePrimaryKeys.sql"),
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
        int serverMajorVersion = 0, string policy = "warn", string sqlMode = null, bool? xmlCompressionReadable = null,
        bool? checkConstraintsReadable = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(sqlMode))
            sb.Append("sql_mode=").Append(sqlMode).Append('\n');
        // The XML encoding swaps in different script bodies (and drops fn_FormatJson), so the concatenated
        // resolved text — and therefore the stamp — already differs from Json; no extra discriminator needed.
        foreach (var s in GetKindlingScripts(platform, encoding))
            sb.Append(ResolveKindleScript(s.FileName, platform, s.ReplaceParseJson, s.ReplaceTableDef, encoding,
                serverMajorVersion, policy, xmlCompressionReadable, checkConstraintsReadable));

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
            // The pre-OnlineApply arities, for the same reason.
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ModifiedTableQuench\"(boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, boolean, text, integer, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"TableQuench\"(varchar, text, boolean, boolean, boolean, boolean, text, integer, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"IndexOnlyQuench\"(varchar, text, boolean, boolean, boolean, boolean, boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"MissingIndexesAndConstraintsQuench\"(boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ForeignKeyQuench\"(boolean)",
            "DROP PROCEDURE IF EXISTS \"SchemaSmith\".\"ConstraintStateQuench\"(boolean)",
            // Took the column's sequence; it now takes the column's schema, table and name, and the old one is left unused.
            "DROP FUNCTION IF EXISTS \"SchemaSmith\".\"IdentityOptionChanges\"(text, regclass)",
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
