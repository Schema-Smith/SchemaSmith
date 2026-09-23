// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;
using Schema.Domain;
using Schema.Delivery;
using Schema.Isolators;

namespace Schema.Utility;

/// <summary>
/// Generates platform-aware data delivery merge scripts.
/// SQL Server and PostgreSQL use MERGE syntax; MySQL uses INSERT/REPLACE/ON DUPLICATE KEY UPDATE.
/// </summary>
public static class MergeScriptHelper
{
    // ---- Delivery-scoped catalog memo ----
    //
    // Building one table's merge script reads INFORMATION_SCHEMA twice, and GetColumnInfoMySql has six
    // call sites, so a single table was read roughly four times over. Measured on the 11-table Chinook
    // demo: of 132 INFORMATION_SCHEMA reads in a no-op quench, 86 -- 65% -- were those two queries. On
    // MySQL and MariaDB that is not a cheap repeat: every INFORMATION_SCHEMA query materialises to a
    // DISK-based temporary table, unconditionally, so the lever is FEWER reads rather than faster ones.
    //
    // WHY THIS IS SOUND, and the limit that makes it so. SchemaSmith's catalog reads are point-in-time
    // over a catalog the quench itself mutates, so a global snapshot would be wrong. This memo is not
    // one: it lives only for the duration of a single data-delivery pass, which performs DML and no DDL
    // -- the user's own table-data scripts run AFTER DeliverTables returns, and delivery itself only
    // inserts and updates rows. Outside a scope there is no caching at all, so every other phase behaves
    // exactly as before.
    //
    // ThreadStatic rather than a shared dictionary: work units run in parallel on their own threads and
    // against their own connections, and one unit's catalog must never answer for another's.
    [ThreadStatic]
    private static Dictionary<string, object> _catalogMemo;

    /// <summary>
    /// Opens a scope in which repeated catalog reads for the same table are answered once. Only valid
    /// around a run that performs no DDL -- see the note on <see cref="MergeScriptHelper"/>. Disposing
    /// discards the memo; nested scopes are safe and the outermost one owns the lifetime.
    /// </summary>
    public static IDisposable BeginCatalogScope() => CatalogScope.Begin();

    // Open and close are STATIC members of the scope type, not its constructor and Dispose body: the
    // analyzers refuse a static field assigned in a constructor (S3010) or written from an instance
    // member (S2696), and they are right that either reads as a surprise. The scope object itself carries
    // only the "am I the outermost" flag, so a nested scope disposing cannot tear down the outer one's
    // memo.
    private sealed class CatalogScope : IDisposable
    {
        private readonly bool _owns;

        private CatalogScope(bool owns) => _owns = owns;

        internal static CatalogScope Begin()
        {
            var owns = _catalogMemo == null;
            if (owns) _catalogMemo = new Dictionary<string, object>(StringComparer.Ordinal);
            return new CatalogScope(owns);
        }

        private static void Close() => _catalogMemo = null;

        public void Dispose()
        {
            if (_owns) Close();
        }
    }

    private static T Memoized<T>(string key, Func<T> read) where T : class
    {
        if (_catalogMemo == null) return read();
        if (_catalogMemo.TryGetValue(key, out var cached)) return (T)cached;
        var value = read();
        _catalogMemo[key] = value;
        return value;
    }
    // SQL Server types that cannot be represented in JSON or compared reliably
    internal const string SqlServerUnsupportedTypeFilter = "AND c.DATA_TYPE NOT IN ('sql_variant', 'rowversion', 'timestamp')";

    // PostgreSQL types that cannot be represented in JSON or compared reliably.
    // Note: 'point' and 'polygon' are NOT excluded — they overlap with PostGIS types.
    internal const string PostgreSqlUnsupportedTypeFilter =
        "AND c.udt_name NOT IN ('tsvector', 'tsquery', 'money', 'box', 'circle', 'line', 'lseg', 'path')" +
        "\n    AND NOT EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace" +
        "\n                    WHERE t.typname = c.udt_name AND n.nspname = c.udt_schema AND t.typtype = 'c')";

    /// <summary>
    /// Extracts the union of all property names from a JSON array string.
    /// Returns null if the data is empty/null (caller should include all columns).
    /// </summary>
    internal static HashSet<string> GetJsonDataKeys(string tableData)
    {
        if (string.IsNullOrWhiteSpace(tableData)) return null;
        try
        {
            var arr = JArray.Parse(tableData);
            if (arr.Count == 0) return null;
            return arr
                .SelectMany(t => ((JObject)t).Properties().Select(p => p.Name))
                .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        }
        catch
        {
            return null; // If parsing fails, include all columns
        }
    }

