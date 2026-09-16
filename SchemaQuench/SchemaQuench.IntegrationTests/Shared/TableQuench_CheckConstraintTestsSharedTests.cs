// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Table-level CHECK constraint coverage on MySQL/MariaDB, including the Bug 2 gap where a MODIFIED
/// table-level check was never re-applied. Parity with the SQL Server + PostgreSQL behavior (#313).
/// <para>The column-level <c>CheckExpression</c> alias these tests once also covered is RETIRED (2.7.0) --
/// the engines cannot round-trip a column-level check, so table-level is the only authoring form.</para>
/// Idempotency is the load-bearing assertion: MySQL reformats CHECK_CLAUSE on storage (an authored
/// "`Id` &gt; 100" comes back as "(`Id` &gt; 100)"), so a naive desired-vs-stored text compare would
/// phantom-drop/recreate every run. The proc normalizes both sides before comparing; the no-op
/// re-quench tests below are the arbiter and assert NO drop/create check DDL is emitted on a
/// converged table (observed via the SchemaSmith_StatusMessages status log, MySQL's no-rebuild signal).
/// </summary>
[Category("Integration")]
public abstract class TableQuench_CheckConstraintTestsSharedTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_TableLevelCheck_ModifiedIsReApplied()
    {
        if (!TargetSupportsCheckConstraints())
            Assert.Ignore("CHECK constraints require MySQL 8.0.16; skipped below the floor.");
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"TblChkMod_{id}";
        var ck = $"CK_{table}_Id";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Existing table-level check enforces `Id` > 0.
            cmd.CommandText = $@"
DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));
ALTER TABLE `{_mainDb}`.`{table}` ADD CONSTRAINT `{ck}` CHECK (`Id` > 0);
INSERT INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{_mainDb}', 'TABLE', '{table}');
INSERT INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{_mainDb}', 'CHECK CONSTRAINT', '{table}.{ck}');";
            cmd.ExecuteNonQuery();

            // Desired table-level check is semantically different: `Id` > 100.
            RunTableQuenchProc(cmd, TableCheckJson(table, ck, "`Id` > 100"));

            // The NEW clause must be enforced (semantically distinct from the old `Id` > 0):
            // Id = 50 satisfied the old check but must be rejected by the new one.
            cmd.CommandText = $"INSERT INTO `{_mainDb}`.`{table}` (`Id`) VALUES (50)";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception,
                "After modifying the table-level check to Id > 100, an Id of 50 must be rejected.");
        }
        finally
        {
            Cleanup(cmd, table);
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_TableLevelCheck_IsIdempotent()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var table = $"TblChkIdem_{id}";
        var ck = $"CK_{table}_Id";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = $@"
DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;
CREATE TABLE `{_mainDb}`.`{table}` (`Id` INT NOT NULL, PRIMARY KEY (`Id`));
ALTER TABLE `{_mainDb}`.`{table}` ADD CONSTRAINT `{ck}` CHECK (`Id` > 0);
INSERT INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{_mainDb}', 'TABLE', '{table}');
INSERT INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
VALUES ('{_productName}', '', '{_mainDb}', 'CHECK CONSTRAINT', '{table}.{ck}');";
            cmd.ExecuteNonQuery();

            // Author the SAME expression that is already live (`Id` > 0).
            var json = TableCheckJson(table, ck, "`Id` > 0");
            RunTableQuenchProc(cmd, json);

            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            ReQuenchChecks(cmd, json);

            Assert.Multiple(() =>
            {
                Assert.That(CountMessages(cmd, "Drop modified check constraint:", table), Is.EqualTo(0),
                    "Converged table-level check must NOT be phantom-dropped (normalization failed).");
                Assert.That(CountMessages(cmd, "Create check constraint:", table), Is.EqualTo(0),
                    "Converged table-level check must NOT be re-created.");
            });
        }
        finally
        {
            Cleanup(cmd, table);
        }
        conn.Close();
    }


    private static int CountMessages(System.Data.IDbCommand cmd, string messagePrefix, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith_StatusMessages
 WHERE SessionId = CONNECTION_ID()
   AND Message LIKE '%{messagePrefix}%'
   AND Message LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string TableCheckJson(string table, string constraintName, string expression) => $$"""
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

    // Re-runs only the two procs that handle check constraints (parse + modify + create) so the
    // status log isolates check DDL. Mirrors the converge-once-then-drive-directly idempotency pattern.
    private void ReQuenchChecks(System.Data.IDbCommand cmd, string json)
    {
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{_mainDb}', '{json.Replace("'", "''")}')";
        cmd.ExecuteNonQuery();
        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below,
        // which keeps DropCheckConstraintsRemovedFromProduct (its 4th and last argument).
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{_mainDb}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{_mainDb}', 0, 1)";
        cmd.ExecuteNonQuery();
    }


    private void Cleanup(System.Data.IDbCommand cmd, string table)
    {
        try
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectSchema = '{_mainDb}' AND ObjectName LIKE '{table}%'";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
        }
        catch (System.Data.Common.DbException) { /* best-effort cleanup */ }
    }
}
