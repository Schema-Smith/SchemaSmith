// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// SchemaSmith.RebuildTable on SQL Server: the shadow-copy-and-swap engine. It is the only thing in
/// SchemaSmith that destroys user data, so every test here asserts what the USER ends up holding -- rows,
/// identifiers, column order -- rather than which statements were emitted to get there.
///
/// The procedure reads the declared definition from the #Columns / #Tables temp tables that
/// ParseTableJsonIntoTempTables populates, so each test runs the real parse script (the same text
/// ForgeKindler bakes into TableQuench) in its own batch and then calls the procedure from that batch.
/// That is the procedure's actual contract; faking the temp tables would test a different procedure.
///
/// Nothing here elects a rebuild -- these call the procedure directly, which keeps the engine's own
/// behaviour (refusals, identity, row preservation) separable from the policy that points it at a table.
/// The decision that elects one lives in ModifiedTableQuench and is covered by RebuildDecisionTests.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class RebuildTableTests : BaseTableQuenchTests
{
    private static readonly string ParseJsonScript = ForgeKindler.GetParseTableJsonScript(Platform.SqlServer);

    // ---- harness ------------------------------------------------------------

    private IDbConnection OpenMainDb()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static string RebuildBatch(string json, string table, bool whatIf = false, string schema = "dbo")
    {
        return "DECLARE @TableDefinitions NVARCHAR(MAX) = N'" + json.Replace("'", "''") + "';\r\n"
               + "DECLARE @UpdateFillFactor BIT = 0;\r\n"
               + ParseJsonScript + "\r\n"
               + $"EXEC SchemaSmith.RebuildTable @p_Schema = N'{schema}', @p_Table = N'{table}', @p_WhatIf = {(whatIf ? 1 : 0)};";
    }

    private static void Rebuild(IDbCommand cmd, string json, string table, bool whatIf = false)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = RebuildBatch(json, table, whatIf);
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }

    private static void Exec(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static int Int(IDbCommand cmd, string sql)
    {
        var v = Scalar(cmd, sql);
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt32(v);
    }

    private static List<string> Strings(IDbCommand cmd, string sql)
    {
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return results;
    }

    /// <summary>
    /// sp_cdc_enable_table writes database-wide replication metadata and deadlocks against sibling
    /// fixtures doing DDL in the same database -- see TableQuench_CDCTests, which hit this in CI. Same
    /// retry, for the same reason: retrying beats serializing the suite or narrowing the run to a mode
    /// that happens to pass.
    /// </summary>
    private static void ExecWithDeadlockRetry(IDbCommand cmd, string sql)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                cmd.CommandTimeout = 120;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
            }
            catch (DbException e) when (e.Message.ContainsIgnoringCase("deadlock victim"))
            {
                Thread.Sleep(1000);
            }
        }

        // Out of retries -- run unguarded so the real error surfaces instead of a synthesized one.
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- rows survive -------------------------------------------------------

    [Test]
    public void Rebuild_PreservesEveryRow_AndAuditsTheRebuild()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildRowsSurvive;
