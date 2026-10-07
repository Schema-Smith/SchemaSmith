// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;

namespace Schema.Utility;

public static class TokenHelper
{
    public const string QueryTag = "<*Query*>";
    public const string QueryFileTag = "<*QueryFile*>";
    public const string FileTag = "<*File*>";
    public const string BinaryFileTag = "<*BinaryFile*>";
    public const string SpecificTableTag = "<*SpecificTable*>";
    public const string SpecificMaterializedViewTag = "<*SpecificMaterializedView*>";
    public const string SpecificIndexedViewTag = "<*SpecificIndexedView*>";
    // A2: XML twins of the per-object tags — resolve to the object's ingest XML (ToIngestXmlObject)
    // instead of its JSON, so a below-cliff SQL Server can shred a single object with XQuery.
    public const string SpecificTableXmlTag = "<*SpecificTableXml*>";
    public const string SpecificMaterializedViewXmlTag = "<*SpecificMaterializedViewXml*>";
    public const string SpecificIndexedViewXmlTag = "<*SpecificIndexedViewXml*>";

    // tolerateErrors: deploy path leaves this false — an unresolvable file token throws
    // immediately, same as always. `--Validate` (PackageLoader) passes true so a missing/unreadable
    // file is returned in the errors list instead of aborting the load; the token keeps its
    // original unresolved "<*File*>..." literal, which is harmless on the validate-only path since
    // nothing downstream executes it.
    public static List<string> ResolveFileTokens(Dictionary<string, string> tokens, string basePath, Platform platform, bool tolerateErrors = false)
    {
        // PostgreSQL BYTEA needs an E-string-escaped hex literal with an explicit cast; SQL Server VARBINARY and
        // MySQL BLOB both accept 0x<hex> directly. Pattern mirrors GetDropTempTablesScript's platform branching.
        var (binaryPrefix, binarySuffix) = platform switch
        {
            Platform.PostgreSQL => (@"E'\\x", "'::bytea"),
            _ => ("0x", "")
        };

        var tokenErrors = new List<string>();
        ResolveFileTokensByTag(tokens, basePath, tokenErrors, FileTag, "", "", fileName => ProductFileWrapper.GetFromFactory().ReadAllText(fileName));
        ResolveFileTokensByTag(tokens, basePath, tokenErrors, BinaryFileTag, binaryPrefix, binarySuffix, fileName => BitConverter.ToString(ProductFileWrapper.GetFromFactory().ReadAllBytes(fileName)).Replace("-", ""));
        ResolveFileTokensByTag(tokens, basePath, tokenErrors, QueryFileTag, QueryTag, "", fileName => ProductFileWrapper.GetFromFactory().ReadAllText(fileName));
        if (tokenErrors.Count == 0 || tolerateErrors) return tokenErrors;
        throw new Exception(string.Join("\r\n", tokenErrors));
    }

    public static void ResolveSpecificTableTokens(Dictionary<string, string> tokens, IList<Table> tables, Platform platform)
    {
        var tokenErrors = new List<string>();
        foreach (var token in tokens.Where(t =>
                     t.Value.StartsWithIgnoringCase(SpecificTableTag) || t.Value.StartsWithIgnoringCase(SpecificTableXmlTag)).ToList())
        {
            var asXml = token.Value.StartsWithIgnoringCase(SpecificTableXmlTag);
            var tag = asXml ? SpecificTableXmlTag : SpecificTableTag;
            var table = token.Value.Substring(tag.Length).Trim();
            if (string.IsNullOrEmpty(table))
            {
                tokenErrors.Add($"Unable to resolve specific table token '{token.Key}'. No table name was provided");
                continue;
            }
            var foundTable = FindTable(tables, table, platform);
            if (foundTable == null)
            {
                tokenErrors.Add($"Unable to resolve specific table token '{token.Key}' missing table: '{table}'");
                continue;
            }
            try
            {
                var json = JToken.FromObject(foundTable).ToString();
                tokens[token.Key] = asXml ? ModelXmlSerializer.ToIngestXmlObject(json, "Table") : json;
            }
            catch (Exception e)
            {
                tokenErrors.Add($"Unable to resolve specific table token '{token.Key}' error:\r\n{e}");
            }
        }
        if (tokenErrors.Count == 0) return;
        throw new Exception(string.Join("\r\n", tokenErrors));
    }

