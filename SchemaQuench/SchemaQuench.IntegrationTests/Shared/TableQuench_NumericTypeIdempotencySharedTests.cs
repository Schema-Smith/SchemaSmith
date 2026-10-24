// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Regression coverage for the numeric/decimal whitespace + DECIMAL/NUMERIC synonym
/// false-change bug. ModifiedTableQuench compared INFORMATION_SCHEMA.COLUMNS.COLUMN_TYPE
/// against the authored DataType with a naive UPPER(...) compare and no whitespace
/// normalization. MySQL's canonical COLUMN_TYPE is e.g. "decimal(10,2)" (no space, and
/// "decimal" not "numeric"), so a hand-authored "numeric(10, 2)" / "DECIMAL(10, 2)" was
/// wrongly classed as "modified" on EVERY quench and re-issued a spurious MODIFY COLUMN.
///
/// MySQL has no stable per-PK OID to watch (and the spurious MODIFY does not drop the PK),
/// so the reliable no-rebuild signal here is the procedure's own status log: a real column
/// modification emits a "  Modify column: ..." message into SchemaSmith_StatusMessages.
/// We converge once with the full TableQuench, then drive ModifiedTableQuench directly on a
/// clean status log and assert it emits NO modify message for our table. The type is also
/// asserted byte-identical before/after as a defense-in-depth end-state check.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_NumericTypeIdempotencySharedTests : BaseTableQuenchTests
{
    // declared form differs from canonical "decimal(10,2)" only by whitespace / synonym.
    [TestCase("decimal(10,2)", TestName = "TableQuench_DecimalNoSpaceIsIdempotent")]
    [TestCase("decimal(10, 2)", TestName = "TableQuench_DecimalSpacedIsIdempotent")]
    [TestCase("NUMERIC(10,2)", TestName = "TableQuench_NumericSynonymIsIdempotent")]
    [TestCase("NUMERIC(10, 2)", TestName = "TableQuench_NumericSynonymSpacedIsIdempotent")]
    public void TableQuench_NumericTypeSpellingIsIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"NumericIdem_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Build the table directly so its canonical COLUMN_TYPE is MySQL's "decimal(10,2)".
        cmd.CommandText = $@"
DROP TABLE IF EXISTS `{_mainDb}`.`{tableName}`;
CREATE TABLE `{_mainDb}`.`{tableName}` (
    `Id` INT NOT NULL,
    `Amount` DECIMAL(10,2) NOT NULL,
    PRIMARY KEY (`Id`)
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
[
{
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",     "DataType": "INT",            "Nullable": false },
        { "Name": "Amount", "DataType": "{{declaredType}}", "Nullable": false }
    ],
    "Indexes": [
        { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
    ]
}
]
""";

        try
        {
            // Converge once + register product ownership. With the bug present this first
            // run may still issue a phantom MODIFY; we only assert stability afterward.
            RunTableQuenchProc(cmd, json);

            var typeBefore = GetRawColumnType(cmd, tableName, "Amount");

            // Clean status log, then drive ModifiedTableQuench directly so we can observe
            // whether a phantom "Modify column" is emitted on a converged (no-op) table.
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();

            cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{_mainDb}', '{json.Replace("'", "''")}')";
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
            // Trailing 0, 0 are DropUnknownIndexes and DropIndexesRemovedFromProduct. Index removal
            // lives in this procedure now; nothing here recreates an index, so both stay off.
            cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{_mainDb}', 0, 0, 1, 1, 1, 1, 0, 0, 0)";
            TransientRetry.Run(() => cmd.ExecuteNonQuery());

            cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith_StatusMessages
 WHERE SessionId = CONNECTION_ID()
   AND Message LIKE '%Modify column:%'
   AND Message LIKE '%{tableName}%'";
            var modifyMessages = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.That(modifyMessages, Is.EqualTo(0),
                $"Re-quench of '{declaredType}' (canonically decimal(10,2)) must NOT emit a MODIFY COLUMN. " +
                "A 'Modify column' status message means the type comparison produced a phantom modification.");

            var typeAfter = GetRawColumnType(cmd, tableName, "Amount");
            Assert.That(typeAfter, Is.EqualTo(typeBefore),
                "The stored column type must be unchanged across the no-op re-quench.");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{tableName}`";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private string GetRawColumnType(System.Data.IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS
 WHERE TABLE_SCHEMA = '{_mainDb}'
   AND TABLE_NAME = '{tableName}'
   AND COLUMN_NAME = '{columnName}'";
        return cmd.ExecuteScalar()?.ToString() ?? "UNKNOWN";
    }
}