CREATE TABLE dbo.RebuildRowsSurvive (Id INT NOT NULL, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildRowsSurvive (Id, Val) VALUES (1, 'one'), (2, 'two'), (3, NULL);");

        // Captured so the assertions below cannot pass on a procedure that did nothing at all: if no
        // rebuild happened the rows would obviously still be there, and every row assertion would be
        // vacuous. A new object_id is the only proof the table was actually replaced.
        var objectIdBefore = Int(cmd, "SELECT OBJECT_ID('dbo.RebuildRowsSurvive')");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildRowsSurvive]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildRowsSurvive");

        var objectIdAfter = Int(cmd, "SELECT OBJECT_ID('dbo.RebuildRowsSurvive')");
        Assert.That(objectIdAfter, Is.Not.EqualTo(objectIdBefore),
            "The table must actually have been replaced. Without this the row assertions below would pass "
            + "just as happily on a procedure that returned without doing anything.");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildRowsSurvive"), Is.EqualTo(3),
            "Carrying the rows across IS the feature. A rebuild that loses rows is data destruction with a "
            + "successful exit code.");
        Assert.That(Strings(cmd, "SELECT ISNULL(Val, '<null>') FROM dbo.RebuildRowsSurvive ORDER BY Id"),
            Is.EqualTo(new List<string> { "one", "two", "<null>" }),
            "Values must arrive intact and paired with the same keys -- a copy that shifts columns would "
            + "still produce three rows.");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[RebuildRowsSurvive]' AND ActionType = 'rebuilt'"),
            Is.EqualTo(1),
            "A rebuild replaces a table wholesale; the run manifest has to say so, or an operator reading "
            + "the audit sees a table that changed with nothing accounting for it.");

        // Int() maps a NULL OBJECT_ID to -1, so "no such object" is an unambiguous value rather than a
        // null/DBNull ambiguity in the assertion.
        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildRowsSurvive_SchemaSmithRebuild')"), Is.EqualTo(-1),
            "The shadow table must not survive the swap.");
        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildRowsSurvive_SchemaSmithOld')"), Is.EqualTo(-1),
            "The renamed-out original must not survive the swap.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildRowsSurvive");
        conn.Close();
    }

    // ---- the proven identity defect ----------------------------------------

    [Test]
    public void Rebuild_DoesNotReissueTheIdentityOfADeletedHighestRow()
    {
        // THE regression test for the reseed defect. Ids 1-3 with id 3 deleted: IDENT_CURRENT is 3 (the
        // last value HANDED OUT) while max(id) is 2 (the largest still present). Reseeding to the copied
        // max makes the next insert re-issue 3 -- an identifier the old table had already given to a row
        // that existed -- and nothing errors. Anything that recorded the old 3 (an audit row, an export,
        // a downstream system) then aliases two different entities.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildIdentityReuse;
CREATE TABLE dbo.RebuildIdentityReuse (Id INT IDENTITY(1,1) NOT NULL, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildIdentityReuse (Val) VALUES ('a'), ('b'), ('c');
DELETE FROM dbo.RebuildIdentityReuse WHERE Id = 3;");

        // Guard the setup itself: if the seeding did not actually issue 3, the whole test is vacuous --
        // the next insert would get 3 for entirely innocent reasons and the assertion below would pass
        // while proving nothing.
        Assert.That(Int(cmd, "SELECT CONVERT(INT, IDENT_CURRENT('dbo.RebuildIdentityReuse'))"), Is.EqualTo(3),
            "Setup precondition: the original counter must have issued 3 and the row must be gone.");
        Assert.That(Int(cmd, "SELECT MAX(Id) FROM dbo.RebuildIdentityReuse"), Is.EqualTo(2),
            "Setup precondition: the surviving rows must stop at 2, so max(id) and the counter disagree.");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildIdentityReuse]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT IDENTITY(1,1)", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildIdentityReuse");

        Assert.That(Strings(cmd, "SELECT CONVERT(NVARCHAR(20), Id) + '=' + Val FROM dbo.RebuildIdentityReuse ORDER BY Id"),
            Is.EqualTo(new List<string> { "1=a", "2=b" }),
            "The surviving rows must keep the identifiers they already had -- renumbering them would break "
            + "every reference held outside the database.");

        Exec(cmd, "INSERT INTO dbo.RebuildIdentityReuse (Val) VALUES ('d')");

        Assert.That(Int(cmd, "SELECT Id FROM dbo.RebuildIdentityReuse WHERE Val = 'd'"), Is.EqualTo(4),
            "The next insert after a rebuild must continue from the counter the ORIGINAL table had reached "
            + "(3), not from the largest surviving row (2). Getting 3 here is the silent defect: the value "
            + "was already issued once.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildIdentityReuse WHERE Id = 3"), Is.EqualTo(0),
            "Id 3 was handed out before the rebuild and must never be handed out again.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildIdentityReuse");
        conn.Close();
    }

    // ---- new column ---------------------------------------------------------

    [Test]
    public void Rebuild_NewDeclaredColumn_ArrivesDefaultedInsteadOfBreakingTheCopy()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildNewColumn;
