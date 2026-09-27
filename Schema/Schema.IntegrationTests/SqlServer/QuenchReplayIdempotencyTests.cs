// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// <c>SchemaSmith.MissingTableAndColumnQuench</c> must be safe to EXEC twice against one parse, because
/// that is exactly what a contention retry does.
/// <para><c>ExecuteNonQueryHandlingMessages(..., retryOnDeadlock: true)</c> re-issues the command and its
/// contract says the convergence procs "recompute desired-vs-existing every run". They did not:
/// <c>NewTable</c>/<c>NewColumn</c> are computed by <c>ParseTableJsonIntoTempTables</c>, a SEPARATE and
/// EARLIER command, so on a replay they still described the catalog as it was before the failed attempt and
/// everything that attempt had created was re-created. Found on the MySQL family (MariaDB 11.8, the
/// database-per-tenant fleet in <c>Demos/Learn/course7-module-01</c>); this engine carries the same shape.</para>
/// <para>The table half of the fix already existed here but was gated behind
/// <c>IF OBJECT_ID('SchemaSmith.CustomTableRestore') IS NOT NULL</c> — a hook almost nothing installs — so
/// it was written for the restore case and switched off for everyone else. The COLUMN half had no
/// equivalent at all on this engine (the MySQL twin already clears <c>NewColumn</c> after its rename pass,
/// which is why only the table half was missing there).</para>
/// <para>The parse script is run through <see cref="ForgeKindler.GetParseTableJsonScript"/> rather than by
/// hand-filling the temp tables: the proc under test consumes whatever the real parse produces, and a
/// hand-built working set would quietly diverge from it.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class QuenchReplayIdempotencyTests
{
    private IDbConnection _connection = null!;
    private const string NewTable = "replay_new_tbl_ss";
    private const string ExistingTable = "replay_existing_tbl_ss";

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
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
            Exec($"DROP TABLE IF EXISTS dbo.[{t}]");
    }

    [Test]
    public void ReplayingTheQuenchAfterItCreatedATable_DoesNotAttemptToCreateItAgain()
    {
        Parse(TableJson(NewTable, "    { \"Name\": \"[Label]\", \"DataType\": \"VARCHAR(40)\", \"Nullable\": true }"));
        Quench();
        Assert.That(ColumnCount(NewTable), Is.EqualTo(2), "the first EXEC must create the table");

        Assert.DoesNotThrow(Quench,
            "the replay must re-derive what already exists rather than re-issuing the CREATE. Otherwise the "
            + "retry that exists to absorb transient contention is what fails the deploy, with "
            + "\"There is already an object named ...\".");

        Assert.That(ColumnCount(NewTable), Is.EqualTo(2),
            "the replay must leave the table exactly as the first pass built it");
    }

    [Test]
    public void ReplayingTheQuenchAfterItAddedAColumn_DoesNotAttemptToAddItAgain()
    {
        Exec($"CREATE TABLE dbo.[{ExistingTable}] ([Id] INT NOT NULL)");

        Parse(TableJson(ExistingTable, "    { \"Name\": \"[Added]\", \"DataType\": \"VARCHAR(20)\", \"Nullable\": true }"));
        Quench();
        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2), "the first EXEC must add the column");

        Assert.DoesNotThrow(Quench,
            "the replay must not re-ADD a column the first pass already added -- SQL Server answers "
            + "\"Column names in each table must be unique\" and the deploy dies. Unlike the MySQL twin, "
            + "this engine had no pre-existing NewColumn re-derivation, so the table half alone would only "
            + "have moved the failure from the duplicate table to the duplicate column.");

        Assert.That(ColumnCount(ExistingTable), Is.EqualTo(2),
            "the replay must not have changed the table");
    }

    private static string TableJson(string table, string secondColumn) =>
        "[{\n  \"Schema\": \"[dbo]\",\n  \"Name\": \"[" + table + "]\",\n  \"Columns\": [\n"
        + "    { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },\n"
        + secondColumn + "\n  ]\n}]";

    private void Parse(string json)
    {
        // The frame TableQuench gives the script: the payload plus the fill-factor switch.
        var parse = ForgeKindler.GetParseTableJsonScript(Platform.SqlServer);
        Exec("DECLARE @TableDefinitions NVARCHAR(MAX) = N'" + json.Replace("'", "''") + "';\r\n"
             + "DECLARE @UpdateFillFactor BIT = 0;\r\n" + parse);
    }

    private void Quench() => Exec("EXEC SchemaSmith.MissingTableAndColumnQuench @WhatIf = 0");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int ColumnCount(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo." + table + "')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
