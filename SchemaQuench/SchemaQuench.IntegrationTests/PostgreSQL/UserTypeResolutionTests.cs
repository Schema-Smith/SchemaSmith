// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A column typed by an enum, domain or composite type the package declares. The comparison renders a live
/// user-defined type as the catalog spells it -- schema-qualified, and quoted for a domain -- so a declaration had to
/// match that spelling exactly or the column was altered on every deploy. And a bare name did not resolve at all
/// outside the search path: a schema-template tenant table typed by its own tenant's enum failed with 42704. A bare
/// name now resolves the way a reader expects -- the table's own schema first, then public -- and any spelling of a
/// user-defined type is folded to the catalog's.
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class UserTypeResolutionTests : BaseTableQuenchTests
{
    private sealed record Env(IDbConnection Conn, IDbCommand Cmd, string Schema) : IDisposable
    {
        public void Dispose()
        {
            Cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{Schema}"" CASCADE;";
            Cmd.ExecuteNonQuery();
            Conn.Dispose();
        }
    }

    private Env NewSchemaWithTypes()
    {
        var schema = $"utr_{Guid.NewGuid():N}"[..12];
        var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"CREATE SCHEMA ""{schema}"";
                             CREATE TYPE ""{schema}"".tier AS ENUM ('bronze', 'gold');
                             CREATE DOMAIN ""{schema}"".email AS varchar(256) CHECK (VALUE LIKE '%@%');";
        cmd.ExecuteNonQuery();
        return new Env(conn, cmd, schema);
    }

    private static string TableJson(string schema, string tierType, string emailType) => $$"""
        {
            "Schema": "{{schema}}",
            "Name": "customer",
            "Columns": [
                { "Name": "id", "DataType": "integer" },
                { "Name": "tier", "DataType": "{{tierType}}", "Default": "'bronze'" },
                { "Name": "email", "DataType": "{{emailType}}", "Nullable": true }
            ]
        }
        """;

    private static string ColumnType(IDbCommand cmd, string schema, string column)
    {
        cmd.CommandText = $@"SELECT n.nspname || '.' || t.typname FROM pg_attribute a
                               JOIN pg_type t ON t.oid = a.atttypid JOIN pg_namespace n ON n.oid = t.typnamespace
                              WHERE a.attrelid = to_regclass('""{schema}"".customer') AND a.attname = '{column}'";
        return cmd.ExecuteScalar() as string;
    }

    private static int ModifiedColumns(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT COUNT(*) FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""SessionId"" = pg_backend_pid() AND ""ObjectType"" = 'column' AND ""ActionType"" = 'modified'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string ModifiedColumnNames(IDbCommand cmd)
    {
        cmd.CommandText = @"SELECT COALESCE(STRING_AGG(""ObjectName"", ', '), '') FROM ""SchemaSmith"".""ChangeAudit""
                             WHERE ""SessionId"" = pg_backend_pid() AND ""ObjectType"" = 'column' AND ""ActionType"" = 'modified'";
        return "modified: " + cmd.ExecuteScalar();
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = @"DELETE FROM ""SchemaSmith"".""ChangeAudit"" WHERE ""SessionId"" = pg_backend_pid()";
        cmd.ExecuteNonQuery();
    }

    // Bare names -- how a schema-template tenant table naturally names its tenant's own types -- and every other
    // spelling of the same types. Each must build the column on the table's own schema's type, and leave it alone
    // on the next deploy.
    [TestCase("tier", "email")]
    [TestCase("{s}.tier", "{s}.email")]
    [TestCase("\\\"{s}\\\".\\\"tier\\\"", "\\\"{s}\\\".\\\"email\\\"")]
    public void AUserTypeNamedAnyWay_ResolvesInTheTablesSchema_AndIsNotReAltered(string tierType, string emailType)
    {
        using var env = NewSchemaWithTypes();
        var json = TableJson(env.Schema, tierType.Replace("{s}", env.Schema), emailType.Replace("{s}", env.Schema));

        RunTableQuenchProc(env.Cmd, json);

        Assert.That(ColumnType(env.Cmd, env.Schema, "tier"), Is.EqualTo($"{env.Schema}.tier"));
        Assert.That(ColumnType(env.Cmd, env.Schema, "email"), Is.EqualTo($"{env.Schema}.email"));

        ClearAudit(env.Cmd);
        RunTableQuenchProc(env.Cmd, json);
        Assert.That(ModifiedColumns(env.Cmd), Is.Zero, "an unchanged user-typed column was re-altered");
    }

    // The table's own schema wins over public, as it would for a reader who put the tenant schema first.
    [Test]
    public void ABareNameInBothTheTablesSchemaAndPublic_ResolvesToTheTablesSchema()
    {
        using var env = NewSchemaWithTypes();
        var decoy = $"utrpub_{Guid.NewGuid():N}"[..14];
        env.Cmd.CommandText = $@"CREATE TYPE public.""{decoy}"" AS ENUM ('x'); CREATE TYPE ""{env.Schema}"".""{decoy}"" AS ENUM ('bronze');";
        env.Cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(env.Cmd, TableJson(env.Schema, decoy, "email"));
            Assert.That(ColumnType(env.Cmd, env.Schema, "tier"), Is.EqualTo($"{env.Schema}.{decoy}"));
        }
        finally
        {
            env.Cmd.CommandText = $@"DROP TYPE IF EXISTS public.""{decoy}"" CASCADE;";
            env.Cmd.ExecuteNonQuery();
        }
    }

    private void Exec(Env env, string sql)
    {
        env.Cmd.CommandText = sql;
        env.Cmd.ExecuteNonQuery();
    }

    private string ColumnTypeRendered(Env env, string column)
    {
        env.Cmd.CommandText = $@"SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
                                  WHERE a.attrelid = to_regclass('""{env.Schema}"".customer') AND a.attname = '{column}'";
        return env.Cmd.ExecuteScalar() as string;
    }

    // A mixed-case type (what Prisma and EF generate) must keep its quotes: rendered bare, PostgreSQL folds it to
    // lowercase and the DDL cannot find it.
    [Test]
    public void AMixedCaseQuotedType_KeepsItsQuotes_AndIsNotReAltered()
    {
        using var env = NewSchemaWithTypes();
        Exec(env, $@"CREATE TYPE ""{env.Schema}"".""Role"" AS ENUM ('bronze', 'admin');");
        var json = TableJson(env.Schema, "\\\"Role\\\"", "email");

        RunTableQuenchProc(env.Cmd, json);
        Assert.That(ColumnType(env.Cmd, env.Schema, "tier"), Is.EqualTo($"{env.Schema}.Role"));

        ClearAudit(env.Cmd);
        RunTableQuenchProc(env.Cmd, json);
        Assert.That(ModifiedColumns(env.Cmd), Is.Zero);
    }

    // A standalone composite type qualifies; the fallback to public works for a type the table's schema lacks.
    [Test]
    public void ACompositeTypeInPublic_ResolvesFromATenantTable()
    {
        using var env = NewSchemaWithTypes();
        var composite = $"utrc_{Guid.NewGuid():N}"[..12];
        Exec(env, $@"CREATE TYPE public.""{composite}"" AS (street text, city text);");
        try
        {
            var json = TableJson(env.Schema, "tier", composite);
            RunTableQuenchProc(env.Cmd, json);
            Assert.That(ColumnType(env.Cmd, env.Schema, "email"), Is.EqualTo($"public.{composite}"));

            ClearAudit(env.Cmd);
            RunTableQuenchProc(env.Cmd, json);
            Assert.That(ModifiedColumns(env.Cmd), Is.Zero);
        }
        finally { Exec(env, $@"DROP TABLE IF EXISTS ""{env.Schema}"".customer; DROP TYPE IF EXISTS public.""{composite}"";"); }
    }

    // An existing column keeps the type it already has: a same-named type appearing later in the table's own
    // schema must not re-resolve it, or the deploy would change the column's type.
    [Test]
    public void AnExistingColumnsType_IsNotReResolvedToALaterSameNamedType()
    {
        using var env = NewSchemaWithTypes();
        var shared = $"utrs_{Guid.NewGuid():N}"[..12];
        Exec(env, $@"CREATE TYPE public.""{shared}"" AS ENUM ('bronze');
                     CREATE TABLE ""{env.Schema}"".customer (id integer NOT NULL, tier public.""{shared}"" NOT NULL DEFAULT 'bronze', email ""{env.Schema}"".email);");
        try
        {
            Exec(env, $@"CREATE TYPE ""{env.Schema}"".""{shared}"" AS ENUM ('bronze');");
            ClearAudit(env.Cmd);

            RunTableQuenchProc(env.Cmd, TableJson(env.Schema, shared, "email"));

            Assert.That(ColumnType(env.Cmd, env.Schema, "tier"), Is.EqualTo($"public.{shared}"));
            Assert.That(ModifiedColumns(env.Cmd), Is.Zero, ModifiedColumnNames(env.Cmd));
        }
        finally { Exec(env, $@"DROP TABLE IF EXISTS ""{env.Schema}"".customer; DROP TYPE IF EXISTS public.""{shared}"";"); }
    }

    // A table is also a row type (typtype 'c'), and SQL-standard spellings are not catalog names: a table in the
    // schema named like one must never capture a column declared with that spelling.
    [Test]
    public void ATableNamedLikeAnSqlTypeSpelling_DoesNotCaptureTheColumn()
    {
        using var env = NewSchemaWithTypes();
        Exec(env, $@"CREATE TABLE ""{env.Schema}"".""character"" (x int);");
        var json = $$"""
            {
                "Schema": "{{env.Schema}}",
                "Name": "customer",
                "Columns": [
                    { "Name": "id", "DataType": "integer" },
                    { "Name": "code", "DataType": "character", "Nullable": true }
                ]
            }
            """;

        RunTableQuenchProc(env.Cmd, json);

        Assert.That(ColumnTypeRendered(env, "code"), Is.EqualTo("character(1)"));
    }
}