CREATE TABLE dbo.RebuildNewColumn (Id INT NOT NULL, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildNewColumn (Id, Val) VALUES (1, 'one'), (2, 'two');");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildNewColumn]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true},
                    {"Name": "[Added]", "DataType": "INT", "Nullable": false, "Default": "42"},
                    {"Name": "[AlsoAdded]", "DataType": "NVARCHAR(20)", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildNewColumn");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildNewColumn"), Is.EqualTo(2),
            "A column the live table does not have must not appear in the SELECT list -- if it did, the "
            + "copy would fail outright and take the rows with it.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildNewColumn WHERE Added = 42"), Is.EqualTo(2),
            "A new NOT NULL column with a DEFAULT must arrive at that default on every carried-over row.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildNewColumn WHERE AlsoAdded IS NULL"), Is.EqualTo(2),
            "A new nullable column with no default must arrive NULL rather than blocking the copy.");
        Assert.That(Strings(cmd, "SELECT Val FROM dbo.RebuildNewColumn ORDER BY Id"),
            Is.EqualTo(new List<string> { "one", "two" }),
            "The pre-existing data must be unchanged by the arrival of new columns.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildNewColumn");
        conn.Close();
    }

    // ---- removed column -----------------------------------------------------

    [Test]
    public void Rebuild_ColumnRemovedFromTheDefinition_IsGoneAfterwards()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildDropColumn;
CREATE TABLE dbo.RebuildDropColumn (Id INT NOT NULL, Keep NVARCHAR(50) NULL, Obsolete NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildDropColumn (Id, Keep, Obsolete) VALUES (1, 'kept', 'junk'), (2, 'also kept', 'junk');");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'RebuildDropColumn' AND COLUMN_NAME = 'Obsolete'"),
            Is.EqualTo(1), "Setup precondition: the column being removed must exist beforehand.");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildDropColumn]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Keep]", "DataType": "NVARCHAR(50)", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildDropColumn");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'RebuildDropColumn' AND COLUMN_NAME = 'Obsolete'"),
            Is.EqualTo(0),
            "A column the package no longer declares must not survive the rebuild -- it appears in neither "
            + "the shadow's CREATE nor the copy.");
        Assert.That(Strings(cmd, "SELECT Keep FROM dbo.RebuildDropColumn ORDER BY Id"),
            Is.EqualTo(new List<string> { "kept", "also kept" }),
            "Dropping one column must not disturb the columns either side of it.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildDropColumn");
        conn.Close();
    }

    // ---- declared column order ---------------------------------------------

    [Test]
    public void Rebuild_HonoursDeclaredColumnOrder()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildColumnOrder;