    public static void ResolveSpecificMaterializedViewTokens(
        Dictionary<string, string> tokens,
        IList<PostgreSqlMaterializedView> materializedViews)
    {
        var tokenErrors = new List<string>();
        foreach (var token in tokens.Where(t =>
                     t.Value.StartsWithIgnoringCase(SpecificMaterializedViewTag) || t.Value.StartsWithIgnoringCase(SpecificMaterializedViewXmlTag)).ToList())
        {
            var asXml = token.Value.StartsWithIgnoringCase(SpecificMaterializedViewXmlTag);
            var tag = asXml ? SpecificMaterializedViewXmlTag : SpecificMaterializedViewTag;
            var viewRef = token.Value.Substring(tag.Length).Trim();
            if (string.IsNullOrEmpty(viewRef))
            {
                tokenErrors.Add($"Unable to resolve specific materialized view token '{token.Key}'. No view name was provided");
                continue;
            }
            var foundView = FindMaterializedView(materializedViews, viewRef);
            if (foundView == null)
            {
                tokenErrors.Add($"Unable to resolve specific materialized view token '{token.Key}' missing view: '{viewRef}'");
                continue;
            }
            try
            {
                var json = JToken.FromObject(foundView).ToString();
                tokens[token.Key] = asXml ? ModelXmlSerializer.ToIngestXmlObject(json, "MaterializedView") : json;
            }
            catch (Exception e)
            {
                tokenErrors.Add($"Unable to resolve specific materialized view token '{token.Key}' error:\r\n{e}");
            }
        }
        if (tokenErrors.Count == 0) return;
        throw new Exception(string.Join("\r\n", tokenErrors));
    }

    public static void ResolveSpecificIndexedViewTokens(
        Dictionary<string, string> tokens,
        IList<SqlServerIndexedView> indexedViews)
    {
        var tokenErrors = new List<string>();
        foreach (var token in tokens.Where(t =>
                     t.Value.StartsWithIgnoringCase(SpecificIndexedViewTag) || t.Value.StartsWithIgnoringCase(SpecificIndexedViewXmlTag)).ToList())
        {
            var asXml = token.Value.StartsWithIgnoringCase(SpecificIndexedViewXmlTag);
            var tag = asXml ? SpecificIndexedViewXmlTag : SpecificIndexedViewTag;
            var viewRef = token.Value.Substring(tag.Length).Trim();
            if (string.IsNullOrEmpty(viewRef))
            {
                tokenErrors.Add($"Unable to resolve specific indexed view token '{token.Key}'. No view name was provided");
                continue;
            }
            var foundView = FindIndexedView(indexedViews, viewRef);
            if (foundView == null)
            {
                tokenErrors.Add($"Unable to resolve specific indexed view token '{token.Key}' missing view: '{viewRef}'");
                continue;
            }
            try
            {
                var json = JToken.FromObject(foundView).ToString();
                tokens[token.Key] = asXml ? ModelXmlSerializer.ToIngestXmlObject(json, "IndexedView") : json;
            }
            catch (Exception e)
            {
                tokenErrors.Add($"Unable to resolve specific indexed view token '{token.Key}' error:\r\n{e}");
            }
        }
        if (tokenErrors.Count == 0) return;
        throw new Exception(string.Join("\r\n", tokenErrors));
    }

    private static SqlServerIndexedView FindIndexedView(
        IList<SqlServerIndexedView> views, string viewRef)
    {
        var parts = viewRef.Replace("[", "").Replace("]", "").Split('.');
        if (parts.Length == 2)
        {
            return views.FirstOrDefault(v =>
                v.Schema.Replace("[", "").Replace("]", "").Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                v.Name.Replace("[", "").Replace("]", "").Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        }
        return views.FirstOrDefault(v =>
            v.Schema.Replace("[", "").Replace("]", "").Equals("dbo", StringComparison.OrdinalIgnoreCase) &&
            v.Name.Replace("[", "").Replace("]", "").Equals(viewRef.Replace("[", "").Replace("]", ""), StringComparison.OrdinalIgnoreCase));
    }

    private static PostgreSqlMaterializedView FindMaterializedView(
        IList<PostgreSqlMaterializedView> views, string viewRef)
    {
        var parts = viewRef.Replace("\"", "").Split('.');
        if (parts.Length == 2)
        {
            return views.FirstOrDefault(v =>
                v.Schema.Equals(parts[0], StringComparison.OrdinalIgnoreCase) &&
                v.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        }
        return views.FirstOrDefault(v =>
            v.Schema.Equals("public", StringComparison.OrdinalIgnoreCase) &&
            v.Name.Equals(viewRef, StringComparison.OrdinalIgnoreCase));
    }

    internal static Table FindTable(IList<Table> tables, string tableRef, Platform platform)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => FindTableSqlServer(tables, tableRef),
            Platform.PostgreSQL => FindTablePostgreSQL(tables, tableRef),
            Platform.MySQL => FindTableMySQL(tables, tableRef),
            _ => FindTableSqlServer(tables, tableRef)
        };
    }

    private static string GetTableSchema(Table table)
    {
        var prop = table.GetType().GetProperty("Schema");
        return prop?.GetValue(table) as string ?? (table.Extensions as JObject)?["Schema"]?.ToString() ?? "";
    }

