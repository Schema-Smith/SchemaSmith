// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// sp_rename refuses a column while a check constraint, a computed column, a filtered index or a filtered statistic reads
// it (15336, 5074), and refuses to rename a computed column at all (4928), so a package renaming such a column through
// OldName failed every deploy. The deploy now drops what blocks the rename and puts back what the package declares.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_ColumnRenameDependentsTests : BaseTableQuenchTests
{
    private static string Json(string table, string price, string oldPrice, string calc, string oldCalc) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT", "Nullable": false },
      { "Name": "{{price}}",  "DataType": "INT", "Nullable": true, "CheckExpression": "{{price}} >= 0"{{oldPrice}} },
      { "Name": "[Qty]",      "DataType": "INT", "Nullable": true },
      { "Name": "[Total]",    "DataType": "INT", "Nullable": true, "ComputedExpression": "{{price}} * [Qty]", "Persisted": true },
      { "Name": "{{calc}}",   "DataType": "INT", "Nullable": true, "ComputedExpression": "[Id] + 1"{{oldCalc}} }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
      { "Name": "[IX_{{table}}_Total]", "IndexColumns": "[Total]" },
      { "Name": "[IX_{{table}}_Priced]", "IndexColumns": "[Id]", "FilterExpression": "{{price}} > 0" }
    ],
    "Statistics": [ { "Name": "[ST_{{table}}_Priced]", "Columns": "[Qty]", "FilterExpression": "{{price}} > 0" } ],
    "CheckConstraints": [ { "Name": "[CK_{{table}}_Line]", "Expression": "{{price}} < 1000 OR [Qty] < 10" } ]
  }
]
""";

    [Test]
    public void RenamingAColumnThatOthersRead_KeepsItsDataAndPutsTheReadersBack()
    {
        var table = $"RenDep_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString();
        }
        try
        {
            RunTableQuenchProc(cmd, Json(table, "[Price]", "", "[Calc]", ""));
            cmd.CommandText = $"INSERT INTO [dbo].[{table}] ([Id], [Price], [Qty]) VALUES (1, 5, 3)";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd,
                    Json(table, "[UnitPrice]", ", \"OldName\": \"[Price]\"", "[NextId]", ", \"OldName\": \"[Calc]\"")),
                "renaming [Price] and the computed [Calc] deploys");

            Assert.That(Scalar($"SELECT CAST([UnitPrice] AS VARCHAR(10)) + '|' + CAST([Total] AS VARCHAR(10)) + '|' + CAST([NextId] AS VARCHAR(10)) FROM [dbo].[{table}]"),
                Is.EqualTo("5|15|2"), "the renamed column keeps its value; the computed columns read it under the new name");
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] IN ('Price', 'Calc')"),
                Is.EqualTo("0"), "the old names are gone");
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] IN ('IX_{table}_Total', 'IX_{table}_Priced') AND (has_filter = 0 OR filter_definition LIKE '%UnitPrice%')"),
                Is.EqualTo("2"), "the index on the computed column and the filtered index are back, filtering on the new name");
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.stats WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'ST_{table}_Priced' AND filter_definition LIKE '%UnitPrice%'"),
                Is.EqualTo("1"), "the filtered statistic is back, filtering on the new name");
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.{table}') AND [definition] LIKE '%UnitPrice%'"),
                Is.EqualTo("2"), "the column check and the table check are back, reading the new name");

            cmd.CommandText = $"UPDATE [dbo].[{table}] SET [UnitPrice] = -1";
            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception, "the column check is enforced again");

            var checkId = $"SELECT [object_id] FROM sys.check_constraints WHERE [name] = 'CK_{table}_Line'";
            cmd.CommandText = checkId;
            var created = cmd.ExecuteScalar();
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd,
                    Json(table, "[UnitPrice]", ", \"OldName\": \"[Price]\"", "[NextId]", ", \"OldName\": \"[Calc]\"")),
                "the same package deploys again");
            cmd.CommandText = checkId;
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(created), "and leaves the table check alone");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnUndeclaredReaderOfARenamingColumn_StopsTheDeployByName_BeforeAnythingChanges()
    {
        var table = $"RenUnd_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, "[Price]", "", "[Calc]", ""));
            cmd.CommandText = $"CREATE INDEX [IX_{table}_Mine] ON [dbo].[{table}] ([Qty]) WHERE [Price] > 100";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd,
                Json(table, "[UnitPrice]", ", \"OldName\": \"[Price]\"", "[Calc]", "")));
            Assert.That(ex!.Message, Does.Contain($"[IX_{table}_Mine]"), "the refusal names the index it would have lost");

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] IN ('IX_{table}_Mine', 'IX_{table}_Priced')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(2), "neither the undeclared index nor the declared one was dropped");
            cmd.CommandText = $"SELECT COL_NAME(OBJECT_ID('dbo.{table}'), 2)";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("Price"), "the column was not renamed");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }

    // A key on a computed column that reads the renaming column has to come off, and SQL Server refuses to drop a key a
    // foreign key references (3725). The deploy stops by name before it drops anything, rather than partway through.
    [Test]
    public void AReferencedKeyOnAComputedReader_StopsTheDeployByName_BeforeAnythingChanges()
    {
        var table = $"RenKey_{Guid.NewGuid().ToString("N")[..8]}";
        string Json(string price, string oldPrice) => $$"""
[ { "Schema": "[dbo]", "Name": "[{{table}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                 { "Name": "{{price}}", "DataType": "INT", "Nullable": true{{oldPrice}} },
                 { "Name": "[Code]", "DataType": "INT", "Nullable": true, "ComputedExpression": "{{price}} + 1000", "Persisted": true } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                 { "Name": "[UQ_{{table}}_Code]", "Unique": true, "UniqueConstraint": true, "IndexColumns": "[Code]" } ] } ]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json("[Price]", ""));
            cmd.CommandText = $"CREATE TABLE [dbo].[{table}_C] ([Code] INT NULL CONSTRAINT [FK_{table}_C] REFERENCES [dbo].[{table}] ([Code]))";
            cmd.ExecuteNonQuery();

            var ex = Assert.Catch(() => RunTableQuenchProc(cmd, Json("[UnitPrice]", ", \"OldName\": \"[Price]\"")));

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain($"FK_{table}_C").And.Contain($"UQ_{table}_Code"), "the refusal names the key and what references it");
                cmd.CommandText = $"SELECT COUNT(*) FROM sys.key_constraints WHERE [name] = 'UQ_{table}_Code'";
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1), "the key was not dropped");
                cmd.CommandText = $"SELECT COL_NAME(OBJECT_ID('dbo.{table}'), 2)";
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo("Price"), "the column was not renamed");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}_C]; DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