CREATE TABLE dbo.RebuildColumnOrder (Alpha INT NULL, Bravo INT NULL, Charlie INT NULL);
INSERT INTO dbo.RebuildColumnOrder (Alpha, Bravo, Charlie) VALUES (1, 2, 3);");

        Assert.That(Strings(cmd, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'RebuildColumnOrder' ORDER BY ORDINAL_POSITION"),
            Is.EqualTo(new List<string> { "Alpha", "Bravo", "Charlie" }),
            "Setup precondition: the deployed order must START different from the declared order, or the "
            + "order assertion below would hold no matter what the procedure did.");

        // Declared in a deliberately different order from the deployed table.
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildColumnOrder]",
                "Columns": [
                    {"Name": "[Charlie]", "DataType": "INT", "Nullable": true},
                    {"Name": "[Alpha]", "DataType": "INT", "Nullable": true},
                    {"Name": "[Bravo]", "DataType": "INT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildColumnOrder");

        Assert.That(Strings(cmd, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'RebuildColumnOrder' ORDER BY ORDINAL_POSITION"),
            Is.EqualTo(new List<string> { "Charlie", "Alpha", "Bravo" }),
            "The rebuilt table must be laid out in the order the package file declares -- putting the "
            + "deployed order back is the one thing an in-place ALTER can never do.");

        Assert.That(Strings(cmd, "SELECT CONVERT(NVARCHAR(20), Alpha) + '/' + CONVERT(NVARCHAR(20), Bravo) + '/' + CONVERT(NVARCHAR(20), Charlie) FROM dbo.RebuildColumnOrder"),
            Is.EqualTo(new List<string> { "1/2/3" }),
            "Reordering the columns must move the DATA with them. A positional copy would land 3/1/2 here "
            + "and still report the right column order.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildColumnOrder");
        conn.Close();
    }

    // ---- inbound foreign key ------------------------------------------------

    [Test]
    public void Rebuild_InboundForeignKey_DoesNotBlockTheRebuild_AndTheChildSurvives()
    {
        // Proven live on all four engines: sp_rename of the parent SUCCEEDS and the inbound FK FOLLOWS it
        // onto the renamed-away table, so after a naive swap the child would be constrained against the
        // wrong table. The drop then failing is merely what surfaces that. The engine drops inbound keys
        // before the swap and leaves the re-add to the owning table's foreign-key pass.
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
IF OBJECT_ID('dbo.RebuildFkChild', 'U') IS NOT NULL DROP TABLE dbo.RebuildFkChild;
IF OBJECT_ID('dbo.RebuildFkParent', 'U') IS NOT NULL DROP TABLE dbo.RebuildFkParent;
CREATE TABLE dbo.RebuildFkParent (Id INT NOT NULL CONSTRAINT PK_RebuildFkParent PRIMARY KEY, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildFkParent (Id, Val) VALUES (1, 'p1'), (2, 'p2');
CREATE TABLE dbo.RebuildFkChild (Id INT NOT NULL, ParentId INT NOT NULL,
  CONSTRAINT FK_RebuildFkChild_Parent FOREIGN KEY (ParentId) REFERENCES dbo.RebuildFkParent (Id));
INSERT INTO dbo.RebuildFkChild (Id, ParentId) VALUES (10, 1), (11, 2);");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID('dbo.RebuildFkParent')"),
            Is.EqualTo(1), "Setup precondition: an inbound foreign key must exist, or this test proves nothing.");

        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildFkParent]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildFkParent");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildFkParent"), Is.EqualTo(2),
            "The parent's rows must survive a rebuild it was only blocked from by an inbound key.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildFkChild"), Is.EqualTo(2),
            "The child table and its rows are not this rebuild's business and must be untouched.");

        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildFkParent_SchemaSmithOld')"), Is.EqualTo(-1),
            "The renamed-out original must be gone. If the inbound key had not been dropped first, the DROP "
            + "would have failed and this table would still be sitting there -- with the child pointing at it.");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('dbo.RebuildFkChild')"),
            Is.EqualTo(0),
            "The inbound key is deliberately NOT re-added here: it is declared in the CHILD's JSON, so the "
            + "child's foreign-key quench pass re-creates it. What must never happen is the key surviving "
            + "pointed at the renamed-away table.");

        Exec(cmd, @"
IF OBJECT_ID('dbo.RebuildFkChild', 'U') IS NOT NULL DROP TABLE dbo.RebuildFkChild;
IF OBJECT_ID('dbo.RebuildFkParent', 'U') IS NOT NULL DROP TABLE dbo.RebuildFkParent;");
        conn.Close();
    }

    // ---- refusal ------------------------------------------------------------

    // A shadow copy is an ordinary table, so rebuilding a ledger table would leave its ledger behind. An updatable ledger
    // table keeps its history, but sys.tables does not report it as temporal, so the temporal refusal never met it. Its
    // own database: a dropped ledger table stays behind in the catalog.
    [TestCase("LEDGER = ON (APPEND_ONLY = ON)", "APPEND_ONLY_LEDGER_TABLE")]
    [TestCase("SYSTEM_VERSIONING = ON, LEDGER = ON", "UPDATABLE_LEDGER_TABLE")]
    public void Rebuild_OfALedgerTable_IsRefused(string tableOptions, string ledgerType)
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        if (Int(cmd, "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))") < 16) Assert.Ignore("Ledger tables are SQL Server 2022.");
        var db = $"RebuildLedger_{Guid.NewGuid():N}"[..30];
        Exec(cmd, $"CREATE DATABASE [{db}]");
        try
        {
            conn.ChangeDatabase(db);
            Schema.Utility.ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
            Exec(cmd, $"CREATE TABLE dbo.Ledgered (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL) WITH ({tableOptions})");
            Exec(cmd, "INSERT INTO dbo.Ledgered (Id, Val) VALUES (1, 'one'), (2, 'two')");
            var json = """
                {
                    "Schema": "[dbo]",
                    "Name": "[Ledgered]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
                    ]
                }
                """;

            var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => Rebuild(cmd, json, "Ledgered"));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("the table is a ledger table").And.Contain("Ledgered"), "the refusal names why and which table");
                Assert.That(Scalar(cmd, "SELECT ledger_type_desc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.Ledgered')")?.ToString(),
                    Is.EqualTo(ledgerType), "the table is still the ledger table it was");
                Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.Ledgered"), Is.EqualTo(2), "with its rows");
                Assert.That(Int(cmd, "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'Ledgered[_]SchemaSmith%'"), Is.Zero, "and no shadow or old copy");
            });
        }
        finally
        {
            conn.ChangeDatabase("master");
            Exec(cmd, $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]");
        }
    }

    [Test]
    public void Rebuild_CdcTrackedTable_IsRefused_AndTheErrorNamesTheReasonAndTheTable()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildCdcBlocked;
