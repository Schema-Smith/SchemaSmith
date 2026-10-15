// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Collections.Generic;
using Npgsql;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// A primary key or unique constraint declared <c>WithoutOverlaps</c> deploys as <c>WITHOUT OVERLAPS</c> on PostgreSQL
/// 18 (a GiST-backed temporal key), survives a redeploy untouched, extracts with the flag and without a redundant
/// <c>AccessMethod</c>, and is re-created when the flag changes. Below 18 the key is created without the clause and
/// the downgrade is recorded. Assertions read <c>pg_constraint</c> and the constraint's oid, which a re-creation changes.
/// Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class TemporalKeyTests
{
    private const string Product = "TkTest";
    private const string Table = "tk_rates";
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
        Exec($"DELETE FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"ObjectName\" LIKE '%{Table}%'");
    }

    private void DropOwnObjects() => Exec($"DROP TABLE IF EXISTS public.{Table}_child; DROP TABLE IF EXISTS public.{Table}");

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

    private static string Package(bool withoutOverlaps) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + Table + "\", \"Columns\": ["
        + " { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false },"
        + " { \"Name\": \"valid\", \"DataType\": \"daterange\", \"Nullable\": false } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + Table + "\", \"IndexColumns\": \"id,valid\", \"PrimaryKey\": true, \"Unique\": true"
        + (withoutOverlaps ? ", \"WithoutOverlaps\": true" : "") + " } ] }]";

    /// <summary>The key's definition as the engine prints it, its index's access method, and its oid.</summary>
    private (string Def, string Am, long Oid) Key()
    {
        var s = Str($"SELECT pg_get_constraintdef(c.oid) || '|' || am.amname || '|' || c.oid::text FROM pg_constraint c JOIN pg_class ic ON ic.oid = c.conindid JOIN pg_am am ON am.oid = ic.relam WHERE c.conrelid = 'public.{Table}'::regclass AND c.conname = 'pk_{Table}'");
        Assert.That(s, Is.Not.Null, "the primary key must exist");
        var p = s.Split('|');
        return (p[0], p[1], long.Parse(p[2]));
    }

    private int Downgrades() =>
        Count($"SELECT COUNT(*) FROM \"SchemaSmith\".\"ChangeAudit\" WHERE \"SessionId\" = pg_backend_pid() AND \"ObjectType\" = 'WITHOUT OVERLAPS (PG18)' AND \"ActionType\" = 'downgraded' AND \"ObjectName\" LIKE '%{Table}%'");

    [Test]
    public void ATemporalKey_Deploys_SurvivesARedeploy_AndIsRecreatedWhenTheFlagChanges()
    {
        if (ServerMajor() < 18) Assert.Ignore("WITHOUT OVERLAPS is PostgreSQL 18+");
        Exec("CREATE EXTENSION IF NOT EXISTS btree_gist");

        Deploy(Package(withoutOverlaps: true));
        var first = Key();
        Assert.Multiple(() =>
        {
            Assert.That(first.Def, Is.EqualTo("PRIMARY KEY (id, valid WITHOUT OVERLAPS)"));
            Assert.That(first.Am, Is.EqualTo("gist"), "the engine backs a temporal key with GiST");
            Assert.That(Downgrades(), Is.Zero);
        });

        Deploy(Package(withoutOverlaps: true));
        Assert.That(Key().Oid, Is.EqualTo(first.Oid), "an unchanged temporal key is not re-created: the compare reads conperiod and takes the GiST index as implied");

        Deploy(Package(withoutOverlaps: false));
        var plain = Key();
        Assert.Multiple(() =>
        {
            Assert.That(plain.Def, Is.EqualTo("PRIMARY KEY (id, valid)"));
            Assert.That(plain.Am, Is.EqualTo("btree"));
            Assert.That(plain.Oid, Is.Not.EqualTo(first.Oid), "dropping the flag re-creates the key");
        });
    }

    [Test]
    public void Extraction_WritesTheFlag_AndNoAccessMethod()
    {
        if (ServerMajor() < 18) Assert.Ignore("WITHOUT OVERLAPS is PostgreSQL 18+");
        Exec("CREATE EXTENSION IF NOT EXISTS btree_gist");
        Deploy(Package(withoutOverlaps: true));

        var json = Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}')");
        var table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);
        var key = (PostgreSqlIndex)table.Indexes.Single(i => i.Name == $"pk_{Table}");
        Assert.Multiple(() =>
        {
            Assert.That(key.WithoutOverlaps, Is.True);
            Assert.That(key.IndexColumns, Is.EqualTo("id,valid"));
            Assert.That(key.AccessMethod, Is.Null.Or.Empty, "GiST follows from the flag; writing it would only restate it");
        });
    }

    [Test]
    public void ATemporalForeignKey_IsLeftOutOfTheExtraction_AndTheExtractionSaysSo()
    {
        if (ServerMajor() < 18) Assert.Ignore("temporal foreign keys are PostgreSQL 18+");
        Exec("CREATE EXTENSION IF NOT EXISTS btree_gist");
        Deploy(Package(withoutOverlaps: true));
        Exec($"CREATE TABLE public.{Table}_child (id integer NOT NULL, parent_id integer NOT NULL, valid daterange NOT NULL, " +
             $"CONSTRAINT pk_{Table}_child PRIMARY KEY (id, valid WITHOUT OVERLAPS), " +
             $"CONSTRAINT fk_{Table}_child_temporal FOREIGN KEY (parent_id, PERIOD valid) REFERENCES public.{Table} (id, PERIOD valid))");

        var warnings = new List<string>();
        var npgsql = (NpgsqlConnection)_connection;
        void OnNotice(object sender, NpgsqlNoticeEventArgs e) { if (e.Notice.Severity == "WARNING") warnings.Add(e.Notice.MessageText); }
        npgsql.Notice += OnNotice;
        try
        {
            var json = Str($"SELECT \"SchemaSmith\".\"GenerateTableJSON\"('public', '{Table}_child')");
            var table = (PostgreSqlTable)PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);
            Assert.Multiple(() =>
            {
                Assert.That(table.ForeignKeys, Is.Empty, "the temporal key is not written as a plain key, which would deploy as a different constraint");
                Assert.That(((PostgreSqlIndex)table.Indexes.Single()).WithoutOverlaps, Is.True, "the child's own temporal primary key is modelled as usual");
                Assert.That(warnings, Has.Exactly(1).Items);
                Assert.That(warnings[0], Does.Contain($"fk_{Table}_child_temporal").And.Contain("PERIOD").And.Contain("not modelled"));
            });
        }
        finally
        {
            npgsql.Notice -= OnNotice;
        }
    }

    [Test]
    public void Below18_TheKeyIsCreatedWithoutTheClause_AndTheDowngradeIsRecorded()
    {
        if (ServerMajor() >= 18) Assert.Ignore("the degrade is for servers below PostgreSQL 18");

        Deploy(Package(withoutOverlaps: true));

        var key = Key();
        Assert.Multiple(() =>
        {
            Assert.That(key.Def, Is.EqualTo("PRIMARY KEY (id, valid)"), "the same key, without the clause this server lacks");
            Assert.That(key.Am, Is.EqualTo("btree"));
            Assert.That(Downgrades(), Is.EqualTo(1), "and the manifest says so");
        });
    }
}
