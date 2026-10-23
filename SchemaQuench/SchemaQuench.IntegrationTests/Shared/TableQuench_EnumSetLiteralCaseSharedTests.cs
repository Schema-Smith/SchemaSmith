// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Regression coverage for the enum/set literal case-fidelity bug. ParseTableJson built the
/// column DDL with a naive UPPER(DataType), which upper-cased the quoted VALUES inside
/// enum(...)/set(...) — case-sensitive DATA — so a declared enum('web','ios','android')
/// deployed as enum('WEB','IOS','ANDROID'). Compounding it, ModifiedTableQuench compared
/// enum/set with a case-insensitive UPPER(...) != UPPER(...), so a corrected lowercase
/// declaration never re-applied (the wrong-case definition was sticky).
///
/// After the fix the type keyword is still upper-cased but quoted literals are preserved, and
/// the enum/set comparison is value-case-sensitive (keyword-case-insensitive) so a corrected
/// declaration converges.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_EnumSetLiteralCaseSharedTests : BaseTableQuenchTests
{
    [TestCase("enum('web','ios','android')", TestName = "TableQuench_EnumLiteralCasePreserved")]
    [TestCase("set('web','ios','android')", TestName = "TableQuench_SetLiteralCasePreserved")]
    public void TableQuench_EnumSetLiteralCase_PreservedAndIdempotent(string declaredType)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"EnumCase_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
[
{
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",     "DataType": "INT",             "Nullable": false },
        { "Name": "Flavor", "DataType": "{{declaredType}}", "Nullable": true }
    ],
    "Indexes": [
        { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
    ]
}
]
""";

        try
        {
            // Fresh deploy: TableQuench creates the table. Values must survive verbatim.
            RunTableQuenchProc(cmd, json);

            var typeAfterCreate = GetRawColumnType(cmd, tableName, "Flavor");
            Assert.That(typeAfterCreate, Is.EqualTo(declaredType),
                $"enum/set literal values must be preserved verbatim on deploy (got '{typeAfterCreate}')");

            // Re-quench must be a no-op — the value-case-sensitive compare must not phantom-flag
            // the (already correct) column as modified.
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
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0),
                "Re-quench must NOT emit a MODIFY COLUMN for an unchanged enum/set column.");

            Assert.That(GetRawColumnType(cmd, tableName, "Flavor"), Is.EqualTo(declaredType),
                "Stored enum/set type must be unchanged across the no-op re-quench.");
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

    [Test]
    public void TableQuench_EnumLiteralCase_CorrectedDeclarationConverges()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"EnumSticky_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Simulate a table already deployed with the (buggy) upper-cased enum values.
        cmd.CommandText = $@"
DROP TABLE IF EXISTS `{_mainDb}`.`{tableName}`;
CREATE TABLE `{_mainDb}`.`{tableName}` (
    `Id` INT NOT NULL,
    `Flavor` ENUM('WEB','IOS','ANDROID') NULL,
    PRIMARY KEY (`Id`)
);";
        cmd.ExecuteNonQuery();

        var json = $$"""
[
{
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Id",     "DataType": "INT",                           "Nullable": false },
        { "Name": "Flavor", "DataType": "enum('web','ios','android')",   "Nullable": true }
    ],
    "Indexes": [
        { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" }
    ]
}
]
""";

        try
        {
            // The corrected lowercase declaration must re-apply (case-sensitive compare) and converge.
            RunTableQuenchProc(cmd, json);

            Assert.That(GetRawColumnType(cmd, tableName, "Flavor"), Is.EqualTo("enum('web','ios','android')"),
                "A corrected lowercase enum declaration must converge (the wrong-case form must not be sticky).");
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
