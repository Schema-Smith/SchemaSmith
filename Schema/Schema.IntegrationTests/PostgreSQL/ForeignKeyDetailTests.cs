// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Npgsql;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// The PostgreSQL foreign-key details of WS-03: <c>SetNullColumns</c> deploys <c>ON DELETE SET NULL (col, ...)</c> on 15+,
/// survives a redeploy untouched, extracts and is re-created when it changes, and below 15 the key is created with a
/// plain <c>SET NULL</c> and the downgrade recorded; an omitted <c>MatchType</c> is the engine's own <c>SIMPLE</c>; and a
/// PostgreSQL 18 NOT NULL constraint that is NOT VALID is called out at extraction. Assertions read <c>pg_constraint</c>
/// and the constraint's oid, which a re-creation changes. Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ForeignKeyDetailTests
{
    private const string Product = "FkdTest";
    private const string Parent = "fkd_parent";
    private const string Table = "fkd_child";
    private const string Fk = "fk_fkd_child_parent";
    private IDbConnection _connection;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        DropOwnObjects();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try { DropOwnObjects(); }
        finally { _connection.Close(); _connection.Dispose(); }
    }

    [SetUp]
    public void SetUp()
    {
        DropOwnObjects();
        Exec($"DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%fkd_%'");
    }

    private void DropOwnObjects() => Exec($"DROP TABLE IF EXISTS public.{Table}; DROP TABLE IF EXISTS public.{Parent}");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(Str(sql) ?? "0");

    private int ServerMajor() => Convert.ToInt32(Str("SELECT current_setting('server_version_num')::int / 10000"));

    private void Deploy(string json) =>
        Exec("CALL \"SchemaSmith\".\"TableQuench\"('" + Product + "', $ss$" + json + "$ss$, false, false, false)");

    /// <param name="fk">Keys appended to the foreign key, e.g. <c>, "SetNullColumns": "parent_id"</c>.</param>
    private static string Package(string fk = "", string deleteAction = "SET NULL") =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + Parent + "\", \"Columns\": ["
        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"code\", \"DataType\": \"integer\", \"Nullable\": false } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + Parent + "\", \"IndexColumns\": \"id,code\", \"PrimaryKey\": true, \"Unique\": true } ] },"
        + " { \"Schema\": \"public\", \"Name\": \"" + Table + "\", \"Columns\": ["
        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"parent_id\", \"DataType\": \"integer\", \"Nullable\": true },"
        + " { \"Name\": \"parent_code\", \"DataType\": \"integer\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + Table + "\", \"IndexColumns\": \"id\", \"PrimaryKey\": true, \"Unique\": true } ],"
        + " \"ForeignKeys\": [ { \"Name\": \"" + Fk + "\", \"Columns\": \"parent_id,parent_code\", \"RelatedTableSchema\": \"public\", \"RelatedTable\": \"" + Parent + "\", \"RelatedColumns\": \"id,code\", \"DeleteAction\": \"" + deleteAction + "\"" + fk + " } ] }]";

    /// <summary>The key's definition as the engine prints it, its match type, and its oid.</summary>
    private (string Def, char Match, long Oid) Key()
    {
        var s = Str($"SELECT pg_get_constraintdef(oid) || '|' || confmatchtype::text || '|' || oid::text FROM pg_constraint WHERE conrelid = 'public.{Table}'::regclass AND conname = '{Fk}'");
        Assert.That(s, Is.Not.Null, "the foreign key must exist");
        var p = s.Split('|');
        return (p[0], p[1][0], long.Parse(p[2]));
    }

    private int Downgrades() =>
        Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectType\" = 'ON DELETE SET NULL (columns) (PG15)' AND \"ActionType\" = 'downgraded' AND \"ObjectName\" LIKE '%{Fk}%'");

    private PostgreSqlForeignKey Extracted()
    {
        var json = Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')");
        var table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);
        return (PostgreSqlForeignKey)table.ForeignKeys.Single(f => f.Name == Fk);
    }

    [Test]
    public void SetNullColumns_Deploys_SurvivesARedeploy_Extracts_AndIsRecreatedWhenItChanges()
    {
        if (ServerMajor() < 15) Assert.Ignore("ON DELETE SET NULL (columns) is PostgreSQL 15+");

        Deploy(Package(", \"SetNullColumns\": \"parent_id\""));
        var first = Key();
        Assert.Multiple(() =>
        {
            Assert.That(first.Def, Does.EndWith("ON DELETE SET NULL (parent_id)"), "the subset, as the engine prints it");
            Assert.That(Downgrades(), Is.Zero);
            Assert.That(Extracted().SetNullColumns, Is.EqualTo("parent_id"));
        });

        Deploy(Package(", \"SetNullColumns\": \"parent_id\""));
        Assert.That(Key().Oid, Is.EqualTo(first.Oid), "an unchanged key is not re-created");

        Deploy(Package());
        var plain = Key();
        Assert.Multiple(() =>
        {
            Assert.That(plain.Def, Does.EndWith("ON DELETE SET NULL"), "every column, as a plain SET NULL does");
            Assert.That(plain.Oid, Is.Not.EqualTo(first.Oid), "a changed subset is a drop and re-create");
            Assert.That(Extracted().SetNullColumns, Is.Null.Or.Empty);
        });
    }

    // The subset was emitted for SET NULL alone: a SET DEFAULT key lost it whenever it was re-created, then churned.
    [Test]
    public void SetDefaultColumns_Deploy_KeepTheirSubsetWhenTheKeyIsRecreated_AndThenStayPut()
    {
        if (ServerMajor() < 15) Assert.Ignore("ON DELETE SET DEFAULT (columns) is PostgreSQL 15+");

        Deploy(Package(", \"SetNullColumns\": \"parent_code\"", "SET DEFAULT"));
        Assert.That(Key().Def, Does.EndWith("ON DELETE SET DEFAULT (parent_code)"));

        Deploy(Package(", \"SetNullColumns\": \"parent_code\", \"UpdateAction\": \"CASCADE\"", "SET DEFAULT"));
        var recreated = Key();
        Assert.That(recreated.Def, Does.Contain("ON DELETE SET DEFAULT (parent_code)"), "re-created for its update action, the subset kept");

        Deploy(Package(", \"SetNullColumns\": \"parent_code\", \"UpdateAction\": \"CASCADE\"", "SET DEFAULT"));
        Assert.Multiple(() =>
        {
            Assert.That(Key().Oid, Is.EqualTo(recreated.Oid), "an unchanged key is not re-created");
            Assert.That(Extracted().SetNullColumns, Is.EqualTo("parent_code"));
        });
    }

    [Test]
    public void Below15_TheKeyIsCreatedWithAPlainSetNull_AndTheDowngradeIsRecorded()
    {
        if (ServerMajor() >= 15) Assert.Ignore("the degrade is for servers below PostgreSQL 15");

        Deploy(Package(", \"SetNullColumns\": \"parent_id\""));

        Assert.Multiple(() =>
        {
            Assert.That(Key().Def, Does.EndWith("ON DELETE SET NULL"));
            Assert.That(Downgrades(), Is.EqualTo(1));
        });
    }

    [Test]
    public void AnOmittedMatchType_IsTheEnginesSimple_AndFullIsFull()
    {
        Deploy(Package());
        Assert.That(Key().Match, Is.EqualTo('s'), "the package's default is the engine's, so what CREATE TABLE gives is what a redeploy keeps");

        // The compare does not read the match type (WS-07 takes that up), so a fresh key, not a changed one.
        DropOwnObjects();
        Deploy(Package(", \"MatchType\": \"FULL\""));
        Assert.That(Key().Match, Is.EqualTo('f'));
    }

    [Test]
    public void ANotValidNotNullConstraint_IsCalledOutAtExtraction()
    {
        if (ServerMajor() < 18) Assert.Ignore("named NOT NULL constraints are PostgreSQL 18+");
        Deploy(Package());
        Exec($"INSERT INTO public.{Table} (id) VALUES (1)");
        Exec($"ALTER TABLE public.{Table} ADD CONSTRAINT {Table}_parent_id_not_null NOT NULL parent_id NOT VALID");

        var warnings = new List<string>();
        var npgsql = (NpgsqlConnection)_connection;
        void OnNotice(object sender, NpgsqlNoticeEventArgs e) { if (e.Notice.Severity == "WARNING") warnings.Add(e.Notice.MessageText); }
        npgsql.Notice += OnNotice;
        try
        {
            var json = Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')");
            var column = ((PostgreSqlTable)PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL)).Columns.Single(c => c.Name == "parent_id");
            Assert.Multiple(() =>
            {
                Assert.That(column.Nullable, Is.False, "the catalog says NOT NULL, and the package follows it");
                Assert.That(warnings, Has.Exactly(1).Items);
                Assert.That(warnings[0], Does.Contain($"{Table}_parent_id_not_null").And.Contain("NOT VALID").And.Contain("parent_id"));
            });
        }
        finally
        {
            npgsql.Notice -= OnNotice;
        }
    }
}
