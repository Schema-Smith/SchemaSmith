// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// The PostgreSQL twin of the replay tests on the other engines: a contention retry calls MissingTableAndColumnQuench
/// a second time against the same parse, and it must re-derive what already exists rather than replay the CREATE or
/// the ADD COLUMN. This engine gates each add on a live catalog check, so it is pinned here rather than fixed.
/// </summary>
[Category("PostgreSQL")]
[Category("Integration")]
[TestFixture]
public class QuenchReplayIdempotencyTests
{
    private IDbConnection _connection = null!;
    private const string NewTable = "replay_new_tbl_pg";
    private const string ExistingTable = "replay_existing_tbl_pg";

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        DropProbes();
    }

    [TearDown]
    public void TearDown()
    {
        DropProbes();
        _connection?.Close();
        _connection?.Dispose();
    }

    private void DropProbes()
    {
        foreach (var t in new[] { NewTable, ExistingTable })
            Exec($"DROP TABLE IF EXISTS public.{t}");
    }

    private static string TableJson(string table, string extraColumn) =>
        "[{ \"Schema\": \"public\", \"Name\": \"" + table + "\", \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"integer\", \"Nullable\": false }, " + extraColumn + " ] }]";

    [Test]
    public void ReplayingTheQuenchAfterItCreatedATable_DoesNotAttemptToCreateItAgain()
    {
        Parse(TableJson(NewTable, "{ \"Name\": \"label\", \"DataType\": \"varchar(40)\", \"Nullable\": true }"));
        Quench();
        Assert.That(ColumnCount(NewTable), Is.EqualTo(2), "the first CALL must create the table");

        Assert.DoesNotThrow(Quench, "the replay must not re-issue the CREATE");
        Assert.That(ColumnCount(NewTable), Is.EqualTo(2), "the replay must leave the table as the first pass built it");
    }

    [Test]
    public void ReplayingTheQuenchAfterItAddedAColumn_DoesNotAttemptToAddItAgain()
    {
        Exec($"CREATE TABLE public.{ExistingTable} (id integer NOT NULL)");

        Parse(TableJson(ExistingTable, "{ \"Name\": \"added\", \"DataType\": \"varchar(20)\", \"Nullable\": true }"));
        Quench();
        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2), "the first CALL must add the column");

        Assert.DoesNotThrow(Quench, "the replay must not re-ADD the column");
        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2), "the replay must not have changed the table");
    }

    private void Parse(string json) => Exec($"CALL \"SchemaSmith\".\"ParseTableJson\"('{json.Replace("'", "''")}')");

    private void Quench() => Exec("CALL \"SchemaSmith\".\"MissingTableAndColumnQuench\"(FALSE)");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private int ColumnCount(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
