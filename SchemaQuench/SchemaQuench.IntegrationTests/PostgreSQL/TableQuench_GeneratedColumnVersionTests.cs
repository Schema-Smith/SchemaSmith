// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_GeneratedColumnVersionTests : BaseTableQuenchTests
{
    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE SCHEMA IF NOT EXISTS ""GeneratedColumnVersionTests"";";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Parity (#243 follow-up): a generated column on a BRAND-NEW table (created by SchemaSmith, not
    // seeded via raw CREATE) must be created AS a generated column — not a plain column. SQL Server
    // and MySQL exclude computed/generated columns from CREATE TABLE and add them via a later step
    // (they may reference other columns); PostgreSQL must do the same. Regression guard for the bug
    // where PG's CREATE TABLE emitted the column with no GENERATED clause, then skipped it as
    // "already exists".
    [Test]
    public void TableQuench_CreatesGeneratedColumn_OnNewTable()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "GeneratedColumnVersionTests";
        var tableName = $"GenNew_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
[{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "DoubleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 2)" }
    ]
}]
""";
        RunTableQuenchProc(cmd, json, productName: productName);

        cmd.CommandText = $@"SELECT is_generated FROM information_schema.columns
                             WHERE table_schema = '{schema}' AND table_name = '{tableName}' AND column_name = 'DoubleQty';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ALWAYS"),
            "DoubleQty must be created as a GENERATED column on a brand-new table, not a plain column.");

        // Data proof: the generation must actually compute.
        cmd.CommandText = $@"INSERT INTO ""{schema}"".""{tableName}"" (""Qty"") VALUES (7);
                             SELECT ""DoubleQty"" FROM ""{schema}"".""{tableName}"" WHERE ""Qty"" = 7;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(14));

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Forces the in-DB version helper to report PG 16 (the override GUC), so the
    // pre-17 drop-and-re-add path runs even on a PG17 CI container. Asserts the
    // generated-column expression change is applied AND data is correct, with no
    // 42601 SET EXPRESSION error. Layer-1 — no second container (design §Testing Strategy).
    [Test]
    public void TableQuench_ChangesGeneratedExpression_ViaDropAndReAdd_WhenForcedPg16()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "GeneratedColumnVersionTests";
        var tableName = $"GenColV16_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Seed: a stored generated column "DoubleQty" = Qty * 2, with a row.
        cmd.CommandText = $@"
SET schemasmith.version_override = '16';
CREATE TABLE ""{schema}"".""{tableName}"" (
  ""Qty"" INT NOT NULL,
  ""DoubleQty"" INT GENERATED ALWAYS AS (""Qty"" * 2) STORED
);
INSERT INTO ""{schema}"".""{tableName}"" (""Qty"") VALUES (5);";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with a CHANGED expression: Qty * 3. Override is still set on this session.
        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "DoubleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 3)" }
    ]
}
""";
        cmd.CommandText = $@"
SET schemasmith.version_override = '16';
CALL ""SchemaSmith"".""TableQuench""(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false);";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // New expression must be live and the existing row recomputed to 15 (5 * 3).
        cmd.CommandText = $@"RESET schemasmith.version_override;
SELECT ""DoubleQty"" FROM ""{schema}"".""{tableName}"" WHERE ""Qty"" = 5;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(15),
            "Generated-column expression change must apply via drop-and-re-add on PG < 17 (no SET EXPRESSION)");

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Native-version path: no override, so the helper reports the real container version.
    // End state must be identical regardless of which branch fired.
    [Test]
    public void TableQuench_ChangesGeneratedExpression_NativeVersion()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "GeneratedColumnVersionTests";
        var tableName = $"GenColNative_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
CREATE TABLE ""{schema}"".""{tableName}"" (
  ""Qty"" INT NOT NULL,
  ""DoubleQty"" INT GENERATED ALWAYS AS (""Qty"" * 2) STORED
);
INSERT INTO ""{schema}"".""{tableName}"" (""Qty"") VALUES (5);";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "DoubleQty", "DataType": "INT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 3)" }
    ]
}
""";
        cmd.CommandText = $@"CALL ""SchemaSmith"".""TableQuench""(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false);";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $@"SELECT ""DoubleQty"" FROM ""{schema}"".""{tableName}"" WHERE ""Qty"" = 5;";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(15));

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // Regression for the #296 double-processing bug. On PG < 17 a generated column whose expression
    // AND data type change together must be handled SOLELY by the drop-and-re-add pass (which re-adds
    // the full definition incl. the new type), and must NOT also enter the ALTER pass — otherwise the
    // ALTER pass emits a redundant SET DATA TYPE computed off the stale pre-DDL snapshot, which can
    // hard-fail on a GENERATED ... STORED column. Asserts the new expression is live AND the new type
    // is in effect, with the quench completing without error.
    [Test]
    public void TableQuench_ChangesGeneratedExpressionAndType_ViaDropAndReAdd_WhenForcedPg16()
    {
        var productName = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var schema = "GeneratedColumnVersionTests";
        var tableName = $"GenColV16Mixed_{uniqueId}";

        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Seed: a stored generated INT column "DoubleQty" = Qty * 2, with a row.
        cmd.CommandText = $@"
SET schemasmith.version_override = '16';
CREATE TABLE ""{schema}"".""{tableName}"" (
  ""Qty"" INT NOT NULL,
  ""DoubleQty"" INT GENERATED ALWAYS AS (""Qty"" * 2) STORED
);
INSERT INTO ""{schema}"".""{tableName}"" (""Qty"") VALUES (5);";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        // Quench with BOTH the expression (Qty*2 -> Qty*3) AND the data type (INT -> BIGINT) changed.
        // The mixed change is exactly the case the old guard let leak into the ALTER pass.
        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "Qty", "DataType": "INT", "Nullable": false },
        { "Name": "DoubleQty", "DataType": "BIGINT", "Nullable": true,
          "Generated": "ALWAYS", "GenerationExpression": "(\"Qty\" * 3)" }
    ]
}
""";
        cmd.CommandText = $@"
SET schemasmith.version_override = '16';
CALL ""SchemaSmith"".""TableQuench""(p_ProductName := '{productName}', p_TableDefinitions := '{json.Replace("'", "''")}', p_WhatIf := false, p_DropTablesRemovedFromProduct := false, p_DropUnknownIndexes := false);";
        cmd.CommandTimeout = 300;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // New expression must be live: row recomputed to 15 (5 * 3).
        cmd.CommandText = $@"RESET schemasmith.version_override;
SELECT ""DoubleQty"" FROM ""{schema}"".""{tableName}"" WHERE ""Qty"" = 5;";
        Assert.That(Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(15L),
            "Generated-column expression change must apply via drop-and-re-add on PG < 17");

        // New data type must be in effect: the re-add carries BIGINT (int8); no stale SET DATA TYPE double-processing.
        cmd.CommandText = $@"
SELECT data_type FROM information_schema.columns
 WHERE table_schema = '{schema}' AND table_name = '{tableName}' AND column_name = 'DoubleQty';";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("bigint"),
            "Mixed expression+type change must converge the new type via drop-and-re-add (column handled solely by that pass)");

        cmd.CommandText = $@"DROP TABLE ""{schema}"".""{tableName}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }
}
