// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using MySqlConnector;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Parallelizable(scope: ParallelScope.Self)]
public class RebuildTableTests : RebuildTableSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    /// <summary>
    /// MariaDB-only, and the single most important refusal in the whole feature. A system-versioned table
    /// carries its own row history inside the table; a shadow copy reads only the current rows, so a
    /// rebuild would leave a table that looks completely correct and has silently lost every historical
    /// version -- and nothing in the schema package can put that back. MySQL has no such state, which is
    /// why SchemaSmith_RebuildBlockedReason's MySQL body returns NULL and only the MariaDb variant has a
    /// body to run; this is the test that proves the variant is actually reached.
    /// </summary>
    [Test]
    public void Rebuild_SystemVersionedTable_IsRefused_AndTheErrorNamesTheReasonAndTheTable()
    {
        var table = $"RbSysVer_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        // MariaDB gained system versioning in 10.3; on the 10.2 floor the CREATE below is a hard
        // syntax error, so the state under test cannot exist here at all.
        cmd.CommandText = "SELECT VERSION()";
        var serverVersion = cmd.ExecuteScalar()?.ToString() ?? "";
        if (!FixtureSetup.SupportsSystemVersioning(serverVersion))
            Assert.Ignore($"MariaDB {serverVersion} predates system-versioned tables (10.3), so this state cannot be created on the supported floor.");

        cmd.CommandText = $"USE `{_mainDb}`";
        cmd.ExecuteNonQuery();

        try
        {
            cmd.CommandText = $@"
CREATE TABLE `{table}` (`Id` INT NOT NULL, `Val` VARCHAR(50) NULL, PRIMARY KEY (`Id`)) WITH SYSTEM VERSIONING;
INSERT INTO `{table}` (`Id`, `Val`) VALUES (1, 'one'), (2, 'two');
UPDATE `{table}` SET `Val` = 'one-changed' WHERE `Id` = 1;";
            cmd.ExecuteNonQuery();

            // Guards the premise rather than assuming it: MariaDB reports a system-versioned table with a
            // TABLE_TYPE of its own, and that is exactly what the guard function keys on. If a future
            // MariaDB stopped reporting it this way, the refusal below would stop firing and this test
            // would need to fail rather than quietly pass for the wrong reason.
            cmd.CommandText = "SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES "
                              + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("SYSTEM VERSIONED"),
                "Setup precondition: system versioning must actually be on, or the refusal is not being tested.");

            // And there must genuinely be history to lose -- an UPDATE leaves the superseded row version
            // behind, visible only through FOR SYSTEM_TIME ALL. Without this the test would prove the
            // refusal fires but not that it matters.
            cmd.CommandText = $"SELECT COUNT(*) FROM `{table}` FOR SYSTEM_TIME ALL";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3),
                "Setup precondition: the table must hold a superseded row version (2 current + 1 historical), "
                + "since that history is precisely what a shadow copy would destroy.");

            var json = $$"""
                [{
                    "Name": "{{table}}",
                    "Columns": [
                        {"Name": "Id", "DataType": "INT", "Nullable": false},
                        {"Name": "Val", "DataType": "VARCHAR(50)", "Nullable": true}
                    ],
                    "Indexes": [
                        {"Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id"}
                    ]
                }]
                """;

            ClearLog(cmd);
            var ex = Assert.Throws<MySqlException>(() => Rebuild(cmd, json, table));

            // Asserted non-null first so a failure names the missing message rather than reporting a
            // string mismatch against null.
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.Message, Does.Contain("system versioning"),
                "The refusal must name the blocking state. 'This table cannot be rebuilt' leaves the "
                + $"operator no way to know what to disable or migrate. Got: '{ex.Message}'.");
            Assert.That(ex.Message, Does.Contain(table),
                $"The refusal must name the table, since a deploy touches many. Got: '{ex.Message}'.");

            cmd.CommandText = $"SELECT COUNT(*) FROM `{table}` FOR SYSTEM_TIME ALL";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3),
                "The refusal must fire BEFORE any DDL -- every row version, current and historical, must "
                + "still be there.");

            cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                              + $"WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}_SchemaSmithRebuild'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                "No shadow table may exist after a refusal; a half-built rebuild is worse than none.");

            // A preview that hid the refusal would tell the operator a rebuild is available on a table
            // where it can never be, so the guard has to fire in WhatIf too.
            var whatIfEx = Assert.Throws<MySqlException>(() => Rebuild(cmd, json, table, whatIf: true));
            Assert.That(whatIfEx, Is.Not.Null);
            Assert.That(whatIfEx!.Message, Does.Contain("system versioning"),
                "WhatIf must surface the impossibility rather than printing a rebuild that could never run. "
                + $"Got: '{whatIfEx.Message}'.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{table}`, `{table}_SchemaSmithRebuild`, `{table}_SchemaSmithOld`";
            cmd.ExecuteNonQuery();
            ClearLog(cmd);
            conn.Close();
        }
    }

    private static void ClearLog(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }
}
