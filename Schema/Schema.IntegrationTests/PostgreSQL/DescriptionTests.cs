// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// <c>Description</c> on a table, column and index is <c>COMMENT ON</c>. A declared value is set, a changed one updated in
/// place, <c>""</c> removes it, and an omitted one leaves whatever the database has: a package that never mentions
/// descriptions never touches the ones a DBA wrote. Extraction reads them back as <c>Description</c>.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class DescriptionTests
{
    private const string Table = "desc_orders";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _props = new();
    private string _db = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["PostgreSQL:Server"] ?? "127.0.0.1";
        _user = config["PostgreSQL:User"];
        _password = config["PostgreSQL:Password"];
        _port = config["PostgreSQL:Port"];
        _props = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        _db = $"ss_desc_{Guid.NewGuid():N}"[..28].ToLowerInvariant();
        using var maint = Open("postgres");
        Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        Exec(maint, $"CREATE DATABASE \"{_db}\"");

        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var maint = Open("postgres");
            Exec(maint, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_db}' AND pid <> pg_backend_pid()");
            Exec(maint, $"DROP DATABASE IF EXISTS \"{_db}\"");
        }
        catch (DbException) { /* teardown must not mask an assertion */ }
    }

    [SetUp]
    public void SetUp() => OnDb(cmd =>
    {
        Exec2(cmd, $"DROP TABLE IF EXISTS public.{Table}");
        Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");
    });

    private IDbConnection Open(string database)
    {
        var c = DbConnectionFactory.ForPlatform(Platform.PostgreSQL)
            .GetDbConnection(ConnectionString.Build(Platform.PostgreSQL, _server, database, _user, _password, _port, _props));
        c.Open();
        return c;
    }

    private static void Exec(IDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private static void Exec2(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void OnDb(Action<IDbCommand> act)
    {
        using var c = Open(_db);
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    private static void Deploy(IDbCommand cmd, string json, bool whatIf = false) =>
        Exec2(cmd, "CALL \"SchemaSmith\".\"TableQuench\"('DescTest', $ss$" + json + "$ss$, " + (whatIf ? "true" : "false") + ", false, false)");

    private static void DeployIndexesOnly(IDbCommand cmd, string json) =>
        Exec2(cmd, "CALL \"SchemaSmith\".\"IndexOnlyQuench\"('DescTest', $ss$" + json + "$ss$, false, false, false)");

    private static string Desc(string description) => description == null ? "" : ", \"Description\": \"" + description + "\"";

    private static string Package(string table = null, string id = null, string pk = null, string ix = null) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + Table + "\"" + Desc(table) + ", \"Columns\": ["
        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false" + Desc(id) + " },"
        + " { \"Name\": \"v\", \"DataType\": \"integer\", \"Nullable\": true } ], \"Indexes\": ["
        + " { \"Name\": \"pk_" + Table + "\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true" + Desc(pk) + " },"
        + " { \"Name\": \"ix_" + Table + "_v\", \"IndexColumns\": \"v\"" + Desc(ix) + " } ] }]";

    private static string Str(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private static string TableComment(IDbCommand cmd) => Str(cmd, $"SELECT obj_description('public.{Table}'::regclass, 'pg_class')");
    private static string ColumnComment(IDbCommand cmd, string column = "id") =>
        Str(cmd, $"SELECT col_description('public.{Table}'::regclass, (SELECT attnum FROM pg_attribute WHERE attrelid = 'public.{Table}'::regclass AND attname = '{column}'))");
    private static string IndexComment(IDbCommand cmd, string index) => Str(cmd, $"SELECT obj_description('public.{index}'::regclass, 'pg_class')");

    private static int Audits(IDbCommand cmd, string action, string objectType = null) =>
        Convert.ToInt32(Str(cmd, "SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ActionType\" = '" + action + "'"
                                 + (objectType == null ? "" : " AND \"ObjectType\" = '" + objectType + "'")));

    [Test]
    public void DeclaredDescriptions_AreApplied_ToTableColumnKeyConstraintAndIndex()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));

            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.EqualTo("all the orders"));
                Assert.That(ColumnComment(cmd), Is.EqualTo("the key"));
                Assert.That(ColumnComment(cmd, "v"), Is.Null, "a column that declares nothing gets nothing");
                Assert.That(IndexComment(cmd, "pk_" + Table), Is.EqualTo("the key index"), "a primary key's backing index takes the comment");
                Assert.That(IndexComment(cmd, "ix_" + Table + "_v"), Is.EqualTo("the lookup"));
                Assert.That(Audits(cmd, "modified", "table"), Is.EqualTo(1));
                Assert.That(Audits(cmd, "modified", "column"), Is.EqualTo(1));
                Assert.That(Audits(cmd, "modified", "index"), Is.EqualTo(2));
            });
        });
    }

    // The comments were joined to ordinary tables only, so a partitioned table, its columns and its indexes never got one.
    [Test]
    public void DeclaredDescriptions_AreApplied_ToAPartitionedTable()
    {
        OnDb(cmd =>
        {
            Exec2(cmd, $"CREATE TABLE public.{Table} (id integer NOT NULL, v integer) PARTITION BY RANGE (id)");
            Deploy(cmd, "[{ \"Schema\": \"public\", \"Name\": \"" + Table + "\", \"Description\": \"all the orders\", \"Columns\": ["
                        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false, \"Description\": \"the key\" },"
                        + " { \"Name\": \"v\", \"DataType\": \"integer\", \"Nullable\": true } ],"
                        + " \"Indexes\": [ { \"Name\": \"ix_" + Table + "_v\", \"IndexColumns\": \"v\", \"Description\": \"the lookup\" } ] }]");

            Assert.Multiple(() =>
            {
                Assert.That(Str(cmd, $"SELECT relkind::text FROM pg_class WHERE oid = 'public.{Table}'::regclass"), Is.EqualTo("p"), "setup: partitioned");
                Assert.That(TableComment(cmd), Is.EqualTo("all the orders"));
                Assert.That(ColumnComment(cmd), Is.EqualTo("the key"));
                Assert.That(IndexComment(cmd, "ix_" + Table + "_v"), Is.EqualTo("the lookup"));
            });
        });
    }

    // The quench runs its statements inside a dollar-quoted block, and a $$ in a description ended it mid-statement.
    [Test]
    public void ADescriptionCarryingDollarQuotes_IsApplied()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "costs $$ only", id: "it's $1"));
            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.EqualTo("costs $$ only"));
                Assert.That(ColumnComment(cmd), Is.EqualTo("it's $1"));
            });
        });
    }

    [Test]
    public void AChangedDescription_IsUpdatedInPlace_AndAnUnchangedOne_IsNotTouched()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "before", id: "stays the same"));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");

            Deploy(cmd, Package(table: "after, with Bob's apostrophe", id: "stays the same"));

            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.EqualTo("after, with Bob's apostrophe"));
                Assert.That(Audits(cmd, "modified", "table"), Is.EqualTo(1));
                Assert.That(Audits(cmd, "modified", "column"), Is.Zero, "a comment that already matches is not rewritten");
            });
        });
    }

    [Test]
    public void AnOmittedDescription_LeavesWhatTheDatabaseHas()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package());
            Exec2(cmd, $"COMMENT ON TABLE public.{Table} IS 'written by a DBA'; COMMENT ON COLUMN public.{Table}.id IS 'also by a DBA'");
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");

            Deploy(cmd, Package());

            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.EqualTo("written by a DBA"), "a package that says nothing about descriptions manages none");
                Assert.That(ColumnComment(cmd), Is.EqualTo("also by a DBA"));
                Assert.That(Audits(cmd, "modified"), Is.Zero);
            });
        });
    }

    [Test]
    public void AnEmptyDescription_RemovesIt_AndRemovingWhatIsAbsent_IsNotAChange()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "doomed", ix: "also doomed"));

            Deploy(cmd, Package(table: "", ix: ""));
            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.Null);
                Assert.That(IndexComment(cmd, "ix_" + Table + "_v"), Is.Null);
            });

            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");
            Deploy(cmd, Package(table: "", ix: ""));
            Assert.That(Audits(cmd, "modified"), Is.Zero);
        });
    }

    [Test]
    public void ARedeployOfAnUnchangedPackage_RecordsNothing()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");

            Deploy(cmd, Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));

            Assert.That(Audits(cmd, "modified"), Is.Zero);
        });
    }

    [Test]
    public void WhatIf_ChangesNothing_AndRecordsWhatItWouldChange()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "before"));
            Exec2(cmd, "DELETE FROM \"SchemaSmith\".\"ChangeAudit\"");

            Deploy(cmd, Package(table: "after", id: "new"), whatIf: true);

            Assert.Multiple(() =>
            {
                Assert.That(TableComment(cmd), Is.EqualTo("before"));
                Assert.That(ColumnComment(cmd), Is.Null);
                Assert.That(Audits(cmd, "wouldModify", "table"), Is.EqualTo(1));
                Assert.That(Audits(cmd, "wouldModify", "column"), Is.EqualTo(1));
                Assert.That(Audits(cmd, "modified"), Is.Zero);
            });
        });
    }

    [Test]
    public void IndexOnlyQuench_ConvergesIndexDescriptions_Too()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(ix: "before"));

            DeployIndexesOnly(cmd, Package(ix: "after"));

            Assert.That(IndexComment(cmd, "ix_" + Table + "_v"), Is.EqualTo("after"));
        });
    }

    [Test]
    public void Extraction_ReadsDescriptionsBack()
    {
        OnDb(cmd =>
        {
            Deploy(cmd, Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));

            var json = Str(cmd, $"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')");
            var table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);

            Assert.Multiple(() =>
            {
                Assert.That(table.Description, Is.EqualTo("all the orders"));
                Assert.That(table.Columns.Single(c => c.Name == "id").Description, Is.EqualTo("the key"));
                Assert.That(table.Columns.Single(c => c.Name == "v").Description, Is.Null, "nothing declared, nothing written");
                Assert.That(table.Indexes.Single(i => i.Name == "pk_" + Table).Description, Is.EqualTo("the key index"));
                Assert.That(table.Indexes.Single(i => i.Name == "ix_" + Table + "_v").Description, Is.EqualTo("the lookup"));
            });
        });
    }
}