    // B1: the XML-delivery analogue of GetJsonDataKeys. The delivery XML shape is
    // <rows><row><c n="ColName">value</c>…</row></rows>, so the "keys present in the data" are the
    // distinct <c> @n attribute values. Returns null (include all columns) on empty/unparseable input.
    internal static HashSet<string> GetXmlDataKeys(string tableData)
    {
        if (string.IsNullOrWhiteSpace(tableData)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(tableData);
            var names = doc.Descendants("c")
                .Select(c => (string)c.Attribute("n"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
            return names.Count == 0 ? null : names;
        }
        catch
        {
            return null; // If parsing fails, include all columns
        }
    }

    // B3: MySQL/MariaDB reject dynamic XPath outright, so there is no in-engine XML shred at the floor.
    // Instead an Xml-encoded delivery is converted to the same shape a hand-authored JSON payload would
    // have and run through the already-tuned, version-adaptive JSON row source unchanged. XDocument
    // decodes XML entities on parse and JObject/JArray serialize proper JSON escaping on the way out, so
    // a value round-trips byte-for-byte through both hops without any hand-built string concatenation.
    // A <c> element absent from a <row> stays absent from that row's JObject (never null/""), matching
    // GetJsonDataKeys' "keys present in the data" contract used for column filtering.
    internal static string XmlPayloadToJson(string tableData)
    {
        if (string.IsNullOrWhiteSpace(tableData)) return "[]";
        var doc = System.Xml.Linq.XDocument.Parse(tableData);
        var rows = new JArray();
        foreach (var row in doc.Descendants("row"))
        {
            var obj = new JObject();
            foreach (var c in row.Elements("c"))
            {
                var name = (string)c.Attribute("n");
                if (!string.IsNullOrEmpty(name))
                    obj.Add(name, c.Value);
            }
            rows.Add(obj);
        }
        return rows.ToString(Newtonsoft.Json.Formatting.None);
    }

    // B4b: the inverse of XmlPayloadToJson, used by DataTongs extraction on PostgreSQL/MySQL/MariaDB to
    // produce the same delivery XML shape GetTableDataXmlSqlServer emits natively, so a package extracted
    // on any engine deploys through the same SQL Server shred. A JSON null property is dropped rather than
    // emitted as an empty <c> — the reference producer omits NULL columns entirely (absent <c> = NULL). A
    // JSON boolean is written as "0"/"1" rather than .NET's "True"/"False": the shred casts a <c> element's
    // text straight to the target SQL type via XQuery .value(), and a literal "true"/"false" string fails a
    // BIT cast there (see ParseTableXmlIntoTempTables.sql's identical problem with schema-definition
    // booleans), while "0"/"1" is exactly what GetTableDataXmlSqlServer itself emits for a bit column.
    // XElement/XDocument handle XML-metacharacter escaping — the mirror of JObject's JSON escaping above.
    // Public (unlike XmlPayloadToJson): DataTongs, in a separate project, calls this at extraction time —
    // the same cross-project visibility GetKeyColumns/BuildMergeScript already use below.
    public static string JsonPayloadToXml(string tableData)
    {
        if (string.IsNullOrWhiteSpace(tableData) || tableData == "null") return "";
        var array = JArray.Parse(tableData);
        if (array.Count == 0) return "";

        var rows = new System.Xml.Linq.XElement("rows");
        foreach (var rowToken in array)
        {
            var row = new System.Xml.Linq.XElement("row");
            foreach (var prop in ((JObject)rowToken).Properties())
            {
                if (prop.Value.Type == JTokenType.Null) continue;
                var text = prop.Value.Type == JTokenType.Boolean
                    ? (prop.Value.Value<bool>() ? "1" : "0")
                    : prop.Value.ToString();
                row.Add(new System.Xml.Linq.XElement("c", new System.Xml.Linq.XAttribute("n", prop.Name), text));
            }
            rows.Add(row);
        }
        return rows.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    // B1: the SQL Server data-delivery metadata helpers below aggregate column lists with
    // STRING_AGG … WITHIN GROUP. On a target that cannot run it, that parse-errors during the merge
    // BUILD — so each helper detects the cliff once and falls back to reading the component rows and
    // joining them in C# (works everywhere), leaving the modern STRING_AGG path bit-for-bit unchanged.
    // Self-contained (no signature threading) because GetKeyColumns is invoked outside BuildMergeScript.
    //
    // TWO independent conditions, and both are required — this is not one cliff but two:
    //   * compatibility level < 130 — WITHIN GROUP ordered aggregation needs 130+, and a modern binary
    //     can host a low-compat database, so the compat level alone can disqualify a new server; and
    //   * server major < 14 — STRING_AGG is a SQL Server 2017 FUNCTION. It does not exist on a 2016
    //     binary at ANY compatibility level, so a compat-only probe said "modern path" on 2016 and the
    //     merge build died with "'STRING_AGG' is not a recognized built-in function name".
    // Same 2016/2017 band CompatEncoding.JsonServerMajorFloor documents for the ingest encoding; the two
    // must agree. PARSENAME(...,4) takes the major from ProductVersion ('13.0.6404.1' -> '13') and works
    // on every supported binary, unlike ProductMajorVersion which is itself 2016+ and NULLs below it.
    /// <summary>
    /// The <c>CompatEncoding</c> override in force for this process, or null for <c>auto</c>. Exactly one of
    /// the two keys is meaningful in any run — the settings contract lets SchemaQuench declare only
    /// <c>Target:</c> and the source-side tools only <c>Source:</c> — so consulting both is unambiguous
    /// rather than a precedence question.
    /// </summary>
    private static string CompatEncodingOverride()
    {
        // Resolve, not ResolveOrCreate: this is library code reached from paths that never register a
        // configuration (unit tests, and any caller building a script without a settings file), and
        // ResolveOrCreate tries to instantiate the interface and throws. No config means no override.
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        return config?[SettingsKeys.SourceCompatEncoding] ?? config?[SettingsKeys.CompatEncoding];
    }

    private static bool IsBelowStringAggCliffSqlServer(IDbCommand cmd)
    {
        // Honour the same override the model-ingest encoding honours. These two decisions are the SAME
        // decision -- compat < 130 OR server major < 14, as the comment above says they must be -- but only
        // one of them could be forced, so DataTongs could not be made to build a legacy merge script at all.
        // That matters most for the one tool whose OUTPUT is the product: a delivery file handed to an
        // external vendor could not be generated in the legacy shape on modern hardware.
        var forced = CompatEncodingOverride();
        if (!string.IsNullOrWhiteSpace(forced))
            return CompatEncoding.Select(forced, null, int.MaxValue) == IngestEncoding.Xml;

        cmd.Parameters.Clear();
        cmd.CommandText =
            "SELECT CASE WHEN (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()) < 130 " +
            "        OR COALESCE(CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)), 4) AS INT), 0) < 14 " +
            "       THEN 1 ELSE 0 END";
        // An unparseable result (e.g. a mocked unit-test command) is treated as at/above the cliff — the
        // modern STRING_AGG path, the safe default.
        return int.TryParse(cmd.ExecuteScalar()?.ToString(), out var v) && v == 1;
    }

    // Reads a single-string-column result set into an ordered list — the C#-side aggregation used by the
    // below-cliff fallbacks in place of STRING_AGG.
    private static List<string> ReadStringColumn(IDbCommand cmd)
    {
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return rows;
    }

    /// <summary>
    /// Builds a SQL IN clause fragment for filtering columns by JSON data keys.
    /// Returns empty string if jsonKeys is null (include all columns).
    /// </summary>
    internal static string BuildJsonKeyFilter(HashSet<string> jsonKeys, string columnExpression)
    {
        if (jsonKeys == null || jsonKeys.Count == 0) return "";
        return $" AND LOWER({columnExpression}) IN ({string.Join(",", jsonKeys.Select(k => $"'{k.ToLowerInvariant().Replace("'", "''")}'"))})";
    }

    /// <summary>
    /// Binds named parameters on a shared introspection command. Clears any parameters from a
    /// prior reuse of <paramref name="cmd"/> first — these helper methods reassign CommandText and
    /// re-execute the same command repeatedly, so stale parameters would otherwise throw
    /// "parameter already added". Identifier values are bound as parameters (never interpolated)
    /// so single quotes and other characters in schema/table names can't break or inject into the
    /// introspection query. The <c>@name</c> style is supported by SqlClient, Npgsql, and MySqlConnector.
    /// </summary>
    private static void BindIdentifierParameters(IDbCommand cmd, params (string Name, string Value)[] parameters)
    {
        cmd.Parameters.Clear();
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.DbType = DbType.String;
            p.Value = (object)value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    #region IMergeScriptHelper dispatch methods

    public static string GetMatchColumns(Platform platform, string keyColumns) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => BuildSqlServerMatchColumns(keyColumns),
        Platform.PostgreSQL => BuildPostgreSqlMatchColumns(keyColumns),
        Platform.MySQL => BuildMySqlMatchColumns(keyColumns),
        _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
    };

    public static string GetJsonColumnDefinitions(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => GetJsonColumnDefinitionsSqlServer(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.PostgreSQL => GetJsonColumnDefinitionsPostgreSql(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.MySQL => GetJsonColumnDefinitionsMySql(cmd, schemaOrDb, tableName, jsonKeys),
        _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
    };

    public static string GetJsonSelectColumns(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => GetJsonSelectColumnsSqlServer(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.PostgreSQL => "", // PostgreSQL uses json_populate_recordset — no separate select columns
        Platform.MySQL => GetJsonSelectColumnsMySql(cmd, schemaOrDb, tableName, jsonKeys),
        _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
    };

    public static string GetInsertColumns(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => GetInsertColumnsSqlServer(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.PostgreSQL => GetInsertColumnsPostgreSql(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.MySQL => GetInsertColumnsMySql(cmd, schemaOrDb, tableName, jsonKeys),
        _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
    };

    public static string GetUpdateColumns(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => GetUpdateColumnsSqlServer(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.PostgreSQL => GetUpdateColumnsPostgreSql(cmd, schemaOrDb, tableName, jsonKeys),
        Platform.MySQL => GetUpdateColumnsMySql(cmd, schemaOrDb, tableName, jsonKeys),
        _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
    };

    public static bool NeedsIdentityInsert(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName) => platform.GetBasePlatform() switch
    {
        Platform.SqlServer => NeedsIdentityInsertSqlServer(cmd, schemaOrDb, tableName),
        _ => false
    };

    public static string GetIdentitySequence(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName) => platform.GetBasePlatform() switch
    {
        Platform.PostgreSQL => GetIdentityColumnAndSequencePostgreSql(cmd, schemaOrDb, tableName),
        _ => null
    };

    public static (string Disable, string Enable) GetRuleStatements(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, bool updateDescendents = false) => platform.GetBasePlatform() switch
    {
        Platform.PostgreSQL => GetRuleDisableEnableStatements(cmd, schemaOrDb, tableName, updateDescendents),
        _ => (null, null)
    };

    /// <summary>
    /// Returns structured column metadata for Pro's deferred merge assembly.
    /// Excludes computed columns and unsupported types. Includes JSON parse type
    /// information so Pro can build type-safe NULL casts.
    /// </summary>
    public static List<MergeColumnInfo> GetColumnMetadata(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys = null)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => GetColumnMetadataSqlServer(cmd, schemaOrDb, tableName, jsonKeys),
            Platform.PostgreSQL => GetColumnMetadataPostgreSql(cmd, schemaOrDb, tableName, jsonKeys),
            Platform.MySQL => GetColumnMetadataMySql(cmd, schemaOrDb, tableName, jsonKeys),
            _ => new List<MergeColumnInfo>()
        };
    }

    private static List<MergeColumnInfo> GetColumnMetadataSqlServer(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys)
    {
        var schema = schemaOrDb.Trim().Trim('[', ']');
        var table = tableName.Trim().Trim('[', ']');

        BindIdentifierParameters(cmd, ("@schema", schema), ("@table", table));
        cmd.CommandText = $@"
SELECT c.COLUMN_NAME, c.DATA_TYPE,
       CASE WHEN SCHEMA_NAME(st.[schema_id]) IN ('sys', 'dbo')
            THEN '' ELSE SCHEMA_NAME(st.[schema_id]) + '.' END + st.[name] AS USER_TYPE,
       c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.DATETIME_PRECISION,
       c.IS_NULLABLE, CAST(CASE WHEN ident.[Name] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsIdentity,
       CAST(CASE WHEN cc.[name] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsComputed,
       CAST(CASE WHEN c.DATA_TYPE IN ('geography','geometry') THEN 1 ELSE 0 END AS BIT) AS IsGeometry,
       CAST(CASE WHEN c.DATA_TYPE IN ('binary','varbinary','image') THEN 1 ELSE 0 END AS BIT) AS IsBinary,
       CAST(CASE WHEN c.DATA_TYPE = 'xml' THEN 1 ELSE 0 END AS BIT) AS IsXml
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME) AND sc.[name] = c.COLUMN_NAME
  JOIN (SELECT CASE WHEN SCHEMA_NAME(st2.[schema_id]) IN ('sys', 'dbo')
                    THEN '' ELSE SCHEMA_NAME(st2.[schema_id]) + '.' END + st2.[name] AS [name], st2.user_type_id, st2.[schema_id]
          FROM sys.types st2 WITH (NOLOCK)) st ON st.user_type_id = sc.user_type_id
  LEFT JOIN sys.identity_columns ident WITH (NOLOCK) ON ident.[Name] = c.COLUMN_NAME
                                                    AND ident.[object_id] = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME)
  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[name] = c.COLUMN_NAME
                                                 AND cc.[object_id] = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME)
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND cc.[name] IS NULL
    AND sc.is_rowguidcol = 0
    {SqlServerUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
  ORDER BY c.COLUMN_NAME
";
        var results = new List<MergeColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var userType = reader.GetString(2).ToUpperInvariant();
            var maxLen = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
            var precision = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            var scale = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
            var dtPrecision = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6));

            // Build JSON parse type with proper size/precision qualifiers. This is a cast target for
            // shredding delivered data (OPENJSON WITH / .value()), not a faithful DDL rendering, so it
            // deliberately does NOT share the catalog-derived DataType function C1-0a introduced for the
            // engine scripts' drift comparison and extraction — those two jobs have different correctness
            // requirements (faithful DDL text vs. "a type the shred can safely cast to").
            //
            // There is no TIME case below (unlike DATETIME2) — that gap looks like C1-0a's defect but is
            // not reachable the same way: this is a cast target, and an unqualified TIME/DATETIME2 casts
            // at precision 7 (the max), so the destination column governs final rounding regardless.
            // The DATETIME2 precision suffix just above is defensive, not load-bearing: forcing it to
            // `(dtPrecision * 0)` and rerunning the full suite left every test green, including the
            // delivery round-trip tests below — proof, not just the documented-defaults argument, that
            // nothing downstream depends on this value being correct. Do not "fix" the missing TIME case.
            var parseType = userType
                .Replace("HIERARCHYID", "NVARCHAR(4000)")
                .Replace("GEOGRAPHY", "NVARCHAR(4000)")
                .Replace("GEOMETRY", "NVARCHAR(4000)")
                .Replace("DATETIMEOFFSET", "NVARCHAR(50)")
                .Replace("NTEXT", "NVARCHAR(MAX)")
                .Replace("TEXT", "VARCHAR(MAX)")
                .Replace("IMAGE", "VARBINARY(MAX)");

            if (userType.Contains("CHAR") || userType.Contains("BINARY"))
                parseType += $"({(maxLen == -1 ? "MAX" : maxLen.ToString())})";
            else if (userType is "NUMERIC" or "DECIMAL")
                parseType += $"({precision}, {scale})";
            else if (userType == "DATETIME2")
                parseType += $"({dtPrecision})";

            results.Add(new MergeColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                JsonParseType = parseType,
                MaxLength = maxLen,
                Precision = precision,
                Scale = scale,
                IsNullable = reader.GetString(7) == "YES",
                IsIdentity = (bool)reader.GetValue(8),
                IsComputed = (bool)reader.GetValue(9),
                IsGeometry = (bool)reader.GetValue(10),
                IsBinary = (bool)reader.GetValue(11),
                IsXml = (bool)reader.GetValue(12)
            });
        }
        return results;
    }

    private static List<MergeColumnInfo> GetColumnMetadataPostgreSql(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys)
    {
        var schema = schemaOrDb.Trim().Trim('"');
        var table = tableName.Trim().Trim('"');

        BindIdentifierParameters(cmd, ("@schema", schema), ("@table", table));
        cmd.CommandText = $@"
SELECT c.column_name, c.data_type, c.udt_name, c.udt_schema,
       c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision,
       c.is_nullable,
       CASE WHEN a.attidentity != '' OR a.attgenerated != '' THEN true ELSE false END AS is_identity,
       CASE WHEN a.attgenerated != '' THEN true ELSE false END AS is_computed
  FROM information_schema.columns c
  JOIN pg_class cls ON cls.relname = c.table_name
  JOIN pg_namespace ns ON ns.oid = cls.relnamespace AND ns.nspname = c.table_schema
  JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND NOT a.attisdropped AND a.attgenerated = ''
  WHERE c.table_schema = @schema AND c.table_name = @table{BuildJsonKeyFilter(jsonKeys, "c.column_name")}
  ORDER BY c.column_name
";
        var results = new List<MergeColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var udtName = reader.GetString(2);
            var udtSchema = reader.GetString(3);
            var maxLen = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            var precision = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
            var scale = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6));
            var dtPrecision = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7));

            var fullType = udtSchema is not "pg_catalog" and not "information_schema"
                ? $"{udtSchema}.{udtName}" : udtName;

            // Same cast-target-not-DDL reasoning as GetColumnMetadataSqlServer above applies here — this
            // stays separate from C1-0a's catalog-derived function by design. Unlike the SQL Server twin,
            // this branch already covers time/timestamp precision (no gap to record).
            //
            // maxLen > 0 ? "(n)" : "" (no "MAX" keyword, unlike SQL Server's -1-sentinel case above) is
            // correct PostgreSQL syntax, not a missing case: an unbounded varchar/text has no length
            // parameter at all in PostgreSQL — there is no "(MAX)"-equivalent token to emit. maxLen is 0
            // here only when the column is genuinely unbounded (a bare "char" reports length 1, not 0).
            var parseType = fullType;
            var dataType = reader.GetString(1);
            if (dataType.Contains("char", StringComparison.OrdinalIgnoreCase) || dataType.Contains("binary", StringComparison.OrdinalIgnoreCase))
                parseType += maxLen > 0 ? $"({maxLen})" : "";
            else if (dataType is "numeric" or "decimal")
                parseType += $"({precision}, {scale})";
            else if (dataType.StartsWith("timestamp") || dataType.StartsWith("time"))
                parseType += dtPrecision > 0 ? $"({dtPrecision})" : "";

            results.Add(new MergeColumnInfo
            {
                Name = reader.GetString(0),
                DataType = udtName,
                JsonParseType = parseType,
                MaxLength = maxLen,
                Precision = precision,
                Scale = scale,
                IsNullable = reader.GetString(8) == "YES",
                IsIdentity = (bool)reader.GetValue(9),
                IsComputed = (bool)reader.GetValue(10),
                IsGeometry = IsGeometryTypePostgreSql(udtName),
                IsBinary = IsByteaTypePostgreSql(udtName),
                IsXml = IsXmlTypePostgreSql(udtName)
            });
        }
        return results;
    }

    private static List<MergeColumnInfo> GetColumnMetadataMySql(IDbCommand cmd, string schemaOrDb, string tableName, HashSet<string> jsonKeys)
    {
        var db = schemaOrDb.Trim().Trim('`');
        var table = tableName.Trim().Trim('`');

        BindIdentifierParameters(cmd, ("@db", db), ("@table", table));
        cmd.CommandText = $@"
SELECT c.COLUMN_NAME, c.DATA_TYPE, c.COLUMN_TYPE,
       c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.DATETIME_PRECISION,
       c.IS_NULLABLE,
       CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END AS IsIdentity,
       CASE WHEN c.GENERATION_EXPRESSION != '' THEN 1 ELSE 0 END AS IsComputed,
       c.CHARACTER_SET_NAME
  FROM INFORMATION_SCHEMA.COLUMNS c
  WHERE BINARY c.TABLE_SCHEMA = BINARY @db
    AND BINARY c.TABLE_NAME = BINARY @table
    AND (c.GENERATION_EXPRESSION IS NULL OR c.GENERATION_EXPRESSION = ''){BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
  ORDER BY c.ORDINAL_POSITION
";
        var results = new List<MergeColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dataType = reader.GetString(1).ToLowerInvariant();
            var maxLen = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3));
            var precision = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            var scale = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));

            // MySQL JSON_TABLE COLUMNS clause requires real column types — not CAST aliases
            // like SIGNED/UNSIGNED. Integer types must be spelled out (INT, BIGINT, etc.).
            // Character/text types mirror the non-deferred path (GetMySqlTypeForJsonTable): CHAR maxes
            // at 255 and utf8mb4 VARCHAR at 16383. The old `CHAR(maxLen)` default emitted e.g. CHAR(65535)
            // for a TEXT column, which MySQL tolerated in JSON_TABLE but MariaDB rejects with
            // "Column length too big ... (max = 255); use BLOB or TEXT instead". Read oversized/text as TEXT.
            var jsonType = dataType switch
            {
                "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" => dataType.ToUpperInvariant(),
                "float" or "double" or "real" => "DOUBLE",
                "decimal" or "numeric" => $"DECIMAL({precision},{scale})",
                "date" => "DATE",
                "datetime" or "timestamp" => "DATETIME",
                "time" => "TIME",
                "year" => "YEAR",
                "json" => "JSON",
                "tinytext" or "text" or "mediumtext" or "longtext"
                    or "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => "TEXT",
                "char" => maxLen is > 0 and <= 255 ? $"CHAR({maxLen})" : "TEXT",
                "varchar" => maxLen is > 0 and <= 16383 ? $"VARCHAR({maxLen})" : "TEXT",
                _ => maxLen is > 0 and <= 255 ? $"CHAR({maxLen})" : "TEXT"
            };

            var isGeometry = dataType is "point" or "linestring" or "polygon" or "geometry"
                or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection";
            var isBinary = dataType is "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob";

            results.Add(new MergeColumnInfo
            {
                Name = reader.GetString(0),
                DataType = dataType,
                JsonParseType = jsonType,
                MaxLength = (int)Math.Min(maxLen, int.MaxValue),
                Precision = precision,
                Scale = scale,
                IsNullable = reader.GetString(7) == "YES",
                IsIdentity = Convert.ToInt32(reader.GetValue(8)) == 1,
                IsComputed = Convert.ToInt32(reader.GetValue(9)) == 1,
                IsGeometry = isGeometry,
                IsBinary = isBinary,
                IsXml = false
            });
        }
        return results;
    }

    #endregion

    #region GetKeyColumns

    /// <summary>
    /// Gets the key columns for a table using platform-specific SQL.
    /// </summary>
    public static string GetKeyColumns(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => GetKeyColumnsSqlServer(cmd, schemaOrDb, tableName),
            Platform.PostgreSQL => GetKeyColumnsPostgreSql(cmd, schemaOrDb, tableName),
            Platform.MySQL => GetKeyColumnsMySql(cmd, schemaOrDb, tableName),
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };
    }

    private static string GetKeyColumnsSqlServer(IDbCommand cmd, string tableSchema, string tableName)
    {
        tableSchema = tableSchema.Trim().Trim('[', ']');
        tableName = tableName.Trim().Trim('[', ']');

        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@objname", $"{tableSchema}.{tableName}"));
        const string aggExpr = "CASE WHEN sc.is_nullable = 1 THEN '*' ELSE '' END + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']'";
        const string fromWhere = @"
  FROM sys.indexes si WITH (NOLOCK)
  JOIN sys.index_columns ic WITH (NOLOCK) ON ic.[object_id] = si.[object_id]
                                         AND ic.index_id = si.index_id
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = ic.[object_id]
                                   AND sc.column_id = ic.column_id
  WHERE si.[object_id] = OBJECT_ID(@objname)
    AND si.index_id = (SELECT TOP 1 si2.index_id
                         FROM sys.indexes si2 WITH (NOLOCK)
                         WHERE si2.[object_id] = si.[object_id]
                           AND si2.is_unique = 1
                         ORDER BY CASE WHEN is_primary_key = 1 THEN 0 ELSE 1 END,
                                  (SELECT COUNT(*)
                                    FROM sys.index_columns ic2 WITH (NOLOCK)
                                    JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = ic2.[object_id] AND sc.column_id = ic2.column_id
                                    WHERE ic2.[object_id] = si2.[object_id]
                                      AND sc.is_nullable = 0))
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{fromWhere}  ORDER BY ic.key_ordinal";
            return string.Join(",", ReadStringColumn(cmd));
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, ','){fromWhere}";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string GetKeyColumnsPostgreSql(IDbCommand cmd, string tableSchema, string tableName)
    {
        tableSchema = tableSchema.Trim().Trim('"');
        tableName = tableName.Trim().Trim('"');

        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG(CASE WHEN c.is_nullable = 'YES' THEN '*' ELSE '' END || '""' || a.attname || '""', ',' ORDER BY ik.ord )
  FROM pg_index idx
  JOIN pg_class tbl ON tbl.oid = idx.indrelid
  JOIN pg_namespace ns ON ns.oid = tbl.relnamespace
  JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS ik(attnum, ord) ON TRUE
  JOIN pg_attribute a ON a.attrelid = tbl.oid
                     AND a.attnum = ik.attnum
  JOIN information_schema.columns c ON c.table_schema = ns.nspname
                                   AND c.table_name = tbl.relname
                                   AND c.column_name = a.attname
  WHERE ns.nspname = @schema
    AND tbl.relname = @table
    AND idx.indexrelid = (SELECT i2.indexrelid
                            FROM pg_index i2
                            WHERE i2.indrelid = tbl.oid
                              AND i2.indisunique = true
                            ORDER BY CASE WHEN i2.indisprimary THEN 0 ELSE 1 END,
                                     (SELECT COUNT(*)
                                        FROM UNNEST(i2.indkey) AS k(attnum)
                                        JOIN pg_attribute pa ON pa.attrelid = tbl.oid AND pa.attnum = k.attnum
                                        WHERE pa.attnotnull = true)
                            LIMIT 1);
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string GetKeyColumnsMySql(IDbCommand cmd, string databaseName, string tableName)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');

        BindIdentifierParameters(cmd, ("@db", databaseName), ("@table", tableName));
        cmd.CommandText = $@"
SELECT GROUP_CONCAT(CONCAT('`', kcu.COLUMN_NAME, '`') ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',')
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
  AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
  AND tc.TABLE_NAME = kcu.TABLE_NAME
WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
  AND BINARY tc.TABLE_SCHEMA = BINARY @db
  AND BINARY tc.TABLE_NAME = BINARY @table;
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    #endregion

    #region BuildMergeScript

    /// <summary>
    /// Builds a platform-aware merge script for data delivery.
    /// </summary>
    /// <param name="platform">Target database platform.</param>
    /// <param name="cmd">Database command for metadata queries.</param>
    /// <param name="schemaOrDb">Schema name (SQL Server/PostgreSQL) or database name (MySQL). Used for
    /// INFORMATION_SCHEMA / catalog probes regardless of <paramref name="destSchemaOverride"/>.</param>
    /// <param name="tableName">Target table name.</param>
    /// <param name="tableData">JSON array of row data.</param>
    /// <param name="keyColumns">Comma-separated key columns for matching.</param>
    /// <param name="mergeUpdate">Whether to update matching rows.</param>
    /// <param name="mergeDelete">Whether to delete rows not in source.</param>
    /// <param name="disableTriggers">Whether to disable triggers during merge.</param>
    /// <param name="tokenizeScripts">Whether to use token placeholders instead of inline data.</param>
    /// <param name="mergeFilter">Optional filter for delete clause.</param>
    /// <param name="disableRules">PostgreSQL only: whether to disable rules during merge.</param>
    /// <param name="updateDescendents">PostgreSQL only: whether to update descendant tables (omits ONLY keyword).</param>
    /// <param name="destSchemaOverride">When non-empty, used in place of <paramref name="schemaOrDb"/> for
    /// destination-side refs in the emitted script body (MERGE INTO / ALTER TABLE / IDENTITY_INSERT / SETVAL)
    /// AND for the content-file token (which becomes unqualified — just <c>{{tableName.tabledata}}</c>) when
    /// <paramref name="contentFileToken"/> is not supplied. Source-side catalog probes still use
    /// <paramref name="schemaOrDb"/>. Exists for DataTongs schema-template extraction (design §8) where the
    /// caller passes <c>{{SchemaName}}</c> so the engine resolves the destination schema at quench time.
    /// MySQL ignores the override (no schema-inside-database concept).</param>
    /// <param name="contentFileToken">The exact <c>{{key}}</c> the content-file placeholder should embed
    /// (e.g. <c>Sales.Widget.tabledata</c>). DataTongs computes this once — identical, by construction, to
    /// the .tabledata filename stem it writes — and passes it here so the token can never drift from the
    /// filename. When omitted (all callers other than DataTongs, none of which tokenize), the legacy
    /// per-platform derivation below is used instead.</param>
    public static string BuildMergeScript(Platform platform, IDbCommand cmd,
        string schemaOrDb, string tableName, string tableData, string keyColumns,
        bool mergeUpdate, bool mergeDelete, bool disableTriggers,
        bool tokenizeScripts, string mergeFilter,
        bool disableRules = false, bool updateDescendents = false,
        string destSchemaOverride = null, int pgServerVersionNum = 0, int mySqlServerVersionNum = 0,
        string contentEncoding = "Json", string contentFileToken = null)
    {
        var isXml = string.Equals(contentEncoding, "Xml", StringComparison.OrdinalIgnoreCase);

        // B1/B3: XML delivery shreds the payload with .nodes()/.value() on SQL Server (every compat level)
        // and xmltable() on PostgreSQL (every version at/above the floor — no version gate needed). MySQL
        // and MariaDB reject dynamic XPath outright, so there is no in-engine shred there; instead the
        // payload is converted to JSON once, up front, and run through the unchanged, already
        // version-adaptive JSON row source exactly as a hand-authored JSON payload would be. Tokenized
        // scripts replace the payload with a placeholder at runtime, so there is nothing real to convert.
        var mySqlIsXml = isXml && platform.GetBasePlatform() == Platform.MySQL && !tokenizeScripts;
        var mySqlTableData = mySqlIsXml ? XmlPayloadToJson(tableData) : tableData;

        // Extract the data keys to filter columns — only include columns present in the data. The MySQL
        // XML route filters from the converted JSON so the keys match what is actually being shredded,
        // not the original XML shape. For tokenized scripts, data is replaced at runtime so we include
        // all columns.
        var dataKeys = tokenizeScripts ? null
            : mySqlIsXml ? GetJsonDataKeys(mySqlTableData)
            : isXml ? GetXmlDataKeys(tableData) : GetJsonDataKeys(tableData);

        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => BuildMergeScriptSqlServer(cmd, schemaOrDb, tableName, tableData, keyColumns,
                mergeUpdate, mergeDelete, disableTriggers, tokenizeScripts, mergeFilter, dataKeys, destSchemaOverride, isXml, contentFileToken),
            Platform.PostgreSQL => BuildMergeScriptPostgreSql(cmd, schemaOrDb, tableName, updateDescendents, tableData, keyColumns,
                mergeUpdate, mergeDelete, disableTriggers, disableRules, tokenizeScripts, mergeFilter, dataKeys, destSchemaOverride, pgServerVersionNum, isXml, contentFileToken),
            Platform.MySQL => BuildMergeScriptMySql(cmd, schemaOrDb, tableName, mySqlTableData, keyColumns,
                mergeUpdate, mergeDelete, tokenizeScripts, mergeFilter, dataKeys, mySqlServerVersionNum, contentFileToken),
            _ => throw new ArgumentException($"Unsupported platform: {platform}", nameof(platform))
        };
    }

    #endregion

    #region Content-file token derivation (#390 — single source for filename stem and token key)

    /// <summary>
    /// The one derivation of a table's encoded, optionally schema-qualified display name — the
    /// content-file filename stem, and (suffixed with ".tabledata" via <see cref="BuildContentFileToken"/>)
    /// the ScriptTokens/content-file token key embedded in a tokenized merge script. Unqualified when
    /// <paramref name="forceUnqualified"/> is set (DataTongs schema-template mode, or a per-engine
    /// builder's destSchemaOverride) or <paramref name="schema"/> is empty; otherwise
    /// "{encoded schema}.{encoded tableName}". DataTongs and every per-engine BuildMergeScript fallback
    /// call this — do not re-derive either quantity anywhere else.
    /// </summary>
    public static string EncodeTableDisplayName(string schema, string tableName, bool forceUnqualified)
    {
        return forceUnqualified || string.IsNullOrEmpty(schema)
            ? FileNameEncoder.Encode(tableName)
            : $"{FileNameEncoder.Encode(schema)}.{FileNameEncoder.Encode(tableName)}";
    }

    /// <summary>
    /// The content-file token key: <see cref="EncodeTableDisplayName"/> suffixed with ".tabledata" —
    /// the same string DataTongs uses for the .tabledata filename stem, by construction.
    /// </summary>
    public static string BuildContentFileToken(string schema, string tableName, bool forceUnqualified)
        => $"{EncodeTableDisplayName(schema, tableName, forceUnqualified)}.tabledata";

    #endregion

    #region Unsupported Column Comments

    /// <summary>
    /// Queries for columns with unsupported data types and returns SQL comment lines for each.
    /// </summary>
    internal static string GetUnsupportedColumnComments(Platform platform, IDbCommand cmd, string schemaOrDb, string tableName)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => GetUnsupportedColumnCommentsSqlServer(cmd, schemaOrDb, tableName),
            Platform.PostgreSQL => GetUnsupportedColumnCommentsPostgreSql(cmd, schemaOrDb, tableName),
            _ => ""
        };
    }

    private static string GetUnsupportedColumnCommentsSqlServer(IDbCommand cmd, string tableSchema, string tableName)
    {
        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        const string aggExpr = "'-- Column [' + c.COLUMN_NAME + '] skipped: ' + c.DATA_TYPE + ' is not supported for data delivery'";
        const string filters = @"
  FROM INFORMATION_SCHEMA.COLUMNS c
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND c.DATA_TYPE IN ('sql_variant', 'rowversion', 'timestamp')
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{filters}  ORDER BY c.COLUMN_NAME";
            return string.Join("\r\n", ReadStringColumn(cmd));
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, CHAR(13) + CHAR(10)){filters}";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string GetUnsupportedColumnCommentsPostgreSql(IDbCommand cmd, string tableSchema, string tableName)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG('-- Column ""' || c.column_name || '"" skipped: ' || c.udt_name || ' is not supported for data delivery', CHR(10) ORDER BY c.column_name)
  FROM information_schema.columns c
  JOIN pg_class cls ON cls.relname = c.table_name
  JOIN pg_namespace ns ON ns.oid = cls.relnamespace AND ns.nspname = c.table_schema
  JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND NOT a.attisdropped AND a.attgenerated = ''
  WHERE c.table_schema = @schema AND c.table_name = @table
    AND (c.udt_name IN ('tsvector', 'tsquery', 'money', 'box', 'circle', 'line', 'lseg', 'path')
         OR EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                    WHERE t.typname = c.udt_name AND n.nspname = c.udt_schema AND t.typtype = 'c'))
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    #endregion

    #region SQL Server Implementation

    private static string BuildMergeScriptSqlServer(IDbCommand cmd, string tableSchema, string tableName,
        string tableData, string keyColumns, bool mergeUpdate, bool mergeDelete,
        bool disableTriggers, bool tokenizeScripts, string mergeFilter, HashSet<string> jsonKeys,
        string destSchemaOverride = null, bool isXml = false, string contentFileToken = null)
    {
        tableSchema = tableSchema.Trim().Trim('[', ']');
        tableName = tableName.Trim().Trim('[', ']');

        // destSchema is the identifier used in EMITTED destination refs; tableSchema is used
        // for catalog probes against the source database. They differ only when DataTongs
        // is extracting in schema-template mode (override = "{{SchemaName}}").
        var destSchema = string.IsNullOrEmpty(destSchemaOverride)
            ? tableSchema
            : destSchemaOverride.Trim().Trim('[', ']');

        var unsupportedComments = GetUnsupportedColumnCommentsSqlServer(cmd, tableSchema, tableName);
        var matchColumns = BuildSqlServerMatchColumns(keyColumns);
        // Select-column list for the MERGE source, computed here (before identityInsert) so the catalog-query
        // call order is identical to the JSON-only path that predates the XML encoding. XML shreds with
        // .nodes()/.value(); JSON reads OPENJSON WITH columns (the WITH clause is jsonColumns, below).
        var selectColumns = isXml
            ? BuildXmlShredSelectColumnsSqlServer(cmd, tableSchema, tableName, jsonKeys)
            : GetJsonSelectColumnsSqlServer(cmd, tableSchema, tableName, jsonKeys);
        // IDENTITY_INSERT must be ON only when tabledata provides values for an identity column.
        // NeedsIdentityInsertSqlServer returns true whenever ANY identity column exists; filter by data keys.
        var identityInsert = NeedsIdentityInsertSqlServer(cmd, tableSchema, tableName)
                             && IdentityColumnInJsonKeysSqlServer(cmd, tableSchema, tableName, jsonKeys);
        var jsonColumns = isXml ? null : GetJsonColumnDefinitionsSqlServer(cmd, tableSchema, tableName, jsonKeys);

        // Content-file token: DataTongs passes the exact key it wrote alongside the .tabledata filename
        // (see MergeScriptHelper.BuildMergeScript's contentFileToken doc). The fallback below — the same
        // shared derivation DataTongs itself uses — only serves callers that never tokenize (e.g.
        // DataDeliveryProcessor), kept for backward compatibility.
        var contentToken = contentFileToken
            ?? BuildContentFileToken(tableSchema, tableName, !string.IsNullOrEmpty(destSchemaOverride));
        var contentValue = tokenizeScripts
            ? $"{{{{{contentToken}}}}}"
            : tableData?.Replace("'", "''");

        // B1: the row source is the only thing that differs by encoding. XML shreds the payload with
        // .nodes()/.value() (works at every compat level); JSON uses OPENJSON (compat 130+). The MERGE
        // tail (match/update/insert/delete/identity/triggers) below is identical for both.
        var (declareLine, rowSource) = isXml
            // XML data type methods (.nodes()/.value()) require QUOTED_IDENTIFIER ON; emit it so the script
            // is self-sufficient regardless of the executing session's setting (verified on SQL Server 2008 R2).
            ? ($"SET QUOTED_IDENTIFIER ON;\r\n\r\nDECLARE @v_xml XML = '{contentValue}';",
               $@"USING (
  SELECT {selectColumns}
    FROM @v_xml.nodes('/rows/row') AS Src(n)
) AS Source")
            : ($"DECLARE @v_json NVARCHAR(MAX) = '{contentValue}';",
               $@"USING (
  SELECT {selectColumns}
    FROM OPENJSON(@v_json)
    WITH (
{jsonColumns}
    )
) AS Source");

        var mergeSQL = (string.IsNullOrEmpty(unsupportedComments) ? "" : unsupportedComments + "\r\n") + $@"
{declareLine}

{(disableTriggers ? $"ALTER TABLE [{destSchema}].[{tableName}] DISABLE TRIGGER ALL;" : "")}
{(identityInsert ? $"SET IDENTITY_INSERT [{destSchema}].[{tableName}] ON;" : "")}
MERGE INTO [{destSchema}].[{tableName}] AS Target
{rowSource}
ON {matchColumns}
";

        if (mergeUpdate)
        {
            var updateColumns = GetUpdateColumnsSqlServer(cmd, tableSchema, tableName, jsonKeys);
            // NULL-safe inequality: fires update when exactly one side is NULL, or when both are non-NULL but differ.
            // The original "NOT (T = S OR (both NULL))" form falls into three-valued-logic NULL for the asymmetric cases.
            static string NullSafeNotEqual(string tExpr, string sExpr) =>
                $"((({tExpr}) IS NULL AND ({sExpr}) IS NOT NULL) OR (({tExpr}) IS NOT NULL AND ({sExpr}) IS NULL) OR ({tExpr}) <> ({sExpr}))";
            var updateCompare = string.Join(" OR ",
                updateColumns!.Split(',').Select(c => c.StartsWith("G[")
                    ? NullSafeNotEqual($"Target.{c.Substring(1)}.ToString()", $"Source.{c.Substring(1)}.ToString()")
                    : c.StartsWith("X[") || c.StartsWith("N[")
                        ? NullSafeNotEqual($"CAST(Target.{c.Substring(1)} AS NVARCHAR(MAX))", $"CAST(Source.{c.Substring(1)} AS NVARCHAR(MAX))")
                        : c.StartsWith("T[")
                            ? NullSafeNotEqual($"CAST(Target.{c.Substring(1)} AS VARCHAR(MAX))", $"CAST(Source.{c.Substring(1)} AS VARCHAR(MAX))")
                            : c.StartsWith("I[")
                                ? NullSafeNotEqual($"CAST(Target.{c.Substring(1)} AS VARBINARY(MAX))", $"CAST(Source.{c.Substring(1)} AS VARBINARY(MAX))")
                                : c.StartsWith("D[")
                                    ? NullSafeNotEqual($"CAST(Target.{c.Substring(1)} AS NVARCHAR(50))", $"CAST(Source.{c.Substring(1)} AS NVARCHAR(50))")
                                    : NullSafeNotEqual($"Target.{c}", $"Source.{c}")));

            mergeSQL += $"""

WHEN MATCHED AND ({updateCompare}) THEN
  UPDATE SET
{string.Join(",\r\n", updateColumns.Split(',').Select(c => $"        {c.Replace("G[", "[").Replace("X[", "[").Replace("N[", "[").Replace("T[", "[").Replace("I[", "[").Replace("D[", "[")} = Source.{c.Replace("G[", "[").Replace("X[", "[").Replace("N[", "[").Replace("T[", "[").Replace("I[", "[").Replace("D[", "[")}"))}
""";
        }

        var insertColumns = GetInsertColumnsSqlServer(cmd, tableSchema, tableName, jsonKeys);
        mergeSQL += $@"

 WHEN NOT MATCHED BY TARGET THEN
   INSERT (
 {insertColumns}
   ) VALUES (
 {insertColumns!.Replace("[", "Source.[")}
   )
 ";

        if (mergeDelete)
        {
            mergeSQL += $@"

 WHEN NOT MATCHED BY SOURCE{(string.IsNullOrWhiteSpace(mergeFilter) ? "" : $" AND ({mergeFilter})")} THEN
   DELETE
 ";
        }

        mergeSQL += $";\r\n{(identityInsert ? $"SET IDENTITY_INSERT [{destSchema}].[{tableName}] OFF;\r\n" : "")}{(disableTriggers ? $"ALTER TABLE [{destSchema}].[{tableName}] ENABLE TRIGGER ALL;\r\n" : "")}";
        return mergeSQL;
    }

    private static string BuildSqlServerMatchColumns(string keyColumns)
    {
        return string.Join(" AND ", keyColumns.Split(',')
            .Select(c => c.Trim().StartsWith("*")
                ? $"(Source.[{c.Trim().Substring(1).Trim(']', '[')}] = Target.[{c.Trim().Substring(1).Trim(']', '[')}] OR (Source.[{c.Trim().Substring(1).Trim(']', '[')}] IS NULL AND Target.[{c.Trim().Substring(1).Trim(']', '[')}] IS NULL))"
                : $"Source.[{c.Trim().Trim(']', '[')}] = Target.[{c.Trim().Trim(']', '[')}]"));
    }

    private static string GetJsonSelectColumnsSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        const string aggExpr = @"CASE WHEN c.DATA_TYPE IN ('GEOGRAPHY', 'GEOMETRY')
                       THEN CASE WHEN c.DATA_TYPE = 'GEOGRAPHY' THEN 'geography' ELSE 'geometry' END + '::STGeomFromText([' + c.COLUMN_NAME + '], [' + c.COLUMN_NAME + '.STSrid]) AS [' + c.COLUMN_NAME + ']'
                       ELSE '[' + c.COLUMN_NAME + ']' END";
        var filters = $@"
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME) AND sc.[name] = C.COLUMN_NAME
  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[name] = c.COLUMN_NAME
                                                 AND cc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND cc.[name] IS NULL
    AND sc.is_rowguidcol = 0
    {SqlServerUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{filters}  ORDER BY c.COLUMN_NAME";
            var rows = ReadStringColumn(cmd);
            return rows.Count == 0 ? null : string.Join(",", rows);
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, ',') WITHIN GROUP (ORDER BY c.COLUMN_NAME){filters}";
        return cmd.ExecuteScalar()?.ToString();
    }

    // B1: builds the XML-shred SELECT column list for a SQL Server data-delivery MERGE source. Assembled
    // in C# from GetColumnMetadata (whose column set matches GetJsonSelectColumns exactly — same
    // computed/rowguid/unsupported-type exclusions and data-key filter), so the emitted Source exposes
    // exactly the columns the MERGE's insert/update/match clauses reference. The delivery XML shape is
    // <rows><row><c n="ColName">value</c>…</row></rows>; each column is shredded with
    // Src.n.value('(c[@n="ColName"]/text())[1]', <type>). NULL = absent <c>.
    private static string BuildXmlShredSelectColumnsSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        var columns = GetColumnMetadataSqlServer(cmd, tableSchema, tableName, jsonKeys);
        if (columns.Count == 0) return "*";

        static string Node(string name) => $"(c[@n=\"{name}\"]/text())[1]";

        return string.Join(",", columns.Select(c =>
        {
            var alias = $"[{c.Name}]";
            if (c.IsGeometry)
            {
                var spatialType = c.DataType.Equals("geography", StringComparison.OrdinalIgnoreCase) ? "geography" : "geometry";
                // WKT + SRID companion (<c n="Col.STSrid">), mirroring the JSON path's [Col]/[Col.STSrid] pair.
                return $"{spatialType}::STGeomFromText(Src.n.value('{Node(c.Name)}','NVARCHAR(4000)'), Src.n.value('{Node(c.Name + ".STSrid")}','INT')) AS {alias}";
            }
            if (c.IsBinary)
                // Delivery stores binary as base64 text; xs:base64Binary decodes it in-shred (a straight
                // CAST to VARBINARY would hex-interpret the text, corrupting the bytes).
                return $"Src.n.value('xs:base64Binary({Node(c.Name)})','{c.JsonParseType}') AS {alias}";
            if (c.IsXml)
                // .value() cannot return the xml type; shred as NVARCHAR(MAX) and let the INSERT implicitly
                // convert the text into the xml column.
                return $"Src.n.value('{Node(c.Name)}','NVARCHAR(MAX)') AS {alias}";
            return $"Src.n.value('{Node(c.Name)}','{c.JsonParseType}') AS {alias}";
        }));
    }

    private static bool NeedsIdentityInsertSqlServer(IDbCommand cmd, string tableSchema, string tableName)
    {
        BindIdentifierParameters(cmd, ("@objname", $"{tableSchema}.{tableName}"));
        cmd.CommandText = $@"
SELECT CAST(CASE WHEN EXISTS (SELECT * FROM sys.identity_columns WITH (NOLOCK) WHERE [object_id] = OBJECT_ID(@objname))
                 THEN 1 ELSE 0 END AS BIT)
";
        return cmd.ExecuteScalar() as bool? ?? false;
    }

    private static bool IdentityColumnInJsonKeysSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        if (jsonKeys == null || jsonKeys.Count == 0) return false;
        var names = string.Join(",", jsonKeys.Select(k => $"'{k.Replace("'", "''")}'"));
        BindIdentifierParameters(cmd, ("@objname", $"{tableSchema}.{tableName}"));
        cmd.CommandText = $@"
SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.identity_columns c WITH (NOLOCK)
                              WHERE c.[object_id] = OBJECT_ID(@objname)
                                AND c.[name] IN ({names}))
                 THEN 1 ELSE 0 END AS BIT)
