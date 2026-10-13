// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Drops a fixture-owned schema and the tables and views a fixture put in it, for fixtures that share an
/// already-kindled database and isolate themselves by schema instead of building their own database.
/// <para>Order matters: foreign keys first so tables drop in any order, then views (an indexed view is
/// SCHEMABINDING, which blocks dropping the table under it), then tables, then the schema.</para>
/// </summary>
public static class TestSchemaDrop
{
    public static void Drop(IDbCommand cmd, string schema)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = $@"
DECLARE @s SYSNAME = N'{schema.Replace("'", "''")}', @sql NVARCHAR(MAX) = N'';
SELECT @sql += N'ALTER TABLE ' + QUOTENAME(@s) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
             + N' DROP CONSTRAINT ' + QUOTENAME(fk.[name]) + N';'
  FROM sys.foreign_keys fk WHERE fk.[schema_id] = SCHEMA_ID(@s);
SELECT @sql += N'DROP VIEW ' + QUOTENAME(@s) + N'.' + QUOTENAME(v.[name]) + N';'
  FROM sys.views v WHERE v.[schema_id] = SCHEMA_ID(@s);
SELECT @sql += N'DROP TABLE ' + QUOTENAME(@s) + N'.' + QUOTENAME(t.[name]) + N';'
  FROM sys.tables t WHERE t.[schema_id] = SCHEMA_ID(@s);
IF SCHEMA_ID(@s) IS NOT NULL SET @sql += N'DROP SCHEMA ' + QUOTENAME(@s) + N';';
IF @sql <> N'' EXEC sp_executesql @sql;";
        cmd.ExecuteNonQuery();
    }
}