CREATE TABLE dbo.RebuildCdcBlocked (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildCdcBlocked (Id, Val) VALUES (1, 'one'), (2, 'two');");
        ExecWithDeadlockRetry(cmd,
            "EXEC sys.sp_cdc_enable_table @source_schema = N'dbo', @source_name = N'RebuildCdcBlocked', @role_name = NULL");

        try
        {
            Assert.That(Scalar(cmd, "SELECT is_tracked_by_cdc FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.RebuildCdcBlocked')"),
                Is.EqualTo(true), "Setup precondition: CDC must actually be on, or the refusal below is not being tested.");

            var json = """
                {
                    "Schema": "[dbo]",
                    "Name": "[RebuildCdcBlocked]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                        {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
                    ]
                }
                """;

            var ex = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => Rebuild(cmd, json, "RebuildCdcBlocked"));

            // Asserted non-null first so a failure names the missing message rather than reporting a
            // collection mismatch against null.
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex!.Message, Does.Contain("Change Data Capture"),
                $"The refusal must name the blocking state. 'This table cannot be rebuilt' leaves the "
                + $"operator no way to know what to disable or migrate. Got: '{ex.Message}'.");
            Assert.That(ex.Message, Does.Contain("RebuildCdcBlocked"),
                $"The refusal must name the table, since a deploy touches many. Got: '{ex.Message}'.");

            Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildCdcBlocked"), Is.EqualTo(2),
                "The refusal must fire BEFORE any DDL -- the table and its rows must be exactly as they were.");
            Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildCdcBlocked_SchemaSmithRebuild')"), Is.EqualTo(-1),
                "No shadow table may exist after a refusal; a half-built rebuild is worse than none.");

            // A preview that hid the refusal would tell the operator a rebuild is available on a table
            // where it can never be, so the guard has to fire in WhatIf too.
            var whatIfEx = Assert.Throws<Microsoft.Data.SqlClient.SqlException>(() => Rebuild(cmd, json, "RebuildCdcBlocked", whatIf: true));
            Assert.That(whatIfEx, Is.Not.Null);
            Assert.That(whatIfEx!.Message, Does.Contain("Change Data Capture"),
                $"WhatIf must surface the impossibility rather than printing a rebuild that could never run. "
                + $"Got: '{whatIfEx.Message}'.");
        }
        finally
        {
            ExecWithDeadlockRetry(cmd,
                "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'RebuildCdcBlocked', @capture_instance = N'dbo_RebuildCdcBlocked'");
            Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildCdcBlocked");
            conn.Close();
        }
    }

    // ---- WhatIf -------------------------------------------------------------

    [Test]
    public void Rebuild_WhatIf_ChangesNothing_AndEmitsWouldRebuild()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();

        Exec(cmd, @"
DROP TABLE IF EXISTS dbo.RebuildWhatIf;
CREATE TABLE dbo.RebuildWhatIf (Id INT NOT NULL, Val NVARCHAR(50) NULL);
INSERT INTO dbo.RebuildWhatIf (Id, Val) VALUES (1, 'one'), (2, 'two');
DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[RebuildWhatIf]';");

        var objectIdBefore = Int(cmd, "SELECT OBJECT_ID('dbo.RebuildWhatIf')");

        // Declares a change (a reordered, extended column set) so the preview has something real to
        // describe -- a WhatIf over a table that already matches would leave nothing to change anyway.
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildWhatIf]",
                "Columns": [
                    {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true},
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Added]", "DataType": "INT", "Nullable": true}
                ]
            }
            """;
        Rebuild(cmd, json, "RebuildWhatIf", whatIf: true);

        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildWhatIf')"), Is.EqualTo(objectIdBefore),
            "WhatIf must not replace the table.");
        Assert.That(Strings(cmd, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'RebuildWhatIf' ORDER BY ORDINAL_POSITION"),
            Is.EqualTo(new List<string> { "Id", "Val" }),
            "WhatIf must not add the declared column or reorder the deployed ones -- the shape must be "
            + "exactly what it was. This is what makes the preview safe to run against production.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM dbo.RebuildWhatIf"), Is.EqualTo(2),
            "WhatIf must not move any rows.");
        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildWhatIf_SchemaSmithRebuild')"), Is.EqualTo(-1),
            "WhatIf must not create the shadow table.");

        Assert.That(Int(cmd, "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[RebuildWhatIf]' AND ActionType = 'wouldRebuild'"),
            Is.EqualTo(1),
            "The preview has to be visible in the run manifest, or a WhatIf report cannot show that a "
            + "rebuild was going to happen at all.");
        Assert.That(Int(cmd, "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[RebuildWhatIf]' AND ActionType = 'rebuilt'"),
            Is.EqualTo(0),
            "A preview must never be recorded as work that happened.");

        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildWhatIf");
        conn.Close();
    }

    // ---- permissions and extended properties survive ----------------------

    private static List<string> GrantsFor(IDbCommand cmd, string table, string user) => Strings(cmd, $@"
SELECT p.state_desc COLLATE DATABASE_DEFAULT + ' ' + p.permission_name COLLATE DATABASE_DEFAULT + ISNULL(' (' + c.name COLLATE DATABASE_DEFAULT + ')', '') + ' TO ' + USER_NAME(p.grantee_principal_id) COLLATE DATABASE_DEFAULT
  FROM sys.database_permissions p
  LEFT JOIN sys.columns c ON c.object_id = p.major_id AND c.column_id = p.minor_id
 WHERE p.class = 1 AND p.major_id = OBJECT_ID('dbo.{table}') AND p.grantee_principal_id = USER_ID('{user}')
 ORDER BY 1");

    private static List<string> ExtendedPropertiesOf(IDbCommand cmd, string table) => Strings(cmd, $@"
SELECT ISNULL(c.name COLLATE DATABASE_DEFAULT + ':', '') + ep.name COLLATE DATABASE_DEFAULT + '=' + CONVERT(NVARCHAR(200), ep.value) COLLATE DATABASE_DEFAULT
  FROM sys.extended_properties ep
  LEFT JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
 WHERE ep.class = 1 AND ep.major_id = OBJECT_ID('dbo.{table}') AND ep.name <> 'SchemaSmith_Product'
 ORDER BY 1");

    private static int AuditRows(IDbCommand cmd, string table, string action) =>
        Int(cmd, $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[{table}]' AND ActionType = '{action}'");

    private static string TwoColumnJson(string table) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Val]", "DataType": "NVARCHAR(50)", "Nullable": true}
            ]
        }
        """;

    private static void CreateGrantedTable(IDbCommand cmd, string table)
    {
        Exec(cmd, $@"
IF USER_ID('rb_perm_user') IS NULL CREATE USER rb_perm_user WITHOUT LOGIN;
DROP TABLE IF EXISTS dbo.{table};
DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName = '[dbo].[{table}]';
CREATE TABLE dbo.{table} (Id INT NOT NULL, Val NVARCHAR(50) NULL);
INSERT INTO dbo.{table} (Id, Val) VALUES (1, 'one');
GRANT SELECT ON dbo.{table} TO rb_perm_user;
GRANT INSERT ON dbo.{table} TO rb_perm_user WITH GRANT OPTION;
DENY DELETE ON dbo.{table} TO rb_perm_user;
GRANT UPDATE ([Val]) ON dbo.{table} TO rb_perm_user;
EXEC sp_addextendedproperty 'MS_Description', 'kept table', 'SCHEMA', 'dbo', 'TABLE', '{table}';
EXEC sp_addextendedproperty 'MS_Description', 'kept column', 'SCHEMA', 'dbo', 'TABLE', '{table}', 'COLUMN', 'Val';");
    }

    [Test]
    public void Rebuild_KeepsPermissionsAndExtendedProperties_AndAuditsTheReapply()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        CreateGrantedTable(cmd, "RebuildKeepsGrants");

        var grantsBefore = GrantsFor(cmd, "RebuildKeepsGrants", "rb_perm_user");
        var propertiesBefore = ExtendedPropertiesOf(cmd, "RebuildKeepsGrants");
        Assert.Multiple(() =>
        {
            Assert.That(grantsBefore, Has.Count.EqualTo(4), "setup: the four permissions must be on the table before the rebuild, or the comparison below is vacuous");
            Assert.That(propertiesBefore, Has.Count.EqualTo(2), "setup: both extended properties must be on the table before the rebuild");
        });
        var objectIdBefore = Int(cmd, "SELECT OBJECT_ID('dbo.RebuildKeepsGrants')");

        Rebuild(cmd, TwoColumnJson("RebuildKeepsGrants"), "RebuildKeepsGrants");

        Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildKeepsGrants')"), Is.Not.EqualTo(objectIdBefore),
            "The table must actually have been replaced, or everything below passes on a procedure that did nothing.");
        Assert.Multiple(() =>
        {
            Assert.That(GrantsFor(cmd, "RebuildKeepsGrants", "rb_perm_user"), Is.EqualTo(grantsBefore),
                "sp_rename keeps a permission on the renamed-out original, so the replacement starts with none: every GRANT, "
                + "DENY, grant option and column permission has to be put back on it.");
            Assert.That(ExtendedPropertiesOf(cmd, "RebuildKeepsGrants"), Is.EqualTo(propertiesBefore),
                "Extended properties on the table and its columns are not in the package, so the rebuild is the only thing that can carry them.");
            Assert.That(AuditRows(cmd, "RebuildKeepsGrants", "reapplied"), Is.EqualTo(1),
                "The run manifest has to say the permissions were put back.");
        });
    }

    // The column's grant and description were put back on a replacement that no longer has the column: "Invalid column
    // name", the rebuild rolled back, and so did every deploy after it.
    [Test]
    public void Rebuild_RemovingAColumnThatCarriesAGrantAndADescription_Succeeds_AndKeepsTheTablesOwn()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        CreateGrantedTable(cmd, "RebuildDropsGrantedColumn");
        var json = """
            {
                "Schema": "[dbo]",
                "Name": "[RebuildDropsGrantedColumn]",
                "Columns": [ {"Name": "[Id]", "DataType": "INT", "Nullable": false} ]
            }
            """;

        Rebuild(cmd, json, "RebuildDropsGrantedColumn");

        Assert.Multiple(() =>
        {
            Assert.That(Int(cmd, "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.RebuildDropsGrantedColumn') AND [name] = 'Val'"), Is.Zero);
            Assert.That(GrantsFor(cmd, "RebuildDropsGrantedColumn", "rb_perm_user"), Has.Count.EqualTo(3), "the table's own GRANT, grant option and DENY are put back");
            Assert.That(ExtendedPropertiesOf(cmd, "RebuildDropsGrantedColumn"), Has.Count.EqualTo(1), "and its own description");
            Assert.That(AuditRows(cmd, "RebuildDropsGrantedColumn", "reapplied"), Is.EqualTo(1));
        });
        Exec(cmd, "DROP TABLE IF EXISTS dbo.RebuildDropsGrantedColumn");
    }

    [Test]
    public void Rebuild_WhatIf_ReportsThePermissionsItWouldReapply_AndTouchesNothing()
    {
        using var conn = OpenMainDb();
        using var cmd = conn.CreateCommand();
        CreateGrantedTable(cmd, "RebuildWhatIfGrants");
        var grantsBefore = GrantsFor(cmd, "RebuildWhatIfGrants", "rb_perm_user");
        var propertiesBefore = ExtendedPropertiesOf(cmd, "RebuildWhatIfGrants");
        var objectIdBefore = Int(cmd, "SELECT OBJECT_ID('dbo.RebuildWhatIfGrants')");

        Rebuild(cmd, TwoColumnJson("RebuildWhatIfGrants"), "RebuildWhatIfGrants", whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(Int(cmd, "SELECT OBJECT_ID('dbo.RebuildWhatIfGrants')"), Is.EqualTo(objectIdBefore), "WhatIf must not rebuild");
            Assert.That(GrantsFor(cmd, "RebuildWhatIfGrants", "rb_perm_user"), Is.EqualTo(grantsBefore), "WhatIf must not touch a permission");
            Assert.That(ExtendedPropertiesOf(cmd, "RebuildWhatIfGrants"), Is.EqualTo(propertiesBefore), "WhatIf must not touch an extended property");
            Assert.That(AuditRows(cmd, "RebuildWhatIfGrants", "wouldReapply"), Is.EqualTo(1),
                "The preview lists the permissions the real run would put back, from the same capture.");
            Assert.That(AuditRows(cmd, "RebuildWhatIfGrants", "reapplied"), Is.EqualTo(0), "WhatIf records nothing as done");
        });
    }
}
