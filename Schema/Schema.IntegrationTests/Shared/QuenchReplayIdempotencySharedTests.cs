// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// <c>MissingTableAndColumnQuench</c> must be safe to CALL TWICE against one parse — because that is
/// exactly what a retry does.
/// <para><c>ExecuteNonQueryHandlingMessages(..., retryOnDeadlock: true)</c> re-issues the CALL when it
/// classifies a failure as transient contention, and its contract says the convergence procs "recompute
/// desired-vs-existing every run". They did not. <c>NewTable</c>/<c>NewColumn</c> are computed by
/// <c>ParseTableJson</c> — a SEPARATE, EARLIER command — so on a retry they still described the catalog as
/// it was before the failed attempt, and every table that attempt had already created was replayed. The
/// deploy then died with "Table 'X' already exists": the mechanism that exists to absorb a transient blip
/// converted it into a failed deployment.</para>
/// <para>Found on MariaDB 11.8 running <c>Demos/Learn/course7-module-01</c>, whose subject is parallel
/// database-per-tenant fan-out — four tables created, contention in the add-columns pass, retry, dead on
/// the first CREATE. It took out 4 of 5 tenant databases and passed on 11.4, which is why it read as a
/// version problem; it is not, and 11.4 was luck. PostgreSQL's twin never had it, because that one gates
/// every add on a live <c>NOT EXISTS</c> against <c>information_schema</c> rather than trusting a flag.</para>
/// <para>CALLING THE PROC TWICE IS THE TEST, deliberately. Inducing real contention is timing-dependent
/// and would make this flaky; the replay is the exact condition a retry creates and it is deterministic.
/// A test that drove one deploy and asserted success would pass over the defect entirely.</para>
/// </summary>
public abstract class QuenchReplayIdempotencySharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    private const string NewTable = "replay_new_tbl";
    private const string ExistingTable = "replay_existing_tbl";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    [TearDown]
    public void DropProbeTables()
    {
        foreach (var t in new[] { NewTable, ExistingTable })
            Exec($"DROP TABLE IF EXISTS `{_testDb}`.`{t}`");
    }

    /// <summary>
    /// A brand-new table: the first CALL creates it, the second must not try to create it again.
    /// </summary>
    [Test]
    public void ReplayingTheQuenchAfterItCreatedATable_DoesNotAttemptToCreateItAgain()
    {
        var json = $@"[{{""Name"":""{NewTable}"",""Columns"":[
            {{""Name"":""Id"",""DataType"":""INT"",""Nullable"":false}},
            {{""Name"":""Label"",""DataType"":""VARCHAR(40)"",""Nullable"":true}}]}}]";

        Parse(json);
        Quench();                                   // creates it
        Assert.That(TableExists(NewTable), Is.True, "the first CALL must create the table");

        Assert.DoesNotThrow(Quench,
            "the second CALL replays the same parse, which is precisely what a contention retry does. It "
            + "must re-derive what already exists rather than replaying the CREATE -- otherwise a transient "
            + "blip becomes a failed deploy.");

        Assert.That(ColumnCount(NewTable), Is.EqualTo(2),
            "the replay must leave the table exactly as the first pass built it");
    }

    /// <summary>
    /// A NEW COLUMN on an EXISTING table — the other half, and the one the table flag alone does not cover.
    /// A replay here re-issues <c>ALTER TABLE … ADD COLUMN</c> and fails on the duplicate column, so
    /// clearing only <c>NewTable</c> would have moved the failure rather than fixed it.
    /// </summary>
    [Test]
    public void ReplayingTheQuenchAfterItAddedAColumn_DoesNotAttemptToAddItAgain()
    {
        Exec($"CREATE TABLE `{_testDb}`.`{ExistingTable}` (`Id` INT NOT NULL)");

        var json = $@"[{{""Name"":""{ExistingTable}"",""Columns"":[
            {{""Name"":""Id"",""DataType"":""INT"",""Nullable"":false}},
            {{""Name"":""Added"",""DataType"":""VARCHAR(20)"",""Nullable"":true}}]}}]";

        Parse(json);
        Quench();                                   // adds `Added`
        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2), "the first CALL must add the column");

        Assert.DoesNotThrow(Quench,
            "the replay must not re-ADD a column the first pass already added.");

        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2),
            "the replay must not have changed the table");
    }

    private void Parse(string json) =>
        Exec($"CALL SchemaSmith_ParseTableJson('{_testDb}', '{json.Replace("'", "''")}')");

    private void Quench() =>
        Exec($"CALL SchemaSmith_MissingTableAndColumnQuench('{_testDb}', 0)");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool TableExists(string table) => Scalar(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE BINARY TABLE_SCHEMA = BINARY '{_testDb}' "
        + $"AND BINARY TABLE_NAME = BINARY '{table}'") == 1;

    private int ColumnCount(string table) => Scalar(
        $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE BINARY TABLE_SCHEMA = BINARY '{_testDb}' "
        + $"AND BINARY TABLE_NAME = BINARY '{table}'");

    private int Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
