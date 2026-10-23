// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Parity regression coverage for the string-literal-default false-change bug (the engine-specific
/// fix lives in PostgreSQL, whose catalog stores varchar defaults as "'x'::character varying").
/// MySQL's INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT reports a string default WITHOUT surrounding
/// quotes ("Standard"), which ModifiedTableQuench already reconciles against the authored "'Standard'"
/// form, so a string-literal default is expected to be idempotent here. These cases lock that in so a
/// future change can't regress MySQL into the same phantom-change class.
///
/// MySQL has no stable per-PK OID to watch, so the reliable no-rebuild signal here is the procedure's
/// own status log: a real column modification emits a "  Modify column: ..." message into
/// SchemaSmith_StatusMessages. We converge once with the full TableQuench, then drive
/// ModifiedTableQuench directly on a clean status log and assert it emits NO modify message.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_DefaultIdempotencySharedTests : BaseTableQuenchTests
{
    [TestCase("'Standard'", TestName = "TableQuench_StringDefaultIsIdempotent")]
    public void TableQuench_StringLiteralDefaultIsIdempotent(string declaredDefault)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"DefaultIdem_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = $@"
DROP TABLE IF EXISTS `{_mainDb}`.`{tableName}`;
CREATE TABLE `{_mainDb}`.`{tableName}` (
    `Id`   INT NOT NULL,
    `Tier` VARCHAR(20) NOT NULL DEFAULT 'Standard',
    PRIMARY KEY (`Id`)
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
[
{
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",   "DataType": "INT",          "Nullable": false },
        { "Name": "Tier", "DataType": "VARCHAR(20)",   "Nullable": false, "Default": "{{declaredDefault}}" }
    ],
    "Indexes": [
        { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
    ]
}
]
""";

        try
        {
            // Converge once + register product ownership. With a bug present this first
            // run may still issue a phantom MODIFY; we only assert stability afterward.
            RunTableQuenchProc(cmd, json);

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
                $"Re-quench of default '{declaredDefault}' must NOT emit a MODIFY COLUMN. " +
                "A 'Modify column' status message means the default comparison produced a phantom modification.");
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
}