";
        return cmd.ExecuteScalar() as bool? ?? false;
    }

    // Builds the OPENJSON WITH clause's column types — the T-SQL twin of GetColumnMetadataSqlServer's
    // parseType above, evaluated server-side (STRING_AGG) instead of client-side because this feeds the
    // JSON row source directly rather than a per-column MergeColumnInfo list. Same reasoning applies: a
    // cast target for shredding, not a DDL rendering, so it stays separate from C1-0a's catalog-derived
    // DataType function; and the missing TIME precision case (only DATETIME2 is handled here too) is not
    // reachable for the same mechanism proven at the C# twin above (zeroing its DATETIME2 precision left
    // the full suite green) — this copy's DATETIME2 case was not itself mutated, but it is a cast target
    // read by the same OPENJSON/INSERT machinery, so the same "destination column governs" argument holds.
    private static string GetJsonColumnDefinitionsSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        const string aggExpr = @"'           [' + c.COLUMN_NAME + '] ' +
                  REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(USER_TYPE), 'HIERARCHYID', 'NVARCHAR(4000)'), 'GEOGRAPHY', 'NVARCHAR(4000)'), 'GEOMETRY', 'NVARCHAR(4000)'), 'DATETIMEOFFSET', 'NVARCHAR(50)'), 'NTEXT', 'NVARCHAR(MAX)'), 'TEXT', 'VARCHAR(MAX)'), 'IMAGE', 'VARBINARY(MAX)') +
                      CASE WHEN USER_TYPE LIKE '%CHAR' OR USER_TYPE LIKE '%BINARY'
                           THEN '(' + CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX' ELSE CONVERT(NVARCHAR(20), CHARACTER_MAXIMUM_LENGTH) END + ')'
                                           WHEN USER_TYPE IN ('NUMERIC', 'DECIMAL')
                                           THEN '(' + CONVERT(NVARCHAR(20), NUMERIC_PRECISION) + ', ' + CONVERT(NVARCHAR(20), NUMERIC_SCALE) + ')'
                                           WHEN USER_TYPE = 'DATETIME2'
                                           THEN '(' + CONVERT(NVARCHAR(20), DATETIME_PRECISION) + ')'
                                           WHEN USER_TYPE = 'XML' AND sc.xml_collection_id <> 0
                                           THEN '([' + SCHEMA_NAME(xc.[schema_id]) + '].[' + xc.[name] + '])'
                                           ELSE '' END +
                      CASE WHEN USER_TYPE IN ('GEOGRAPHY', 'GEOMETRY') THEN ', [' + c.COLUMN_NAME + '.STSrid] INT' ELSE '' END";
        var filters = $@"
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME) AND sc.[name] = C.COLUMN_NAME
  JOIN (SELECT CASE WHEN SCHEMA_NAME(st.[schema_id]) IN ('sys', 'dbo')
                    THEN '' ELSE SCHEMA_NAME(st.[schema_id]) + '.' END + st.[name] AS USER_TYPE, st.user_type_id
          FROM sys.types st WITH (NOLOCK)) st ON st.user_type_id = sc.user_type_id
  LEFT JOIN sys.xml_schema_collections xc WITH (NOLOCK) ON xc.xml_collection_id = sc.xml_collection_id
  LEFT JOIN sys.identity_columns ident WITH (NOLOCK) ON ident.[Name] = COLUMN_NAME
                                                    AND ident.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[name] = c.COLUMN_NAME
                                                 AND cc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND cc.[name] IS NULL
    {SqlServerUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{filters}  ORDER BY c.COLUMN_NAME";
            var rows = ReadStringColumn(cmd);
            return rows.Count == 0 ? null : string.Join(",\r\n", rows);
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, ',' + CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY c.COLUMN_NAME){filters}";
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string GetInsertColumnsSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        var aggExpr = "'        [' + c.COLUMN_NAME + ']'";
        var filters = $@"
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME) AND sc.[name] = C.COLUMN_NAME
  LEFT JOIN sys.identity_columns ident WITH (NOLOCK) ON ident.[Name] = COLUMN_NAME
                                                    AND ident.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[name] = c.COLUMN_NAME
                                                 AND cc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND cc.[name] IS NULL
    AND sc.is_rowguidcol = 0
    {SqlServerUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{filters}  ORDER BY c.COLUMN_NAME";
            var rows = ReadStringColumn(cmd);
            return rows.Count == 0 ? null : string.Join(",\r\n", rows);
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, ',' + CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY c.COLUMN_NAME){filters}";
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string GetUpdateColumnsSqlServer(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys)
    {
        var belowCliff = IsBelowStringAggCliffSqlServer(cmd);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        const string aggExpr = @"CASE WHEN c.DATA_TYPE IN ('GEOGRAPHY', 'GEOMETRY') THEN 'G'
                       WHEN c.DATA_TYPE = 'DATETIMEOFFSET' THEN 'D'
                       WHEN c.DATA_TYPE = 'XML' THEN 'X'
                       WHEN c.DATA_TYPE = 'NTEXT' THEN 'N'
                       WHEN c.DATA_TYPE = 'TEXT' THEN 'T'
                       WHEN c.DATA_TYPE = 'IMAGE' THEN 'I'
                       ELSE '' END +
                  '[' + c.COLUMN_NAME + ']'";
        var filters = $@"
  FROM INFORMATION_SCHEMA.COLUMNS c
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME) AND sc.[name] = C.COLUMN_NAME
  LEFT JOIN sys.identity_columns ident WITH (NOLOCK) ON ident.[Name] = COLUMN_NAME
                                                    AND ident.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  LEFT JOIN sys.computed_columns cc WITH (NOLOCK) ON cc.[name] = c.COLUMN_NAME
                                                 AND cc.[object_id] = OBJECT_ID(C.TABLE_SCHEMA + '.' + C.TABLE_NAME)
  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
    AND ident.[Name] IS NULL
    AND cc.[name] IS NULL
    AND sc.is_rowguidcol = 0
    {SqlServerUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.COLUMN_NAME")}
";
        if (belowCliff)
        {
            cmd.CommandText = $"SELECT {aggExpr}{filters}  ORDER BY c.COLUMN_NAME";
            var rows = ReadStringColumn(cmd);
            return rows.Count == 0 ? null : string.Join(",", rows);
        }
        cmd.CommandText = $"SELECT STRING_AGG({aggExpr}, ',') WITHIN GROUP (ORDER BY c.COLUMN_NAME){filters}";
        return cmd.ExecuteScalar()?.ToString();
    }

    #endregion

    #region PostgreSQL Implementation

    private static string BuildMergeScriptPostgreSql(IDbCommand cmd, string tableSchema, string tableName,
        bool updateDescendents, string tableData, string keyColumns, bool mergeUpdate, bool mergeDelete,
        bool disableTriggers, bool disableRules, bool tokenizeScripts, string mergeFilter, HashSet<string> jsonKeys,
        string destSchemaOverride = null, int pgServerVersionNum = 0, bool isXml = false, string contentFileToken = null)
    {
        tableSchema = tableSchema.Trim().Trim('"');
        tableName = tableName.Trim().Trim('"');

        // destSchema is the identifier used in EMITTED destination refs; tableSchema is used
        // for catalog probes against the source database. They differ only when DataTongs is
        // extracting in schema-template mode (override = "{{SchemaName}}").
        var destSchema = string.IsNullOrEmpty(destSchemaOverride)
            ? tableSchema
            : destSchemaOverride.Trim().Trim('"');

        var unsupportedComments = GetUnsupportedColumnCommentsPostgreSql(cmd, tableSchema, tableName);
        var matchColumns = BuildPostgreSqlMatchColumns(keyColumns);
        var identAndSeq = GetIdentityColumnAndSequencePostgreSql(cmd, tableSchema, tableName);
        // B1: the row source is the only thing that differs by encoding — xmltable() is PostgreSQL's
        // structural twin of JSON_ARRAY_ELEMENTS + jsonColumns, addressed by the same <c n="Col"> shape the
        // SQL Server XML shred reads. Column set comes from GetColumnMetadataPostgreSql (shared with the
        // deferred-merge path) rather than the STRING_AGG-based jsonColumns query. geometry/bytea/array
        // columns are extracted as text and cast in the wrapping SELECT (BuildXmlColumnExpressionsPostgreSql)
        // via the same classification/expressions GetJsonColumnDefinitionsPostgreSql uses, so the two row
        // sources cannot drift.
        var jsonColumns = isXml ? null : GetJsonColumnDefinitionsPostgreSql(cmd, tableSchema, tableName, jsonKeys);
        var xmlShredColumns = isXml ? GetColumnMetadataPostgreSql(cmd, tableSchema, tableName, jsonKeys) : null;
        var xmlColumnList = isXml ? BuildXmlColumnsPostgreSql(xmlShredColumns) : null;
        var xmlColumnExprs = isXml ? BuildXmlColumnExpressionsPostgreSql(xmlShredColumns) : null;

        // PostgreSQL does not support DISABLE/ENABLE RULE ALL — each rule must be named individually.
        // Rules cannot be disabled inside PL/pgSQL blocks, so these statements go outside the DO $$ block.
        // Rule names are looked up in source catalog (pg_rules with tableSchema); the emitted ALTER
        // TABLE statements use destSchema so the rules disable/enable on the destination at quench.
        var (disableRuleStatements, enableRuleStatements) = disableRules
            ? GetRuleDisableEnableStatements(cmd, tableSchema, tableName, updateDescendents, destSchema)
            : ("", "");

        // Content-file token: see SQL Server method — DataTongs passes the exact key it wrote, so
        // the fallback derivation below (the same shared derivation) only serves non-tokenizing callers.
        var contentToken = contentFileToken
            ?? BuildContentFileToken(tableSchema, tableName, !string.IsNullOrEmpty(destSchemaOverride));
        var jsonValue = tokenizeScripts
            ? $"{{{{{contentToken}}}}}"
            : tableData?.Replace("'", "''");

        var modernDelete = pgServerVersionNum == 0 || pgServerVersionNum >= 17;
        // PostgreSQL MERGE is a v15 feature; below 15 the upsert is emitted as INSERT ... ON CONFLICT
        // DO UPDATE instead. The delete-on-absence below is a standalone MERGE-free DELETE that already
        // covers every pre-17 server, so it serves the pre-15 path unchanged.
        var legacyUpsert = pgServerVersionNum != 0 && pgServerVersionNum < 15;

        string mergeSQL;
        if (legacyUpsert)
        {
            mergeSQL = BuildPostgreSqlLegacyUpsert(cmd, tableSchema, tableName, destSchema, updateDescendents,
                mergeUpdate, disableTriggers, jsonValue, jsonColumns, keyColumns, identAndSeq,
                unsupportedComments, disableRuleStatements, enableRuleStatements, jsonKeys, isXml, xmlColumnList, xmlColumnExprs);
        }
        else
        {
        var jsonDeclareLine = isXml ? "" : $"  v_json JSON = '{jsonValue}';\n";
        var rowSource = isXml
            ? $@"SELECT {xmlColumnExprs} FROM xmltable('/rows/row' PASSING XMLPARSE(DOCUMENT '{jsonValue}')
             COLUMNS {xmlColumnList}) AS ""x"""
            : $@"WITH my_tables(arr) AS (VALUES(v_json::JSON))
    SELECT {jsonColumns}
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem";

        mergeSQL = (string.IsNullOrEmpty(unsupportedComments) ? "" : unsupportedComments + "\n")
            + (string.IsNullOrEmpty(disableRuleStatements) ? "" : disableRuleStatements + "\n") + $@"
DO $$
DECLARE
{jsonDeclareLine}  nextval BIGINT;
BEGIN
{(disableTriggers ? $"ALTER TABLE {(updateDescendents ? "" : "ONLY ")}\"{destSchema}\".\"{tableName}\" DISABLE TRIGGER ALL;" : "")}
MERGE INTO {(updateDescendents ? "" : "ONLY ")}""{destSchema}"".""{tableName}"" AS ""Target""
USING (
    {rowSource}
) AS ""Source""
ON {matchColumns}
";

        if (mergeUpdate)
        {
            var updateColumns = GetUpdateColumnsPostgreSql(cmd, tableSchema, tableName, jsonKeys);
            var xmlColumns = GetXmlColumnsPostgreSql(cmd, tableSchema, tableName);
            var jsonCols = GetJsonColumnsPostgreSql(cmd, tableSchema, tableName);
            // IS DISTINCT FROM is NULL-safe — treats (NULL, X) and (X, NULL) as different without falling into three-valued-logic NULL results
            var updateCompare = string.Join(" OR ",
                updateColumns!.Split(',').Select(c =>
                {
                    var colName = c.Trim().Trim('"');
                    if (xmlColumns.Contains(colName))
                        return $"\"Target\".{c}::text IS DISTINCT FROM \"Source\".{c}::text";
                    if (jsonCols.TryGetValue(colName, out var jsonType))
                    {
                        var cast = jsonType == "jsonb" ? "::jsonb" : "::text";
                        return $"\"Target\".{c}{cast} IS DISTINCT FROM \"Source\".{c}{cast}";
                    }
                    return $"\"Target\".{c} IS DISTINCT FROM \"Source\".{c}";
                }));

            mergeSQL += $"""

WHEN MATCHED AND ({updateCompare}) THEN
  UPDATE SET
{string.Join(",\r\n", updateColumns.Split(',').Select(c => $"        {c} = \"Source\".{c}"))}
""";
        }

        var insertColumns = GetInsertColumnsPostgreSql(cmd, tableSchema, tableName, jsonKeys);
        mergeSQL += $@"

 WHEN NOT MATCHED {(mergeDelete && modernDelete ? "BY TARGET " : "")}THEN -- 'BY TARGET' requires PostgreSQL v17+; add it only when the v17 delete-on-absence clause is also emitted
   INSERT (
 {insertColumns}
   ) {(!string.IsNullOrEmpty(identAndSeq) ? $"OVERRIDING {identAndSeq.Split("=")[2]} VALUE" : "")}
  VALUES (
 {string.Join(",\n", insertColumns!.Split(',').Select(c => $"        \"Source\".{c.Trim()}"))}
   )
 ";

        if (mergeDelete && modernDelete)
        {
            mergeSQL += $@"

 WHEN NOT MATCHED BY SOURCE{(string.IsNullOrWhiteSpace(mergeFilter) ? "" : $" AND ({mergeFilter})")} THEN -- Requires PostgreSQL v17+
   DELETE
 ";
        }

        mergeSQL += $@";
{(disableTriggers ? $"ALTER TABLE {(updateDescendents ? "" : "ONLY ")}\"{destSchema}\".\"{tableName}\" ENABLE TRIGGER ALL;" : "")}
{(!string.IsNullOrEmpty(identAndSeq) ? $"SELECT SETVAL('{identAndSeq.Split("=")[1]}', (SELECT MAX(\"{identAndSeq.Split("=")[0]}\") FROM \"{destSchema}\".\"{tableName}\")) INTO nextval;" : "")}

END $$ LANGUAGE plpgsql;
" + (string.IsNullOrEmpty(enableRuleStatements) ? "" : "\n" + enableRuleStatements);
        }

        if (mergeDelete && !modernDelete)
        {
            // PG < 17 has no MERGE WHEN NOT MATCHED BY SOURCE. Emit a standalone DELETE outside the
            // DO $$ block targeting rows with no matching source row, keyed on the same columns the
            // MERGE matched on, honoring the same mergeFilter. Source is the payload reprojected
            // identically to the MERGE USING clause (JSON or XML) so key correspondence is exact.
            // Per-key correspondence mirrors BuildPostgreSqlMatchColumns' *-prefix NULL-safe
            // handling (source of truth — keep in sync). A leading '*' is a user-config NULL-safe
            // marker: strip it and emit the (l = r OR (l IS NULL AND r IS NULL)) form.
            var deleteKeyPredicate = string.Join(" AND ",
                keyColumns.Split(',').Select(k =>
                {
                    var raw = k.Trim();
                    var nullSafe = raw.StartsWith("*");
                    var col = $"\"{raw.Trim('*').Trim('"')}\"";  // normalized quoted ident, * stripped
                    var l = $"\"DeleteSource\".{col}";
                    var r = $"\"Target\".{col}";
                    return nullSafe
                        ? $"({l} = {r} OR ({l} IS NULL AND {r} IS NULL))"
                        : $"{l} = {r}";
                }));

            // No PL/pgSQL variable scope out here (this DELETE stands outside the DO $$ block), so the
            // payload is embedded as a literal in both encodings — mirrors the JSON reprojection's
            // '{jsonValue}'::JSON literal immediately below.
            var deleteSource = isXml
                ? $@"(SELECT {xmlColumnExprs} FROM xmltable('/rows/row' PASSING XMLPARSE(DOCUMENT '{jsonValue}')
             COLUMNS {xmlColumnList}) AS ""x"") AS ""DeleteSource"""
                : $@"(WITH my_tables(arr) AS (VALUES('{jsonValue}'::JSON))
           SELECT {jsonColumns}
             FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem) AS ""DeleteSource""";

            mergeSQL += $@"
DELETE FROM {(updateDescendents ? "" : "ONLY ")}""{destSchema}"".""{tableName}"" AS ""Target""
 WHERE {(string.IsNullOrWhiteSpace(mergeFilter) ? "" : $"({mergeFilter}) AND ")}NOT EXISTS (
   SELECT 1
     FROM {deleteSource}
    WHERE {deleteKeyPredicate}
 );";
        }

        return mergeSQL;
    }

    /// <summary>
    /// PostgreSQL &lt; 15 upsert: MERGE does not exist, so the insert/update half is emitted as a manual
    /// UPDATE (matched rows, changed-only) followed by an INSERT ... WHERE NOT EXISTS (unmatched rows) —
    /// the faithful MERGE equivalent. Unlike INSERT ... ON CONFLICT this uses the same NULL-safe key
    /// predicate the MERGE ON clause uses (so '*'-prefixed nullable keys work correctly) and requires no
    /// unique/exclusion constraint on the match columns. The delete-on-absence half is the caller's
    /// standalone DELETE. Mirrors the MERGE path's trigger-disable, identity OVERRIDING, sequence
    /// re-seed, changed-only compare (IS DISTINCT FROM, with xml/json text/jsonb casts), and rule
    /// disable/enable wrapping.
    /// </summary>
    private static string BuildPostgreSqlLegacyUpsert(IDbCommand cmd, string tableSchema, string tableName,
        string destSchema, bool updateDescendents, bool mergeUpdate, bool disableTriggers, string jsonValue,
        string jsonColumns, string keyColumns, string identAndSeq,
        string unsupportedComments, string disableRuleStatements, string enableRuleStatements, HashSet<string> jsonKeys,
        bool isXml = false, string xmlColumnList = null, string xmlColumnExprs = null)
    {
        var only = updateDescendents ? "" : "ONLY ";
        // NULL-safe key predicate (same as the MERGE ON clause) — handles '*'-prefixed nullable keys and
        // needs no unique/exclusion constraint. "Target" is the table, "Source" the reprojected payload.
        var matchColumns = BuildPostgreSqlMatchColumns(keyColumns);
        var source = isXml
            ? $@"(SELECT {xmlColumnExprs} FROM xmltable('/rows/row' PASSING XMLPARSE(DOCUMENT '{jsonValue}')
             COLUMNS {xmlColumnList}) AS ""x"") AS ""Source"""
            : $@"(WITH my_tables(arr) AS (VALUES(v_json::JSON))
          SELECT {jsonColumns}
            FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem) AS ""Source""";

        var updateSql = "";
        if (mergeUpdate)
        {
            var updateColumns = GetUpdateColumnsPostgreSql(cmd, tableSchema, tableName, jsonKeys);
            var xmlColumns = GetXmlColumnsPostgreSql(cmd, tableSchema, tableName);
            var jsonCols = GetJsonColumnsPostgreSql(cmd, tableSchema, tableName);
            string CompareExpr(string c)
            {
                var colName = c.Trim().Trim('"');
                if (xmlColumns.Contains(colName))
                    return $"\"Target\".{c}::text IS DISTINCT FROM \"Source\".{c}::text";
                if (jsonCols.TryGetValue(colName, out var jsonType))
                {
                    var cast = jsonType == "jsonb" ? "::jsonb" : "::text";
                    return $"\"Target\".{c}{cast} IS DISTINCT FROM \"Source\".{c}{cast}";
                }
                return $"\"Target\".{c} IS DISTINCT FROM \"Source\".{c}";
            }
            var setClause = string.Join(",\n", updateColumns!.Split(',').Select(c => $"    {c} = \"Source\".{c}"));
            var whereChanged = string.Join(" OR ", updateColumns.Split(',').Select(CompareExpr));
            updateSql = $@"
UPDATE {only}""{destSchema}"".""{tableName}"" AS ""Target""
   SET
{setClause}
  FROM {source}
 WHERE {matchColumns}
   AND ({whereChanged});";
        }

        // Computed after the update columns to keep the same catalog-probe order as the MERGE path.
        var insertColumns = GetInsertColumnsPostgreSql(cmd, tableSchema, tableName, jsonKeys);
        var selectExprs = string.Join(", ", insertColumns!.Split(',').Select(c => $"\"Source\".{c.Trim()}"));
        var overriding = string.IsNullOrEmpty(identAndSeq)
            ? ""
            : $"OVERRIDING {identAndSeq.Split("=")[2]} VALUE\n  ";

        var jsonDeclareLine = isXml ? "" : $"  v_json JSON = '{jsonValue}';\n";
        return (string.IsNullOrEmpty(unsupportedComments) ? "" : unsupportedComments + "\n")
            + (string.IsNullOrEmpty(disableRuleStatements) ? "" : disableRuleStatements + "\n") + $@"
DO $$
DECLARE
{jsonDeclareLine}  nextval BIGINT;
BEGIN
{(disableTriggers ? $"ALTER TABLE {only}\"{destSchema}\".\"{tableName}\" DISABLE TRIGGER ALL;" : "")}{updateSql}
INSERT INTO ""{destSchema}"".""{tableName}"" (
{insertColumns}
)
  {overriding}SELECT {selectExprs}
    FROM {source}
   WHERE NOT EXISTS (SELECT 1 FROM {only}""{destSchema}"".""{tableName}"" AS ""Target"" WHERE {matchColumns});
{(disableTriggers ? $"ALTER TABLE {only}\"{destSchema}\".\"{tableName}\" ENABLE TRIGGER ALL;" : "")}
{(!string.IsNullOrEmpty(identAndSeq) ? $"SELECT SETVAL('{identAndSeq.Split("=")[1]}', (SELECT MAX(\"{identAndSeq.Split("=")[0]}\") FROM \"{destSchema}\".\"{tableName}\")) INTO nextval;" : "")}
END $$ LANGUAGE plpgsql;
" + (string.IsNullOrEmpty(enableRuleStatements) ? "" : "\n" + enableRuleStatements);
    }

    private static (string DisableStatements, string EnableStatements) GetRuleDisableEnableStatements(
        IDbCommand cmd, string tableSchema, string tableName, bool updateDescendents, string destSchema = null)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT rulename FROM pg_rules
WHERE schemaname = @schema
  AND tablename = @table
  AND rulename <> '_RETURN'
ORDER BY rulename;";
        var ruleNames = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                ruleNames.Add(reader.GetString(0));

        if (ruleNames.Count == 0)
            return ("", "");

        // Catalog probe uses tableSchema (the source); emitted ALTER TABLE statements use the
        // destination schema so the rule manipulation lands on the destination table at quench.
        var emittedSchema = string.IsNullOrEmpty(destSchema) ? tableSchema : destSchema;
        var only = updateDescendents ? "" : "ONLY ";
        var disable = string.Join("\n", ruleNames.Select(r =>
            $"ALTER TABLE {only}\"{emittedSchema}\".\"{tableName}\" DISABLE RULE \"{r}\";"));
        var enable = string.Join("\n", ruleNames.Select(r =>
            $"ALTER TABLE {only}\"{emittedSchema}\".\"{tableName}\" ENABLE RULE \"{r}\";"));
        return (disable, enable);
    }

    private static string BuildPostgreSqlMatchColumns(string keyColumns)
    {
        // Mirrors the PG<17 fallback's deleteKeyPredicate (source of truth — keep in sync).
        // A leading '*' is a user-config NULL-safe marker: strip it from the emitted column and
        // emit the (l = r OR (l IS NULL AND r IS NULL)) form with both aliases properly quoted.
        return string.Join(" AND ", keyColumns.Split(',')
            .Select(k =>
            {
                var raw = k.Trim();
                var nullSafe = raw.StartsWith("*");
                var col = $"\"{raw.TrimStart('*').Trim('"')}\"";  // normalized quoted ident, * stripped
                var l = $"\"Source\".{col}";
                var r = $"\"Target\".{col}";
                return nullSafe
                    ? $"({l} = {r} OR ({l} IS NULL AND {r} IS NULL))"
                    : $"{l} = {r}";
            }));
    }

    private static string GetIdentityColumnAndSequencePostgreSql(IDbCommand cmd, string tableSchema, string tableName)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT c.column_name || '=' || COALESCE(PG_GET_SERIAL_SEQUENCE(c.table_schema || '.' || c.table_name, c.column_name),
                                        CASE WHEN LOWER(c.column_default) LIKE 'nextval(%'
                                             THEN SUBSTRING(c.column_default
                                                            FROM POSITION('''' IN c.column_default) + 1
                                                            FOR POSITION('''' IN SUBSTRING(c.column_default FROM POSITION('''' IN c.column_default) + 1)) - 1)
                                             END) || '=' || CASE WHEN c.is_identity = 'YES' THEN 'SYSTEM' ELSE 'USER' END
  FROM information_schema.columns c
  WHERE c.table_schema = @schema
    AND c.table_name = @table
    AND (LOWER(c.column_default) LIKE 'nextval(%' OR c.is_identity = 'YES')
";
        return cmd.ExecuteScalar()?.ToString();
    }

    // The PostgreSQL twin of GetJsonColumnDefinitionsSqlServer above. Unlike the SQL Server pair, this one
    // has no time/timestamp precision gap to record — it already handles it (see the `time%`/`timestamp%`
    // branch below), matching GetColumnMetadataPostgreSql's C# parseType builder.
    private static string GetJsonColumnDefinitionsPostgreSql(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys = null)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG(
    CASE WHEN c.udt_name IN ('geometry','geography','point','linestring','polygon',
                              'multipoint','multilinestring','multipolygon','geometrycollection')
         THEN 'ST_GeomFromText(elem ->> ''' || c.column_name || ''', COALESCE(NULLIF(elem ->> ''' || c.column_name || '.STSrid'', '''')::int, 0))'
         WHEN c.udt_name = 'bytea'
         THEN 'decode(elem ->> ''' || c.column_name || ''', ''base64'')'
         WHEN LEFT(c.udt_name, 1) = '_'
         THEN 'STRING_TO_ARRAY((elem ->> ''' || c.column_name || '''), ''*,*'', ''*NULL_VALUE_REPRESENTATION*'')::' ||
              CASE WHEN c.udt_schema NOT IN ('pg_catalog','information_schema')
                   THEN c.udt_schema || '.' || c.udt_name
                   ELSE c.udt_name END
         ELSE '(elem ->> ''' || c.column_name || ''')::' ||
              CASE WHEN c.udt_schema NOT IN ('pg_catalog','information_schema')
                   THEN c.udt_schema || '.' || c.udt_name
                   ELSE c.udt_name END ||
              CASE WHEN c.data_type ILIKE '%char%' OR c.data_type ILIKE '%binary%'
                   THEN CASE WHEN c.character_maximum_length IS NULL
                             THEN '' ELSE '(' || c.character_maximum_length || ')' END
                   WHEN c.data_type IN ('numeric','decimal') AND c.numeric_precision IS NOT NULL
                   THEN '(' || c.numeric_precision::text || ', ' || COALESCE(c.numeric_scale::text,'0') || ')'
                   WHEN c.data_type LIKE 'timestamp%' OR c.data_type LIKE 'time%'
                   THEN CASE WHEN c.datetime_precision IS NULL
                             THEN '' ELSE '(' || c.datetime_precision::text || ')' END
                   ELSE '' END
    END || ' AS ""' || c.column_name || '""', ',' || CHR(10) || '           ' ORDER BY c.column_name)
  FROM information_schema.columns c
  JOIN pg_class cls ON cls.relname = c.table_name
  JOIN pg_namespace ns ON ns.oid = cls.relnamespace AND ns.nspname = c.table_schema
  JOIN pg_attribute a ON a.attrelid = cls.oid
                     AND a.attname = c.column_name
                     AND NOT a.attisdropped
                     AND a.attgenerated = ''
  WHERE c.table_schema = @schema AND c.table_name = @table
    {PostgreSqlUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.column_name")}
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    // B1: xmltable()'s COLUMNS list — the PostgreSQL twin of BuildXmlShredSelectColumnsSqlServer. Each
    // column is addressed by the same <c n="Col"> shape the SQL Server XML shred reads. Geometry/bytea/
    // array columns (RequiresXmlColumnTransformPostgreSql) are extracted as text here — their real type
    // gets applied by BuildXmlColumnExpressionPostgreSql in the wrapping SELECT instead, because a base64
    // bytea value or a '*,*'-delimited array cannot be cast to its target type directly by xmltable's
    // COLUMNS typing (silent data corruption for bytea, a parse error for arrays). Everything else is
    // typed directly here with the same JsonParseType the JSON row source casts to.
    // A geometry column declares a second xmltable column for the "<col>.STSrid" companion the
    // extractors emit, so BuildXmlColumnExpressionPostgreSql can pass it to ST_GeomFromText. A package
    // written before the companion existed has no such element and shreds to NULL, which the caller
    // defaults back to 0 -- exactly the behaviour those packages already had.
    private static string BuildXmlColumnsPostgreSql(List<MergeColumnInfo> columns) =>
        string.Join(",\n         ", columns.SelectMany(c =>
            !RequiresXmlColumnTransformPostgreSql(c)
                ? new[] { $"\"{c.Name}\" {c.JsonParseType} PATH 'c[@n=\"{c.Name}\"]/text()'" }
                : c.IsGeometry
                    ? new[]
                      {
                          $"\"{c.Name}\" text PATH 'c[@n=\"{c.Name}\"]/text()'",
                          $"\"{c.Name}.STSrid\" text PATH 'c[@n=\"{c.Name}.STSrid\"]/text()'"
                      }
                    : new[] { $"\"{c.Name}\" text PATH 'c[@n=\"{c.Name}\"]/text()'" }));

    // B1: same udt_name test GetJsonColumnDefinitionsPostgreSql uses for the array case
    // (LEFT(udt_name,1)='_' — PostgreSQL's own array-of-type naming convention), ported to C# rather than
    // duplicated with a different type-name list. Geometry/bytea reuse MergeColumnInfo.IsGeometry/IsBinary,
    // which are themselves already the JSON path's C#-side mirrors (IsGeometryTypePostgreSql/
    // IsByteaTypePostgreSql). internal: shared with DeferredMergeBuilder so the 2-pass path classifies
    // columns identically rather than drifting.
    internal static bool RequiresXmlColumnTransformPostgreSql(MergeColumnInfo c) =>
        c.IsGeometry || c.IsBinary || c.DataType.StartsWith("_", StringComparison.Ordinal);

    // B1: the per-type expression GetJsonColumnDefinitionsPostgreSql applies to `elem ->> 'col'`,
    // reapplied here to xmltable()'s text output for the same three cases — ST_GeomFromText, decode(...,
    // 'base64'), and STRING_TO_ARRAY with the identical '*,*' delimiter and '*NULL_VALUE_REPRESENTATION*'
    // sentinel — so the two row sources cannot drift. A column that needs no transform is already
    // correctly typed by BuildXmlColumnsPostgreSql's direct PATH form and just passes through.
    internal static string BuildXmlColumnExpressionPostgreSql(MergeColumnInfo c, string sourceAlias)
    {
        var raw = $"\"{sourceAlias}\".\"{c.Name}\"";
        if (c.IsGeometry)
            return $"ST_GeomFromText({raw}, COALESCE(NULLIF(\"{sourceAlias}\".\"{c.Name}.STSrid\", '')::int, 0))";
        if (c.IsBinary) return $"decode({raw}, 'base64')";
        if (c.DataType.StartsWith("_", StringComparison.Ordinal))
            return $"STRING_TO_ARRAY({raw}, '*,*', '*NULL_VALUE_REPRESENTATION*')::{c.JsonParseType}";
        return raw;
    }

    // B1: the outer SELECT list wrapping xmltable() in the direct (non-deferred) MERGE path — applies
    // BuildXmlColumnExpressionPostgreSql to every column, including the pass-through case, so the FROM-item
    // always projects exactly the column set BuildXmlColumnsPostgreSql declared.
    private static string BuildXmlColumnExpressionsPostgreSql(List<MergeColumnInfo> columns) =>
        string.Join(",\n         ", columns.Select(c => $"{BuildXmlColumnExpressionPostgreSql(c, "x")} AS \"{c.Name}\""));

    private static string GetInsertColumnsPostgreSql(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys = null)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG('        ""' || c.column_name || '""', ',' || CHR(10) ORDER BY c.column_name)
  FROM information_schema.columns c
  JOIN pg_class cls ON cls.relname = c.table_name
  JOIN pg_namespace ns ON ns.oid = cls.relnamespace AND ns.nspname = c.table_schema
  JOIN pg_attribute a ON a.attrelid = cls.oid
                     AND a.attname = c.column_name
                     AND NOT a.attisdropped
                     AND a.attgenerated = ''
  WHERE c.table_schema = @schema
    AND c.table_name = @table
    {PostgreSqlUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.column_name")}
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string GetUpdateColumnsPostgreSql(IDbCommand cmd, string tableSchema, string tableName, HashSet<string> jsonKeys = null)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG('""' || c.column_name || '""', ',' ORDER BY c.column_name)
  FROM information_schema.columns c
  JOIN pg_class cls ON cls.relname = c.table_name
  JOIN pg_namespace ns ON ns.oid = cls.relnamespace AND ns.nspname = c.table_schema
  JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND NOT a.attisdropped AND a.attgenerated = ''
  WHERE c.table_schema = @schema
    AND c.table_name = @table
    AND NOT (COALESCE(c.column_default, '') LIKE 'nextval(%' OR c.is_identity = 'YES')
    {PostgreSqlUnsupportedTypeFilter}{BuildJsonKeyFilter(jsonKeys, "c.column_name")}
";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    /// <summary>
    /// Returns true if the PostgreSQL udt_name represents a PostGIS geometry/geography type.
    /// </summary>
    internal static bool IsGeometryTypePostgreSql(string udtName) => udtName.ToLowerInvariant() switch
    {
        "geometry" or "geography" or "point" or "linestring" or "polygon"
            or "multipoint" or "multilinestring" or "multipolygon" or "geometrycollection" => true,
        _ => false
    };

    /// <summary>
    /// Returns true if the PostgreSQL udt_name represents a bytea (binary) type.
    /// </summary>
    internal static bool IsByteaTypePostgreSql(string udtName) =>
        udtName.ToLowerInvariant() == "bytea";

    internal static bool IsXmlTypePostgreSql(string udtName) =>
        udtName.ToLowerInvariant() == "xml";

    private static HashSet<string> GetXmlColumnsPostgreSql(IDbCommand cmd, string tableSchema, string tableName)
    {
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT STRING_AGG(c.column_name, ',')
  FROM information_schema.columns c
  WHERE c.table_schema = @schema
    AND c.table_name = @table
    AND c.udt_name = 'xml';
";
        var result = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrEmpty(result)) return new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        return new HashSet<string>(result.Split(','), StringComparer.InvariantCultureIgnoreCase);
    }

    private static Dictionary<string, string> GetJsonColumnsPostgreSql(IDbCommand cmd, string tableSchema, string tableName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BindIdentifierParameters(cmd, ("@schema", tableSchema), ("@table", tableName));
        cmd.CommandText = $@"
SELECT c.column_name, c.udt_name
  FROM information_schema.columns c
  WHERE c.table_schema = @schema
    AND c.table_name = @table
    AND c.udt_name IN ('json', 'jsonb')";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    #endregion

    #region MySQL Implementation

    private static string BuildMySqlMatchColumns(string keyColumns)
    {
        return string.Join(" AND ", keyColumns.Split(',')
            .Select(c => c.Trim().StartsWith("*")
                ? $"(`Source`.`{c.Trim().Substring(1).Trim('`')}` = `Target`.`{c.Trim().Substring(1).Trim('`')}` OR (`Source`.`{c.Trim().Substring(1).Trim('`')}` IS NULL AND `Target`.`{c.Trim().Substring(1).Trim('`')}` IS NULL))"
                : $"`Source`.`{c.Trim().Trim('`')}` = `Target`.`{c.Trim().Trim('`')}`"));
    }

    private static string GetJsonColumnDefinitionsMySql(IDbCommand cmd, string databaseName, string tableName, HashSet<string> jsonKeys)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');
        var columns = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys: jsonKeys);
        return BuildJsonTableColumnsMySql(columns);
    }

    private static string GetJsonSelectColumnsMySql(IDbCommand cmd, string databaseName, string tableName, HashSet<string> jsonKeys)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');
        var columns = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys: jsonKeys);
        return BuildSelectExpressionsMySql(columns);
    }

    private static string GetInsertColumnsMySql(IDbCommand cmd, string databaseName, string tableName, HashSet<string> jsonKeys)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');
        var columns = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys: jsonKeys);
        return string.Join(", ", columns.Select(c => $"`{c.Name}`"));
    }

    private static string GetUpdateColumnsMySql(IDbCommand cmd, string databaseName, string tableName, HashSet<string> jsonKeys)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');
        var columns = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys: jsonKeys);
        return string.Join(",", columns.Select(c =>
            c.IsJson ? $"J[`{c.Name}`]" : $"`{c.Name}`"));
    }

    private static string BuildMergeScriptMySql(IDbCommand cmd, string databaseName, string tableName,
        string tableData, string keyColumns, bool mergeUpdate, bool mergeDelete, bool tokenizeScripts, string mergeFilter,
        HashSet<string> jsonKeys, int mySqlServerVersionNum = 0, string contentFileToken = null)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');

        // Compose from fragment methods — same pattern as SQL Server and PostgreSQL.
        // MySQL allows explicit inserts into AUTO_INCREMENT columns without any special command
        // (unlike SQL Server's IDENTITY_INSERT or PostgreSQL's OVERRIDING VALUE).
        var insertColumns = GetInsertColumnsMySql(cmd, databaseName, tableName, jsonKeys);
        var selectExpressions = GetJsonSelectColumnsMySql(cmd, databaseName, tableName, jsonKeys);

        // The JSON-array row source is version-adaptive: JSON_TABLE where the target has it (MySQL 8.0+ /
        // MariaDB 10.6+), else a recursive-CTE shred for MariaDB 10.2-10.5. MySQL below 8.0 has neither and is
        // gated out of automatic data delivery upstream (DataDeliveryProcessor) -- BuildJsonRowSourceMySql
        // throws NotSupportedException there so the gate has a single source of truth.
        var columns = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys);
        var jsonSource = BuildJsonRowSourceMySql(columns, mySqlServerVersionNum);

        if (string.IsNullOrEmpty(insertColumns))
            throw new InvalidOperationException($"No columns found for table `{databaseName}`.`{tableName}` (or all columns are auto-increment/generated).");

        // Map standardized MergeType flags to MySQL strategies:
        // Insert only (no update, no delete) -> INSERT IGNORE
        // Insert/Update (update, no delete) -> INSERT ON DUPLICATE KEY UPDATE
        // Insert/Update/Delete (update and/or delete) -> Upsert + DELETE WHERE NOT EXISTS
        //   REPLACE INTO was used previously but it deletes+reinserts every matching row,
        //   breaking ON DELETE RESTRICT foreign keys.
        var hasJsonTable = mySqlServerVersionNum == 0 ||
                           (mySqlServerVersionNum >= 1000 ? mySqlServerVersionNum >= 1006 : mySqlServerVersionNum >= 800);
        var chunked = TryChunkMySqlPayload(hasJsonTable, tokenizeScripts, tableData, out var payloadRows);

        if (mergeDelete)
        {
            var updateColumns = GetUpdateColumnsMySql(cmd, databaseName, tableName, jsonKeys);
            var stringKeys = GetStringKeyColumnsMySql(cmd, databaseName, tableName, keyColumns);
            if (chunked)
                return BuildChunkedMergeMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource,
                    mergeUpdate ? updateColumns : null, keyColumns, payloadRows, columns, stringKeys, mergeFilter);

            var upsert = mergeUpdate
                ? BuildUpsertStatementMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource, updateColumns, tableData, keyColumns, tokenizeScripts, contentFileToken)
                : BuildInsertStatementMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource, tableData, tokenizeScripts, contentFileToken);
            var delete = BuildDeleteStatementMySql(databaseName, tableName, jsonSource, keyColumns, mergeFilter, stringKeys);
            return upsert + "\n" + delete;
        }
        if (mergeUpdate)
        {
            var updateColumns = GetUpdateColumnsMySql(cmd, databaseName, tableName, jsonKeys);
            if (chunked)
                return BuildChunkedMergeMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource,
                    updateColumns, keyColumns, payloadRows, columns, null, null);
            return BuildUpsertStatementMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource, updateColumns, tableData, keyColumns, tokenizeScripts, contentFileToken);
        }
        if (chunked)
            return BuildChunkedMergeMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource,
                null, keyColumns, payloadRows, columns, null, null);
        return BuildInsertStatementMySql(databaseName, tableName, insertColumns, selectExpressions, jsonSource, tableData, tokenizeScripts, contentFileToken);
    }

    private static List<MySqlColumnInfo> GetColumnInfoMySql(IDbCommand cmd, string databaseName, string tableName, bool excludeAutoIncrement, HashSet<string> jsonKeys = null)
    {
        databaseName = databaseName.Trim().Trim('`');
        tableName = tableName.Trim().Trim('`');

        // jsonKeys and excludeAutoIncrement both change the shape of the result, so they are part of the
        // key rather than ignored -- two callers asking different questions about one table must not
        // share an answer.
        var memoKey = $"cols|{databaseName}|{tableName}|{excludeAutoIncrement}|" +
                      (jsonKeys == null ? "" : string.Join(",", jsonKeys.OrderBy(k => k, StringComparer.Ordinal)));
        return Memoized(memoKey, () => ReadColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement, jsonKeys));
    }

    private static List<MySqlColumnInfo> ReadColumnInfoMySql(IDbCommand cmd, string databaseName, string tableName, bool excludeAutoIncrement, HashSet<string> jsonKeys)
    {
        var jsonCheckColumns = GetJsonCheckConstraintColumnsMySql(cmd, databaseName, tableName);

        BindIdentifierParameters(cmd, ("@db", databaseName), ("@table", tableName));
        cmd.CommandText = $@"
SELECT
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    c.DATETIME_PRECISION,
    c.COLUMN_TYPE,
    c.EXTRA,
    c.GENERATION_EXPRESSION
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE BINARY c.TABLE_SCHEMA = BINARY @db
  AND BINARY c.TABLE_NAME = BINARY @table
ORDER BY c.ORDINAL_POSITION;
";
        var columns = new List<MySqlColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var extra = reader.GetString(reader.GetOrdinal("EXTRA"));
            var genExpr = reader.IsDBNull(reader.GetOrdinal("GENERATION_EXPRESSION"))
                ? null
                : reader.GetString(reader.GetOrdinal("GENERATION_EXPRESSION"));

            if (!string.IsNullOrWhiteSpace(genExpr)) continue;
            if (excludeAutoIncrement && extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase)) continue;

            var colName = reader.GetString(reader.GetOrdinal("COLUMN_NAME"));
            if (jsonKeys != null && !jsonKeys.Contains(colName)) continue;

            columns.Add(new MySqlColumnInfo
            {
                Name = colName,
                DataType = reader.GetString(reader.GetOrdinal("DATA_TYPE")),
                CharMaxLength = reader.IsDBNull(reader.GetOrdinal("CHARACTER_MAXIMUM_LENGTH"))
                    ? null
                    : reader.GetInt64(reader.GetOrdinal("CHARACTER_MAXIMUM_LENGTH")),
                NumericPrecision = reader.IsDBNull(reader.GetOrdinal("NUMERIC_PRECISION"))
                    ? null
                    : reader.GetInt64(reader.GetOrdinal("NUMERIC_PRECISION")),
                NumericScale = reader.IsDBNull(reader.GetOrdinal("NUMERIC_SCALE"))
                    ? null
                    : reader.GetInt64(reader.GetOrdinal("NUMERIC_SCALE")),
                DatetimePrecision = reader.IsDBNull(reader.GetOrdinal("DATETIME_PRECISION"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("DATETIME_PRECISION")),
                ColumnType = reader.GetString(reader.GetOrdinal("COLUMN_TYPE")),
                IsAutoIncrement = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                IsJson = reader.GetString(reader.GetOrdinal("DATA_TYPE")).Equals("json", StringComparison.OrdinalIgnoreCase)
                         || jsonCheckColumns.Contains(colName)
            });
        }
        return columns;
    }

    // MariaDB reports JSON columns as `longtext` in INFORMATION_SCHEMA (JSON is a LONGTEXT alias there),
    // but auto-creates a `json_valid(<col>)` CHECK constraint per JSON column. Detect those so MariaDB
    // JSON columns get the same JSON-aware merge treatment as MySQL's native `json` type. Returns empty
    // on MySQL — its native JSON columns carry no such constraint and are detected by DATA_TYPE = 'json'.
    private static HashSet<string> GetJsonCheckConstraintColumnsMySql(IDbCommand cmd, string databaseName, string tableName) =>
        Memoized($"jsoncheck|{databaseName}|{tableName}", () => ReadJsonCheckConstraintColumnsMySql(cmd, databaseName, tableName));

    private static HashSet<string> ReadJsonCheckConstraintColumnsMySql(IDbCommand cmd, string databaseName, string tableName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // INFORMATION_SCHEMA.CHECK_CONSTRAINTS does not exist on MySQL 5.7, and only MariaDB auto-creates the
        // json_valid(<col>) checks detected here (MySQL's native `json` type is detected by DATA_TYPE), so this
        // read is MariaDB-only — return empty on MySQL rather than erroring on the missing catalog view. This is
        // reached directly by DataTongs (which builds merge scripts without the DatabaseQuench delivery gate).
        // One SELECT VERSION() per call was a second round trip per table just to decide whether the
        // catalog read below applies at all. The server does not change mid-pass.
        var isMariaDb = Memoized("isMariaDb", () =>
        {
            cmd.CommandText = "SELECT VERSION()";
            return (cmd.ExecuteScalar()?.ToString() ?? string.Empty)
                .IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0 ? "y" : "n";
        });
        if (isMariaDb != "y") return result;

        BindIdentifierParameters(cmd, ("@db", databaseName), ("@table", tableName));
        cmd.CommandText = @"
SELECT cc.CHECK_CLAUSE
FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
  ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
 AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
 AND tc.CONSTRAINT_TYPE = 'CHECK'
WHERE tc.CONSTRAINT_SCHEMA = @db
  AND tc.TABLE_NAME = @table;
";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            foreach (Match match in JsonValidColumnRegex.Matches(reader.GetString(0)))
                result.Add(match.Groups[1].Value);
        }
        return result;
    }

    // Matches MariaDB's auto-generated JSON check clause `json_valid(`<col>`)`, capturing the column name.
    private static readonly Regex JsonValidColumnRegex =
        new(@"json_valid\(`([^`]+)`\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Character data types whose key comparisons carry a collation and can raise
    // "Illegal mix of collations" (1267) when the two sides differ.
    private static readonly HashSet<string> MySqlCharacterTypes = new(StringComparer.OrdinalIgnoreCase)
        { "char", "varchar", "tinytext", "text", "mediumtext", "longtext", "enum", "set" };

    // The key columns whose type is character-based (so their comparisons need an explicit collation).
    private static HashSet<string> GetStringKeyColumnsMySql(IDbCommand cmd, string databaseName, string tableName, string keyColumns)
    {
        var keyNames = new HashSet<string>(ParseKeyColumnsMySql(keyColumns), StringComparer.OrdinalIgnoreCase);
        if (keyNames.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos = GetColumnInfoMySql(cmd, databaseName, tableName, excludeAutoIncrement: false, jsonKeys: keyNames);
        return new HashSet<string>(
            infos.Where(c => MySqlCharacterTypes.Contains(c.DataType)).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    // Builds one key-match predicate. String keys force a common collation on both sides:
    // MariaDB 11.4 defaults new tables to utf8mb4_uca1400_ai_ci while a JSON_TABLE-extracted string
    // column is utf8mb4_general_ci, so `Target.k = jt.k` raises "Illegal mix of collations" (1267).
    // Forcing utf8mb4_unicode_ci on both sides (the same canonical the quench comparison path uses)
    // resolves the mix without changing which rows match. The Target column is transcoded with
    // CONVERT(... USING utf8mb4) FIRST so the collation is valid for its charset -- a non-utf8mb4 key
    // (latin1 / legacy utf8mb3, common on the older MySQL 5.7 / MariaDB 10.2 databases the floor targets)
    // would otherwise raise "COLLATION 'utf8mb4_unicode_ci' is not valid for CHARACTER SET 'latin1'" (1253);
    // same fix as CHANGELOG #359 for the forge procs. Numeric/date keys carry no collation.
    private static string BuildKeyMatchMySql(string k, HashSet<string> stringKeys, string sourceAlias = "jt") =>
        stringKeys.Contains(k)
            ? $"CONVERT(Target.`{k}` USING utf8mb4) COLLATE utf8mb4_unicode_ci = CONVERT({sourceAlias}.`{k}` USING utf8mb4) COLLATE utf8mb4_unicode_ci"
            : $"Target.`{k}` = {sourceAlias}.`{k}`";

    // ---- MariaDB 10.2-10.5 chunked shred -------------------------------------------------------
    //
    // The recursive-CTE row source reads each value with JSON_EXTRACT(@json_data, '$[i].col'). MariaDB
    // stores JSON as text, so '$[i]' rescans the document from the start: element i costs O(i) and a
    // whole table costs O(n^2). Measured on 10.2.44 with an 8-column payload: 200 rows 0.73s, 500 rows
    // 4.9s, 2000 rows 87.8s -- ten times the rows for a hundred and twenty times the work. Delivering a
    // 19,972-row table as one payload ran past thirty hours without committing.
    //
    // So the payload is sliced client-side (it is already parsed here) and the shred runs per chunk,
    // which makes the total linear in row count. Only the CTE path needs this -- JSON_TABLE parses once.
    //
    // The chunk size is small because the quadratic term still applies WITHIN a chunk, so halving the
    // chunk roughly quarters its cost. Measured per-row cost on the same server: 200 rows 11.2ms,
    // 100 rows 4.4ms, 50 rows 1.8ms -- 50 is ~6x better than 200. Below ~50 the per-statement overhead
    // starts to outweigh the shrinking scan, so this is about the useful floor.
    internal const int MariaDbShredChunkRows = 50;

    // The temp table that carries the delete's key set. The full-sync DELETE is a NOT EXISTS against
    // the payload, so it CANNOT be chunked -- run per chunk it would delete every row living in a
    // different chunk. Instead every chunk appends its keys here and one DELETE runs against the
    // completed set at the end. That also stops the delete re-evaluating the shred per target row.
    private const string MySqlDeleteKeyTable = "_ss_merge_keys";

    internal static bool TryChunkMySqlPayload(bool hasJsonTable, bool tokenizeScripts, string tableData, out JArray rows)
    {
        rows = null;
        // tokenizeScripts emits a {{table.tabledata}} placeholder resolved later, so there is no
        // payload to slice at build time; that path keeps the single-statement form.
        if (hasJsonTable || tokenizeScripts || string.IsNullOrWhiteSpace(tableData)) return false;
        try { rows = JArray.Parse(tableData); } catch { return false; }
        return rows.Count > MariaDbShredChunkRows;
    }

    private static string EscapeMySqlPayload(string json) =>
        (json ?? "[]").Replace("\\", "\\\\").Replace("'", "''");

    // Key-column DDL for the delete-key temp table. String keys are declared utf8mb4/utf8mb4_unicode_ci
    // so they meet BuildKeyMatchMySql's forced collation without a 1267 mix; other types reuse the same
    // mapping JSON_TABLE uses, so a key compares identically whichever row source produced it.
    private static string BuildDeleteKeyTableDdlMySql(List<string> keyColNames, List<MySqlColumnInfo> columns, HashSet<string> stringKeys)
    {
        var defs = keyColNames.Select(k =>
        {
            var col = columns.FirstOrDefault(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase));
            if (stringKeys.Contains(k)) return $"`{k}` VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
            var type = col != null ? GetMySqlTypeForJsonTable(col) : "LONGTEXT";
            return $"`{k}` {type}";
        });
        return $"CREATE TEMPORARY TABLE `{MySqlDeleteKeyTable}` (\n  {string.Join(",\n  ", defs)}\n);";
    }

    private static string BuildDeleteStatementMySql(string databaseName, string tableName,
        string jsonSource, string keyColumns, string mergeFilter, HashSet<string> stringKeys)
    {
        var keyColNames = ParseKeyColumnsMySql(keyColumns);
        var sb = new StringBuilder();
        sb.AppendLine($"DELETE Target FROM `{databaseName}`.`{tableName}` Target");
        sb.AppendLine("WHERE NOT EXISTS (");
        sb.AppendLine($"  SELECT 1 FROM {jsonSource}");
        sb.AppendLine($"  WHERE {string.Join(" AND ", keyColNames.Select(k => BuildKeyMatchMySql(k, stringKeys)))}");
        sb.Append(")");
        if (!string.IsNullOrWhiteSpace(mergeFilter))
            sb.Append($"\nAND ({mergeFilter})");
        sb.AppendLine(";");
        return sb.ToString();
    }

    private static string BuildUpsertStatementMySql(string databaseName, string tableName,
        string insertColumns, string selectExpressions, string jsonSource, string updateColumns,
        string tableData, string keyColumns, bool tokenizeScripts, string contentFileToken = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"INSERT INTO `{databaseName}`.`{tableName}` ({insertColumns})");
        sb.AppendLine($"SELECT {selectExpressions}");
        sb.AppendLine($"FROM {jsonSource}");

        var keyColNames = ParseKeyColumnsMySql(keyColumns);
        sb.AppendLine(BuildUpsertClauseMySql(databaseName, tableName, insertColumns, updateColumns, keyColNames) + ";");

        return BuildWithJsonVariableMySql(tableName, tableData, sb.ToString(), tokenizeScripts, contentFileToken);
    }

    // The ON DUPLICATE KEY UPDATE clause, shared by the single-statement and chunked paths so the two
    // cannot drift. Key columns are excluded (assigning them is a no-op that can confuse the optimizer);
    // when nothing but keys is updatable, a self-assignment of the first column keeps the upsert legal.
    private static string BuildUpsertClauseMySql(string databaseName, string tableName,
        string insertColumns, string updateColumns, List<string> keyColNames)
    {
        var nonKeyColumns = updateColumns.Split(',')
            .Where(c =>
            {
                var colName = c.Trim().Trim('`');
                if (colName.StartsWith("J[")) colName = colName.Substring(2).TrimEnd(']').Trim('`');
                return !keyColNames.Contains(colName, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();

        if (nonKeyColumns.Count == 0)
        {
            var firstCol = insertColumns.Split(',')[0].Trim();
            return $"ON DUPLICATE KEY UPDATE {firstCol} = VALUES({firstCol})";
        }

        var updateAssignments = nonKeyColumns.Select(c =>
        {
            var trimmed = c.Trim();
            if (trimmed.StartsWith("J["))
            {
                var colName = trimmed.Substring(2).TrimEnd(']');
                // JSON_EXTRACT(x,'$') normalizes the document for comparison and is portable across
                // MySQL and MariaDB. MariaDB rejects CAST(x AS JSON) (JSON is a LONGTEXT alias with
                // no native cast); MySQL keeps order-insensitive object equality on the extracted value.
                return $"  {colName} = IF(JSON_EXTRACT(VALUES({colName}), '$') = JSON_EXTRACT(`{databaseName}`.`{tableName}`.{colName}, '$'), `{databaseName}`.`{tableName}`.{colName}, VALUES({colName}))";
            }
            return $"  {trimmed} = VALUES({trimmed})";
        });
        return "ON DUPLICATE KEY UPDATE\n" + string.Join(",\n", updateAssignments);
    }

    private static string BuildInsertStatementMySql(string databaseName, string tableName,
        string insertColumns, string selectExpressions, string jsonSource,
        string tableData, bool tokenizeScripts, string contentFileToken = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"INSERT IGNORE INTO `{databaseName}`.`{tableName}` ({insertColumns})");
        sb.AppendLine($"SELECT {selectExpressions}");
        sb.AppendLine($"FROM {jsonSource};");

        return BuildWithJsonVariableMySql(tableName, tableData, sb.ToString(), tokenizeScripts, contentFileToken);
    }

    // Emits the MariaDB 10.2-10.5 shred as one SET + statement pair per chunk, so the quadratic
    // positional scan is bounded by the chunk instead of the whole table. When the merge deletes,
    // each chunk also appends its keys to a temp table and a single DELETE runs against the
    // completed set afterwards -- see MySqlDeleteKeyTable for why that cannot be chunked.
    // updateColumns null => INSERT IGNORE (no update); stringKeys/mergeFilter null => no delete half.
    internal static string BuildChunkedMergeMySql(string databaseName, string tableName,
        string insertColumns, string selectExpressions, string jsonSource, string updateColumns,
        string keyColumns, JArray payloadRows, List<MySqlColumnInfo> columns,
        HashSet<string> stringKeys, string mergeFilter)
    {
        var keyColNames = ParseKeyColumnsMySql(keyColumns);
        var withDelete = stringKeys != null;
        var sb = new StringBuilder();

        sb.AppendLine($"-- Delivered in {(int)Math.Ceiling(payloadRows.Count / (double)MariaDbShredChunkRows)} chunks of up to {MariaDbShredChunkRows} rows:");
        sb.AppendLine("-- this target has no JSON_TABLE, and the recursive-CTE shred is quadratic in payload size.");

        if (withDelete)
        {
            sb.AppendLine($"DROP TEMPORARY TABLE IF EXISTS `{MySqlDeleteKeyTable}`;");
            sb.AppendLine(BuildDeleteKeyTableDdlMySql(keyColNames, columns, stringKeys));
            sb.AppendLine();
        }

        for (var offset = 0; offset < payloadRows.Count; offset += MariaDbShredChunkRows)
        {
            var chunk = new JArray(payloadRows.Skip(offset).Take(MariaDbShredChunkRows));
            sb.AppendLine($"SET @json_data = '{EscapeMySqlPayload(chunk.ToString(Newtonsoft.Json.Formatting.None))}';");

            if (updateColumns == null)
                sb.AppendLine($"INSERT IGNORE INTO `{databaseName}`.`{tableName}` ({insertColumns})");
            else
                sb.AppendLine($"INSERT INTO `{databaseName}`.`{tableName}` ({insertColumns})");
            sb.AppendLine($"SELECT {selectExpressions}");
            sb.Append($"FROM {jsonSource}");

            if (updateColumns != null)
            {
                sb.AppendLine();
                sb.Append(BuildUpsertClauseMySql(databaseName, tableName, insertColumns, updateColumns, keyColNames));
            }
            sb.AppendLine(";");

            if (withDelete)
            {
                sb.AppendLine($"INSERT INTO `{MySqlDeleteKeyTable}` ({string.Join(", ", keyColNames.Select(k => $"`{k}`"))})");
                sb.AppendLine($"SELECT {string.Join(", ", keyColNames.Select(k => $"jt.`{k}`"))}");
                sb.AppendLine($"FROM {jsonSource};");
            }
            sb.AppendLine();
        }

        if (withDelete)
        {
            sb.AppendLine($"DELETE Target FROM `{databaseName}`.`{tableName}` Target");
            sb.AppendLine($"LEFT JOIN `{MySqlDeleteKeyTable}` K");
            sb.AppendLine($"  ON {string.Join(" AND ", keyColNames.Select(k => BuildKeyMatchMySql(k, stringKeys, "K")))}");
            sb.Append($"WHERE K.`{keyColNames[0]}` IS NULL");
            if (!string.IsNullOrWhiteSpace(mergeFilter))
                sb.Append($"\nAND ({mergeFilter})");
            sb.AppendLine(";");
            sb.AppendLine($"DROP TEMPORARY TABLE `{MySqlDeleteKeyTable}`;");
        }
        return sb.ToString();
    }

    private static string BuildWithJsonVariableMySql(string tableName, string tableData, string sqlStatement,
        bool tokenizeScripts, string contentFileToken = null)
    {
        var sb = new StringBuilder();
        if (tokenizeScripts)
        {
            // DataTongs passes the exact key it wrote alongside the .tabledata filename; the fallback
            // below (the same shared derivation, always unqualified — MySQL has no schema concept)
            // only serves non-DataTongs callers.
            var tokenKey = contentFileToken ?? BuildContentFileToken(null, tableName, forceUnqualified: true);
            sb.AppendLine($"SET @json_data = '{{{{{tokenKey}}}}}';");
        }
        else
        {
            // Escape backslashes first (before single quotes) to prevent MySQL string literal
            // interpretation of \n, \t, \', etc. in JSON data containing XML, file paths,
            // or other content with backslashes.
            var escapedData = (tableData ?? "[]").Replace("\\", "\\\\").Replace("'", "''");
            sb.AppendLine($"SET @json_data = '{escapedData}';");
        }
        sb.AppendLine();
        sb.Append(sqlStatement);
        return sb.ToString();
    }

    // Builds the JSON-array row source "<expr> AS jt" for the merge statements, version-adaptively.
    // JSON_TABLE exists on MySQL 8.0+ / MariaDB 10.6+; MariaDB 10.2-10.5 has no JSON_TABLE but has recursive
    // CTEs, so it shreds @json_data with an embedded recursive CTE (WITH cannot prefix INSERT/DELETE on
    // MariaDB 10.2, hence embedding inside the derived table). MySQL below 8.0 has neither and is unsupported
    // for automatic data delivery (gated upstream in DataDeliveryProcessor) -- this throws so that gate has a
    // single source of truth. versionNum is major*100+minor; 0 means "unknown/modern" (unit tests, callers
    // that don't detect) and takes the JSON_TABLE path. versionNum >= 1000 identifies MariaDB (10+/11+);
    // MySQL never reaches major 10, so the single number distinguishes the engine family.
    private static string BuildJsonRowSourceMySql(List<MySqlColumnInfo> columns, int versionNum)
    {
        var isMariaDb = versionNum >= 1000;
        var hasJsonTable = versionNum == 0 || (isMariaDb ? versionNum >= 1006 : versionNum >= 800);
        if (hasJsonTable)
            return "JSON_TABLE(\n    @json_data,\n    '$[*]' COLUMNS (\n      " +
                   BuildJsonTableColumnsMySql(columns) + "\n    )\n  ) AS jt";

        if (!isMariaDb)
            throw new NotSupportedException(
                $"Automatic data delivery requires JSON_TABLE (MySQL 8.0+); it is unavailable on MySQL {versionNum / 100}.{versionNum % 100}. " +
                "Deliver data on this target with manual data scripts.");

        // MariaDB 10.2-10.5: recursive-CTE shred (cap-free), embedded in the derived table. The outer WHERE
        // bounds the sequence to the array length so an empty payload yields zero rows (JSON_TABLE parity).
        var extractions = string.Join(",\n      ", columns.SelectMany(BuildCteExtractionsMySql));
        return "(\n" +
               "    WITH RECURSIVE _ss_seq AS (SELECT 0 i UNION ALL SELECT i + 1 FROM _ss_seq WHERE i + 1 < JSON_LENGTH(@json_data))\n" +
               "    SELECT\n      " + extractions + "\n" +
               "    FROM _ss_seq WHERE _ss_seq.i < JSON_LENGTH(@json_data)\n" +
               "  ) AS jt";
    }

    // The recursive-CTE extraction expressions for one column, matching the JSON_TABLE columns they
    // replace -- geometry yields TWO, the value and its `<col>.STSrid` companion, just as JSON_TABLE does.
    // their structure (JSON_EXTRACT); every other column is read as a null-safe scalar via
    // SchemaSmith_JsonScalarStr so an explicit JSON null becomes SQL NULL (a bare JSON_UNQUOTE would yield the
    // string 'null') -- the target column type coerces the extracted text on INSERT, matching JSON_TABLE.
    private static IEnumerable<string> BuildCteExtractionsMySql(MySqlColumnInfo col)
    {
        var pathSuffix = col.Name.Contains(' ') || col.Name.Contains('.') || col.Name.Contains('-')
            ? $"\"{col.Name}\""
            : col.Name;
        var extract = $"JSON_EXTRACT(@json_data, CONCAT('$[', _ss_seq.i, '].{pathSuffix}'))";
        yield return col.IsJson
            ? $"{extract} AS `{col.Name}`"
            : $"SchemaSmith_JsonScalarStr({extract}) AS `{col.Name}`";

        // Geometry carries a "<col>.STSrid" companion, exactly as the JSON_TABLE path declares it.
        // Without it the SELECT built by BuildSingleSelectExpressionMySql reads a column this row
        // source never produced, and delivery fails with "Unknown column '<col>.STSrid'". The key
        // always contains a dot, so the JSON path is always the quoted form.
        if (!IsGeometryTypeMySql(col.DataType)) yield break;

        var sridExtract =
            $"JSON_EXTRACT(@json_data, CONCAT('$[', _ss_seq.i, '].\"{col.Name}.STSrid\"'))";
        yield return $"SchemaSmith_JsonScalarStr({sridExtract}) AS `{col.Name}.STSrid`";
    }

    private static string BuildJsonTableColumnsMySql(List<MySqlColumnInfo> columns)
    {
        var definitions = new List<string>();
        foreach (var col in columns)
        {
            var mysqlType = GetMySqlTypeForJsonTable(col);
            // Quote JSON path key with double quotes when it contains spaces or special characters
            var jsonPath = col.Name.Contains(' ') || col.Name.Contains('.') || col.Name.Contains('-')
                ? $"$.\"{col.Name}\""
                : $"$.{col.Name}";
            definitions.Add($"`{col.Name}` {mysqlType} PATH '{jsonPath}'");
            // Geometry carries a "<col>.STSrid" companion beside its WKT; declare it so the SELECT can
            // rebuild the value in its own reference system. Absent in pre-companion packages -> NULL.
            if (IsGeometryTypeMySql(col.DataType))
                definitions.Add($"`{col.Name}.STSrid` INT PATH '$.\"{col.Name}.STSrid\"'");
        }
        return string.Join(",\n    ", definitions);
    }

    private static string GetMySqlTypeForJsonTable(MySqlColumnInfo col)
    {
        // JSON columns must be read as JSON in JSON_TABLE so nested objects/arrays survive extraction;
        // reading them as TEXT yields NULL for non-scalar values. MariaDB reports the column as longtext
        // (JSON alias) but accepts JSON as a JSON_TABLE column type, so key on IsJson, not the raw type.
        if (col.IsJson) return "JSON";

        var dataType = col.DataType.ToLowerInvariant();

        return dataType switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" => dataType.ToUpperInvariant(),
            "decimal" or "numeric" => col.NumericPrecision.HasValue && col.NumericScale.HasValue
                ? $"DECIMAL({col.NumericPrecision},{col.NumericScale})"
                : "DECIMAL(65,30)",
            "float" => "FLOAT",
            "double" => "DOUBLE",
            "bit" => col.NumericPrecision.HasValue ? $"BIT({col.NumericPrecision})" : "BIT(1)",
            "date" => "DATE",
            "datetime" => col.DatetimePrecision.HasValue && col.DatetimePrecision > 0
                ? $"DATETIME({col.DatetimePrecision})"
                : "DATETIME",
            "timestamp" => col.DatetimePrecision.HasValue && col.DatetimePrecision > 0
                ? $"TIMESTAMP({col.DatetimePrecision})"
                : "TIMESTAMP",
            "time" => col.DatetimePrecision.HasValue && col.DatetimePrecision > 0
                ? $"TIME({col.DatetimePrecision})"
                : "TIME",
            "year" => "YEAR",
            "char" => col.CharMaxLength.HasValue
                ? $"CHAR({col.CharMaxLength})"
                : "CHAR(255)",
            "varchar" => col.CharMaxLength.HasValue
                ? $"VARCHAR({col.CharMaxLength})"
                : "VARCHAR(65535)",
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob"
                => "TEXT",
            "tinytext" or "text" or "mediumtext" or "longtext" => "TEXT",
            // Read ENUM/SET as text in JSON_TABLE: MySQL accepts the raw `enum(...)`/`set(...)`
            // column type here, but MariaDB rejects it. The extracted string value coerces back
            // into the real ENUM/SET column on INSERT, so VARCHAR is behavior-identical on MySQL
            // and portable to MariaDB. CHARACTER_MAXIMUM_LENGTH holds the longest member (ENUM)
            // or the full members-with-separators length (SET).
            "enum" or "set" => col.CharMaxLength.HasValue
                ? $"VARCHAR({col.CharMaxLength})"
                : "VARCHAR(65535)",
            "json" => "JSON",
            "geometry" or "point" or "linestring" or "polygon" or "multipoint"
                or "multilinestring" or "multipolygon" or "geometrycollection" => "TEXT",
            _ => "TEXT"
        };
    }

    private static bool IsGeometryTypeMySql(string dataType) => dataType.ToLowerInvariant() switch
    {
        "geometry" or "point" or "linestring" or "polygon" or "multipoint"
            or "multilinestring" or "multipolygon" or "geometrycollection" => true,
        _ => false
    };

    private static bool IsBinaryTypeMySql(string dataType) => dataType.ToLowerInvariant() switch
    {
        "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => true,
        _ => false
    };

    private static string BuildSelectExpressionsMySql(List<MySqlColumnInfo> columns)
    {
        return string.Join(", ", columns.Select(BuildSingleSelectExpressionMySql));
    }

    private static string BuildSingleSelectExpressionMySql(MySqlColumnInfo c)
    {
        return IsGeometryTypeMySql(c.DataType)
            ? $"ST_GeomFromText(`{c.Name}`, COALESCE(`{c.Name}.STSrid`, 0))"
            : IsBinaryTypeMySql(c.DataType)
                ? $"FROM_BASE64(`{c.Name}`)"
                : $"`{c.Name}`";
    }

    private static List<string> ParseKeyColumnsMySql(string keyColumns)
    {
        if (string.IsNullOrWhiteSpace(keyColumns))
            return new List<string>();

        return keyColumns.Split(',')
            .Select(c => c.Trim().Trim('`'))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
    }

    internal class MySqlColumnInfo
    {
        public string Name { get; init; } = "";
        public string DataType { get; init; } = "";
        public long? CharMaxLength { get; init; }
        public long? NumericPrecision { get; init; }
        public long? NumericScale { get; init; }
        public int? DatetimePrecision { get; init; }
        public string ColumnType { get; init; } = "";
        public bool IsAutoIncrement { get; init; }

        // JSON on MySQL is a native DATA_TYPE ('json'); on MariaDB it is a LONGTEXT alias detected via
        // its auto-created json_valid(<col>) CHECK constraint. Either way the column gets JSON-aware
        // merge treatment (JSON_TABLE read + order-tolerant upsert comparison).
        public bool IsJson { get; init; }
    }

    #endregion
}