    private static Table FindTableSqlServer(IList<Table> tables, string tableRef)
    {
        return tables.FirstOrDefault(t => $"{GetTableSchema(t)}.{t.Name}".Replace("[", "").Replace("]", "").EqualsIgnoringCase(tableRef.Replace("[", "").Replace("]", "")))
            ?? tables.FirstOrDefault(t => $"{GetTableSchema(t)}.{t.Name}".Replace("[", "").Replace("]", "").EqualsIgnoringCase($"dbo.{tableRef}".Replace("[", "").Replace("]", "")));
    }

    private static Table FindTablePostgreSQL(IList<Table> tables, string tableRef)
    {
        return tables.FirstOrDefault(t => $"{GetTableSchema(t)}.{t.Name}".Replace("\"", "").EqualsIgnoringCase(tableRef.Replace("\"", "")))
            ?? tables.FirstOrDefault(t => $"{GetTableSchema(t)}.{t.Name}".Replace("\"", "").EqualsIgnoringCase($"public.{tableRef}".Replace("\"", "")));
    }

    private static Table FindTableMySQL(IList<Table> tables, string tableRef)
    {
        return tables.FirstOrDefault(t => $"{t.Name}".Replace("\"", "").Replace("`", "").EqualsIgnoringCase(tableRef.Replace("\"", "").Replace("`", "")));
    }

    private static void ResolveFileTokensByTag(Dictionary<string, string> tokens, string basePath, List<string> tokenErrors, string tag, string contentPrefix, string contentSuffix, Func<string, string> readFile)
    {
        var fileTokens = tokens.Where(t => t.Value.StartsWithIgnoringCase(tag)).ToList();
        if (fileTokens.Count == 0) return;

        var results = new ConcurrentDictionary<string, string>();
        var errors = new ConcurrentBag<string>();

        var queue = new TaskQueueManager<KeyValuePair<string, string>>(Environment.ProcessorCount * 2);
        foreach (var token in fileTokens)
        {
            queue.AddToQueue(token, t =>
            {
                var fileName = Path.Combine(basePath, t.Value.Substring(tag.Length).Trim());
                if (ProductFileWrapper.GetFromFactory().Exists(fileName))
                    try
                    {
                        results[t.Key] = $"{contentPrefix}{readFile.Invoke(fileName)}{contentSuffix}";
                    }
                    catch (Exception e)
                    {
                        errors.Add($"Unable to resolve file token '{t.Key}' error:\r\n{e}");
                    }
                else
                    errors.Add($"Unable to resolve file token '{t.Key}' missing file: '{fileName}'");
            });
        }
        queue.WaitForAll();

        foreach (var result in results)
            tokens[result.Key] = result.Value;
        tokenErrors.AddRange(errors);
    }

    public static Dictionary<string, string> SplitOutQueryTokens(Dictionary<string, string> tokens)
    {
        var queryTokens = new Dictionary<string, string>();
        foreach (var token in tokens.Where(t => t.Value.StartsWithIgnoringCase(QueryTag) || t.Value.StartsWithIgnoringCase(QueryFileTag)).ToList())
        {
            queryTokens.Add(token.Key, token.Value);
            tokens.Remove(token.Key);
        }
        return queryTokens;
    }

    internal static string GetDropTempTablesScript(Platform platform)
    {
        return platform.GetBasePlatform() switch
        {
            Platform.SqlServer => DropTempTablesSqlServer,
            Platform.PostgreSQL => DropTempTablesPostgreSQL,
            Platform.MySQL => DropTempTablesMySQL,
            _ => DropTempTablesSqlServer
        };
    }

    // Variable concatenation rather than STRING_AGG (2017), and a plain DROP TABLE rather than DROP TABLE IF EXISTS
    // (2016): this runs before every query token, on every supported version (SS-123). Not FOR XML PATH, whose .value()
    // fails under QUOTED_IDENTIFIER OFF. The order of the drops does not matter, and the WHERE keeps only tables that exist.
    private const string DropTempTablesSqlServer = """
DECLARE @v_sql NVARCHAR(MAX) = N''
SELECT @v_sql = @v_sql + N'DROP TABLE ' + QUOTENAME([name]) + N';' + NCHAR(13) + NCHAR(10)
  FROM tempdb..sysobjects WITH (NOLOCK)
 WHERE [name] LIKE '#[^#]%'
   AND OBJECT_ID('tempdb..' + QUOTENAME([name])) IS NOT NULL
IF @v_sql <> N'' EXEC(@v_sql)
""";

    private const string DropTempTablesPostgreSQL = """
DO $$
DECLARE
  v_sql TEXT = '';
BEGIN
  SELECT STRING_AGG('DROP TABLE IF EXISTS "' || tablename || '";', CHR(10))
    INTO v_sql
    FROM pg_catalog.pg_tables
    WHERE ('"' || schemaname || '"')::regnamespace = pg_catalog.PG_MY_TEMP_SCHEMA();
  IF v_sql IS NOT NULL THEN
    EXECUTE v_sql;
  END IF;
END $$;
""";

