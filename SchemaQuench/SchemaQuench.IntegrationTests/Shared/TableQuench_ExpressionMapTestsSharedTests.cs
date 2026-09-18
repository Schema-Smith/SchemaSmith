// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Expression false-change detection (#242) for the MySQL/MariaDB family — the twin of the SQL Server and
/// PostgreSQL fixtures, run on both engines by the two bindings.
/// <para><b>Authored in natural form.</b> MySQL reformats what it stores: an authored <c>`Id` > 100</c> comes
/// back from the catalog as <c>(`Id` > 100)</c>, and a generated column's expression comes back with the
/// engine's own spacing and backticking. A test authored in the engine's canonical form proves nothing.</para>
/// <para><b>How a re-apply is detected.</b> These engines give a constraint no stable identity to watch — no
/// object_id, no oid — so the arbiter is the deploy's own status log: a converged table must emit NO check or
/// column DDL on the second and third passes. That is the same signal the other idempotency fixtures here use.</para>
/// <para><b>Generated columns had no idempotency coverage at all</b> before this, which is exactly why nothing
/// noticed them re-applying.</para>
/// </summary>
public abstract class TableQuench_ExpressionMapTestsSharedTests : BaseTableQuenchTests
{
    private static string CheckJson(string table, string constraintName, string expression) => $$"""
        [
        {
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
            ],
            "CheckConstraints": [
                { "Name": "{{constraintName}}", "Expression": "{{expression}}" }
            ]
        }
        ]
        """;

    private static string GeneratedJson(string table, string expression, bool declareNullable = true) => $$"""
        [
        {
            "Name": "{{table}}",
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Tag", "DataType": "VARCHAR(50)", "Nullable": true },
                { "Name": "Label", "DataType": "VARCHAR(60)",{{(declareNullable ? " \"Nullable\": true," : "")}} "GenerationExpression": "{{expression}}", "Generated": "STORED" }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
            ]
        }
        ]
        """;

    private int CountMessages(IDbCommand cmd, string messagePrefix, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith_StatusMessages
                              WHERE SessionId = CONNECTION_ID()
                                AND Message LIKE '%{messagePrefix}%'
                                AND Message LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void ClearMessages(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private string LiveCheckClause(IDbCommand cmd, string constraintName)
    {
        cmd.CommandText = $@"SELECT IFNULL((SELECT CONVERT(cc.CHECK_CLAUSE USING utf8mb4)
                                              FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
                                             WHERE cc.CONSTRAINT_SCHEMA = '{_mainDb}'
                                               AND cc.CONSTRAINT_NAME = '{constraintName}'), '')";
        return cmd.ExecuteScalar() as string;
    }

    private string LiveGenerationExpression(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT IFNULL((SELECT GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS
                                             WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'
                                               AND COLUMN_NAME = 'Label'), '')";
        return cmd.ExecuteScalar() as string;
    }

