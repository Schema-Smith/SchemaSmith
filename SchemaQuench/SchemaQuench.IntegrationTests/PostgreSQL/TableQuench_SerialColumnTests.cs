// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// A column declared <c>serial</c> is the engine's own declaration: on a new table or a new column the engine creates
/// and owns the sequence; on an existing serial column it is the same column and a redeploy touches nothing; on an
/// existing plain integer column the sequence is created, owned and made the default (#519).
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class TableQuench_SerialColumnTests : BaseTableQuenchTests
{
    private const string Package = """
        {
            "Schema": "public",
            "Name": "__TABLE__",
            "Columns": [
                {"Name": "id", "DataType": "__KIND__", "Nullable": false},
                {"Name": "n", "DataType": "INT", "Nullable": true}
            ],
            "Indexes": [{"Name": "pk___TABLE__", "PrimaryKey": true, "Unique": true, "IndexColumns": "id"}]
        }
        """;

    private static string Json(string table, string kind) => Package.Replace("__TABLE__", table).Replace("__KIND__", kind);

    private string SerialState(System.Data.IDbCommand cmd, string table) =>
        Run(cmd, $"""
            SELECT format_type(a.atttypid, a.atttypmod) || '|' || COALESCE(pg_get_serial_sequence('public.{table}', 'id'), '-') || '|' ||
                   COALESCE(pg_get_expr(d.adbin, d.adrelid), '-') || '|' || a.attnotnull::text
              FROM pg_attribute a
              LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
             WHERE a.attrelid = 'public.{table}'::regclass AND a.attname = 'id'
            """);

    private static string Run(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    // Any ALTER of the column rewrites its pg_attribute row (a new xmin) or replaces its default (a new pg_attrdef
    // OID), so an unchanged pair is the proof that a redeploy touched nothing.
    private static string ColumnIdentity(System.Data.IDbCommand cmd, string table) =>
        Run(cmd, $"""
            SELECT a.xmin::text || '|' || COALESCE(d.oid::text, '-')
              FROM pg_attribute a
              LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
             WHERE a.attrelid = 'public.{table}'::regclass AND a.attname = 'id'
            """);

    [Test]
    public void ANewTable_WithASerialColumn_GetsAnOwnedSequence_AndRedeploysAsANoOp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            RunTableQuenchProc(cmd, Json("serial_new", "serial"));
            Assert.That(SerialState(cmd, "serial_new"), Is.EqualTo("integer|public.serial_new_id_seq|nextval('serial_new_id_seq'::regclass)|true"));
            cmd.CommandText = "INSERT INTO public.serial_new (n) VALUES (1), (2); SELECT MAX(id) FROM public.serial_new";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "the sequence feeds the column");

            var before = ColumnIdentity(cmd, "serial_new");
            RunTableQuenchProc(cmd, Json("serial_new", "serial"));
            Assert.That(ColumnIdentity(cmd, "serial_new"), Is.EqualTo(before), "a redeploy of the same declaration alters nothing");
            Assert.That(SerialState(cmd, "serial_new"), Is.EqualTo("integer|public.serial_new_id_seq|nextval('serial_new_id_seq'::regclass)|true"));
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS public.serial_new";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnExistingSerialTable_RedeployedAsSerial_IsANoOp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE public.serial_live (id SERIAL NOT NULL CONSTRAINT pk_serial_live PRIMARY KEY, n INT); INSERT INTO public.serial_live (n) VALUES (7)";
        cmd.ExecuteNonQuery();
        try
        {
            var before = ColumnIdentity(cmd, "serial_live");
            RunTableQuenchProc(cmd, Json("serial_live", "serial"));
            Assert.Multiple(() =>
            {
                Assert.That(ColumnIdentity(cmd, "serial_live"), Is.EqualTo(before), "the live serial column already is the declaration");
                Assert.That(SerialState(cmd, "serial_live"), Is.EqualTo("integer|public.serial_live_id_seq|nextval('serial_live_id_seq'::regclass)|true"));
            });
            cmd.CommandText = "INSERT INTO public.serial_live (n) VALUES (8); SELECT MAX(id) FROM public.serial_live";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "the sequence kept its position");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS public.serial_live";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnExistingIntegerColumn_DeclaredSerial_GainsAnOwnedSequence_AndKeepsItsRows()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE public.serial_conv (id INT NOT NULL CONSTRAINT pk_serial_conv PRIMARY KEY, n INT); INSERT INTO public.serial_conv (id, n) VALUES (5, 1)";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, Json("serial_conv", "serial"));
            Assert.That(SerialState(cmd, "serial_conv"), Is.EqualTo("integer|public.serial_conv_id_seq|nextval('serial_conv_id_seq'::regclass)|true"), "the sequence is created, owned by the column and made its default");
            Assert.That(Run(cmd, "SELECT n FROM public.serial_conv WHERE id = 5"), Is.EqualTo("1"), "the row is untouched");
            var before = ColumnIdentity(cmd, "serial_conv");
            RunTableQuenchProc(cmd, Json("serial_conv", "serial"));
            Assert.That(ColumnIdentity(cmd, "serial_conv"), Is.EqualTo(before), "the second deploy is a no-op");
        }
        finally
        {
            cmd.CommandText = "DROP TABLE IF EXISTS public.serial_conv";
            cmd.ExecuteNonQuery();
        }
    }
}