    // MySQL temporary tables are session-scoped and automatically dropped when the session ends.
    // Unlike SQL Server (tempdb..sysobjects) or PostgreSQL (pg_catalog.pg_tables), MySQL doesn't provide
    // a way to enumerate temporary tables. Using SELECT 1 as a no-op since temp table cleanup isn't
    // needed between query token resolutions within the same session.
    private const string DropTempTablesMySQL = "SELECT 1";

    public static void ResolveQueryTokens(Dictionary<string, string> queryTokens, List<KeyValuePair<string, string>> nonQueryTokens, System.Data.IDbCommand cmd, string basePath, Platform platform)
    {
        var dropTempTablesScript = GetDropTempTablesScript(platform);
        foreach (var token in queryTokens.ToList())
        {
            try
            {
                var query = token.Value.Substring(QueryTag.Length);

                cmd.CommandText = dropTempTablesScript;
                cmd.ExecuteNonQuery(); // Clear any existing temp tables before running the query
                cmd.CommandText = SqlScript.TokenReplace(query, nonQueryTokens);
                using var reader = cmd.ExecuteReader();
                var content = new StringBuilder();
                while (reader.Read())
                    content.AppendLine(reader[0] == DBNull.Value ? "" : reader[0].ToString());
                queryTokens[token.Key] = content.ToString().TrimEnd('\r', '\n');
            }
            catch (Exception ex)
            {
                throw new Exception($"Error resolving {token.Key}\r\n{token.Value}\r\n{ex.Message}\r\n{cmd.CommandText}");
            }
        }
    }

    /// <summary>
    /// Assembles the <c>{{SchemaName}}</c> + version-token (<c>{{ServerMajorVersion}}</c>,
    /// <c>{{CompatibilityLevel}}</c>) vocabulary shared by the gate sites that resolve tokens against a
    /// live <c>IDbCommand</c> at evaluation time — the folder gate and the data-delivery gate. (Table,
    /// column, index, and matview/indexed-view gates get the same two version tokens through a different
    /// mechanism: DatabaseQuench substitutes them directly into the already-serialized table/view JSON
    /// payload, since those gates are evaluated server-side inside the generated DDL rather than
    /// client-side against a command.) One assembly point for the command-evaluated sites removes the
    /// divergence that left the data-delivery gate resolving only <c>{{SchemaName}}</c> (N2).
    /// </summary>
    public static List<KeyValuePair<string, string>> AssembleGateTokens(
        string schemaName, List<KeyValuePair<string, string>> versionTokens)
    {
        var tokens = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(schemaName)) tokens.Add(new("SchemaName", schemaName));
        if (versionTokens != null) tokens.AddRange(versionTokens);
        return tokens;
    }

    /// <summary>
    /// Gate vocabulary for product-folder gates (<c>ProductQuench.GateProductFolders</c>), which run at
    /// product scope, before any database is selected. Only <c>{{ServerMajorVersion}}</c> is meaningful
    /// there — the server connection is already open — unlike the folder/delivery gates above,
    /// <c>{{CompatibilityLevel}}</c> is a property of a database (none is selected yet) and
    /// <c>{{SchemaName}}</c> is a template-iteration concept that doesn't exist at product scope. Both
    /// stay unresolved on purpose: an expression that references them reaches the target as literal text
    /// and fails loudly, rather than being silently rewritten into a wrong-but-plausible comparison.
    /// Delegates to <see cref="AssembleGateTokens"/> (schemaName omitted) so product-scope gates still
    /// speak through the one shared assembly point instead of a separate vocabulary (N2/B6b).
    /// </summary>
    public static List<KeyValuePair<string, string>> AssembleProductScopeGateTokens(int serverMajorVersion) =>
        AssembleGateTokens(null,
            [new KeyValuePair<string, string>("ServerMajorVersion", serverMajorVersion.ToString(CultureInfo.InvariantCulture))]);

    public static List<string> GetTokensFromString(string script)
    {
        if (string.IsNullOrEmpty(script) || !script.Contains("{{") || !script.Contains("}}")) return [];
        var e = 0;
        var tokensToReplace = new List<string>();
        while (true)
        {
            var s = script.IndexOf("{{", e, StringComparison.Ordinal);
            if (s < 0) break; // No more tokens found
            e = script.IndexOf("}}", s + 2, StringComparison.Ordinal);
            if (e < 0) break; // No closing token found
            var token = script.Substring(s + 2, e - s - 2);
            if (token.Length < 100 && !token.Contains("\n")) tokensToReplace.Add(token);
        }
        return tokensToReplace;
    }
}