    private void Cleanup(IDbCommand cmd, string table)
    {
        try
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM SchemaSmith_ExpressionMap WHERE ObjectTable = '{table}'";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectSchema = '{_mainDb}' AND ObjectName LIKE '{table}%'";
            cmd.ExecuteNonQuery();
            ClearMessages(cmd);
        }
        catch (System.Data.Common.DbException) { /* best-effort cleanup */ }
    }

    private void WithConnection(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try { body(cmd); }
        finally { Cleanup(cmd, table); conn.Close(); }
    }

    [Test]
    public void ACheckAuthoredInNaturalForm_EmitsNoDdlOnARepeatDeploy()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        var table = $"ExprMapChk_{Guid.NewGuid():N}"[..20];
        var ck = $"CK_{table}_Id";
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            var json = CheckJson(table, ck, "`Id` > 100");
            RunTableQuenchProc(cmd, json);
            Assert.That(LiveCheckClause(cmd, ck), Is.Not.Empty, "setup: the check must exist after the first deploy");
            ClearMessages(cmd);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(CountMessages(cmd, "heck constraint", table), Is.Zero,
                    $"pass {pass}: check DDL was emitted for an unchanged declaration");
            }
        });
    }

    // declareNullable: false measures a generated column authored without "Nullable" -- on SQL Server that case
    // churned on every version because the create path and the comparison read the omission differently.
    [TestCase(true)]
    [TestCase(false)]
    public void AGeneratedColumnAuthoredInNaturalForm_EmitsNoDdlOnARepeatDeploy(bool declareNullable)
    {
        var table = $"ExprMapGen_{Guid.NewGuid():N}"[..20];
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, `Tag` VARCHAR(50) NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            // Natural form: spaces around the operator, no engine backticking of the function call.
            var json = GeneratedJson(table, "concat(`Tag`, 'x')", declareNullable);
            RunTableQuenchProc(cmd, json);
            var live = LiveGenerationExpression(cmd, table);
            Assert.That(live, Is.Not.Empty, "setup: the generated column must exist after the first deploy");
            ClearMessages(cmd);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(CountMessages(cmd, "olumn", table), Is.Zero,
                    $"pass {pass}: column DDL was emitted for an unchanged generated-column declaration "
                    + $"(authored 'concat(`Tag`, ''x'')', engine reports '{live}')");
            }
        });
    }

    // The other half: a hand-edited constraint must still be re-applied. A metadata-only compare would go
    // quiet here, which is worse than the churn it removes.
    [Test]
    public void AnOutOfBandEdit_IsStillReApplied()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        var table = $"ExprMapDrift_{Guid.NewGuid():N}"[..20];
        var ck = $"CK_{table}_Id";
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            var json = CheckJson(table, ck, "`Id` > 100");
            RunTableQuenchProc(cmd, json);

            // MariaDB rejects DROP CHECK; it spells the same operation DROP CONSTRAINT.
            var dropCheck = Platform == Platform.MariaDb ? "DROP CONSTRAINT" : "DROP CHECK";
            cmd.CommandText = $@"ALTER TABLE `{_mainDb}`.`{table}` {dropCheck} `{ck}`;
                                 ALTER TABLE `{_mainDb}`.`{table}` ADD CONSTRAINT `{ck}` CHECK (`Id` > 999);";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, json);

            Assert.That(LiveCheckClause(cmd, ck), Does.Contain("100").And.Not.Contain("999"),
                "a hand-edited check must be re-applied, not silently accepted");
        });
    }

    [Test]
    public void AChangedDeclaration_IsStillApplied()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        var table = $"ExprMapChg_{Guid.NewGuid():N}"[..20];
        var ck = $"CK_{table}_Id";
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, CheckJson(table, ck, "`Id` > 100"));
            RunTableQuenchProc(cmd, CheckJson(table, ck, "`Id` > 500"));

            Assert.That(LiveCheckClause(cmd, ck), Does.Contain("500"),
                "an edited declaration must reach the server");
        });
    }

    [Test]
    public void ApplyingAnExpression_RecordsWhatWasApplied()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        var table = $"ExprMapRec_{Guid.NewGuid():N}"[..20];
        var ck = $"CK_{table}_Id";
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, CheckJson(table, ck, "`Id` > 100"));

            cmd.CommandText = $@"SELECT CONCAT(AuthoredText, '|', CanonicalText) FROM SchemaSmith_ExpressionMap
                                  WHERE ObjectTable = '{table}' AND ObjectKind = 'CHECK'";
            var row = cmd.ExecuteScalar() as string;
            Assert.That(row, Is.Not.Null, "the applied expression must be recorded");
            Assert.That(row, Does.StartWith("`Id` > 100|"), "the authored text is stored as written: " + row);
        });
    }

    // SQL Server and PostgreSQL both elected a just-renamed index for dropping when DropUnknownIndexes was on:
    // it was still in the pre-rename snapshot under its old name. MySQL/MariaDB already clear renamed names
    // from their detection snapshots -- this proves it rather than trusting the comment that says so.
    [Test]
    public void RenamingAnIndex_WithDropUnknownIndexes_DoesNotDropIt()
    {
        var table = $"ExprMapRen_{Guid.NewGuid():N}"[..20];
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, `Tag` VARCHAR(50) NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            string Json(string indexName) => $$"""
                [{ "Name": "{{table}}",
                   "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "Tag", "DataType": "VARCHAR(50)", "Nullable": true } ],
                   "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" },
                                { "Name": "{{indexName}}", "IndexColumns": "Tag" } ] }]
                """;

            cmd.CommandText = $"CALL SchemaSmith_TableQuench('{_productName}', '{_mainDb}', '{Json("IX_Tag_Old").Replace("'", "''")}', 0, 1, 0)";
            cmd.ExecuteNonQuery();
            ClearMessages(cmd);

            cmd.CommandText = $"CALL SchemaSmith_TableQuench('{_productName}', '{_mainDb}', '{Json("IX_Tag_New").Replace("'", "''")}', 0, 1, 0)";
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT GROUP_CONCAT(DISTINCT INDEX_NAME ORDER BY INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS
                                  WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}'";
            Assert.Multiple(() =>
            {
                Assert.That(cmd.ExecuteScalar() as string, Is.EqualTo("IX_Tag_New,PRIMARY"), "the index must exist under its new name only");
                Assert.That(CountMessages(cmd, "Drop", table), Is.Zero, "no drop may be emitted for a renamed index");
                Assert.That(CountMessages(cmd, "Rename index", table), Is.EqualTo(1),
                    "it must have been a RENAME -- a drop-and-recreate also ends with the index under its new name");
            });
        });
    }

    // The SQL Server and PostgreSQL fixtures both pin this; the MySQL family did not. A row written under a
    // different server version cannot vouch for today's canonical text: stale, not wrong. The column must be left
    // alone, the row rewritten with the current version, and the re-baseline reported.
    [Test]
    public void AStaleContextRow_IsReBaselined_WithoutTouchingTheObject_AndIsReported()
    {
        var table = $"ExprMapCtx_{Guid.NewGuid():N}"[..20];
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, `Tag` VARCHAR(50) NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            var json = GeneratedJson(table, "concat(`Tag`, 'x')");
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $@"UPDATE SchemaSmith_ExpressionMap
                                    SET EngineVersion = 'from-another-server'
                                  WHERE ObjectTable = '{table}'";
            Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist to go stale (only the CONTEXT goes stale -- the recorded canonical text still matches the live object, which is what a real engine upgrade looks like)");

            ClearMessages(cmd);
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_ExpressionMap WHERE ObjectTable = '{table}' AND EngineVersion = 'from-another-server'";
            var staleLeft = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(CountMessages(cmd, "olumn", table), Is.Zero, "a stale context must re-baseline, never re-apply");
                Assert.That(staleLeft, Is.Zero, "the stale row must be rewritten with the current server version");
                Assert.That(CountMessages(cmd, "Re-baselined 1 recorded expression", ""), Is.EqualTo(1),
                    "the re-baseline must be reported in the deploy log, not happen silently");
            });
        });
    }

    // Stale context AND drift together: VERSION() moves on any server upgrade, and that must never stop the
    // comparison reading the live object.
    [Test]
    public void AStaleContextDoesNotExcuseDrift_TheObjectIsStillReApplied()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");

        var table = $"ExprMapCtxDrift_{Guid.NewGuid():N}"[..24];
        var ck = $"CK_{table}_Id";
        WithConnection(table, cmd =>
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
                                 CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));";
            cmd.ExecuteNonQuery();

            var json = CheckJson(table, ck, "`Id` > 100");
            RunTableQuenchProc(cmd, json);

            var dropCheck = Platform == Platform.MariaDb ? "DROP CONSTRAINT" : "DROP CHECK";
            cmd.CommandText = $@"ALTER TABLE `{_mainDb}`.`{table}` {dropCheck} `{ck}`;
                                 ALTER TABLE `{_mainDb}`.`{table}` ADD CONSTRAINT `{ck}` CHECK (`Id` > 999);";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"UPDATE SchemaSmith_ExpressionMap SET EngineVersion = 'from-another-server' WHERE ObjectTable = '{table}'";
            Assert.That(cmd.ExecuteNonQuery(), Is.GreaterThan(0), "setup: a mapping row must exist");

            RunTableQuenchProc(cmd, json);

            Assert.That(LiveCheckClause(cmd, ck), Does.Contain("100").And.Not.Contain("999"),
                "a version change must not excuse drift -- the declared expression must be re-applied");
        });
    }
}
