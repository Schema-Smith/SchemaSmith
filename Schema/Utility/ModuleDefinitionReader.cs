// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using Schema.Domain;

namespace Schema.Utility;

/// <summary>
/// The engine's own rendering of a module, for the unchanged-module stamp. Each engine's rendering is compared only
/// with its own earlier rendering, so nothing is normalised across engines. Null when the object is not there or
/// the engine will not render it, which the caller treats as "changed".
/// </summary>
public static class ModuleDefinitionReader
{
    public static string Read(IDbCommand command, Platform platform, ModuleScriptIdentity identity)
    {
        if (identity == null) return null;
        try
        {
            return platform.GetBasePlatform() switch
            {
                Platform.SqlServer => ReadSqlServer(command, identity),
                Platform.PostgreSQL => ReadPostgreSql(command, identity),
                Platform.MySQL => ReadMySql(command, identity),
                _ => null
            };
        }
        catch (DbException)
        {
            return null;
        }
    }

    private static string Scalar(IDbCommand command, string sql)
    {
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == null || value is DBNull ? null : value.ToString();
    }

    private static string Lit(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    private static string ReadSqlServer(IDbCommand command, ModuleScriptIdentity id)
    {
        var qualified = $"[{id.Schema.Replace("]", "]]")}].[{id.Name.Replace("]", "]]")}]";
        // A trigger's enabled state is not in its definition, so it is part of the rendering.
        var state = id.Kind == ModuleKind.Trigger
            ? " + '|' + ISNULL((SELECT CONVERT(VARCHAR(1), t.is_disabled) FROM sys.triggers t WHERE t.object_id = sm.object_id), '')"
            : "";
        return Scalar(command, $"SELECT sm.definition{state} FROM sys.sql_modules sm WHERE sm.object_id = OBJECT_ID(N{Lit(qualified)})");
    }

    private static string ReadPostgreSql(IDbCommand command, ModuleScriptIdentity id)
    {
        var schema = Lit(id.Schema ?? "public");
        var name = Lit(id.Name);
        return id.Kind switch
        {
            ModuleKind.View => Scalar(command, $@"
SELECT pg_get_viewdef(c.oid, true) || '|' || COALESCE(array_to_string(c.reloptions, ','), '')
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE n.nspname = {schema} AND c.relname = {name} AND c.relkind = 'v'"),
            // Every overload of the name: the script's own signature is not parsed, and a changed overload is a change.
            ModuleKind.Function or ModuleKind.Procedure => Scalar(command, $@"
SELECT string_agg(pg_get_functiondef(p.oid), E'\n' ORDER BY p.oid)
  FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname = {schema} AND p.proname = {name}"),
            ModuleKind.Trigger => Scalar(command, $@"
SELECT string_agg(pg_get_triggerdef(t.oid) || '|' || t.tgenabled::text, E'\n' ORDER BY t.oid)
  FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE n.nspname = {schema} AND c.relname = {Lit(id.TableName ?? "")} AND t.tgname = {name} AND NOT t.tgisinternal"),
            _ => null
        };
    }

    private static string ReadMySql(IDbCommand command, ModuleScriptIdentity id)
    {
        var kind = id.Kind switch
        {
            ModuleKind.View => "VIEW",
            ModuleKind.Function => "FUNCTION",
            ModuleKind.Procedure => "PROCEDURE",
            ModuleKind.Trigger => "TRIGGER",
            _ => "EVENT"
        };
        command.CommandText = $"SHOW CREATE {kind} `{id.Name.Replace("`", "``")}`";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        // The rendering is the CREATE text plus the sql_mode it was created under; the column names differ per kind,
        // so both are found by name.
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var createIndex = names.FindIndex(n => n.StartsWith("Create ", StringComparison.OrdinalIgnoreCase) || n.Equals("SQL Original Statement", StringComparison.OrdinalIgnoreCase));
        var modeIndex = names.FindIndex(n => n.Equals("sql_mode", StringComparison.OrdinalIgnoreCase));
        if (createIndex < 0 || reader.IsDBNull(createIndex)) return null;
        return reader.GetString(createIndex) + "|" + (modeIndex >= 0 && !reader.IsDBNull(modeIndex) ? reader.GetString(modeIndex) : "");
    }
}
