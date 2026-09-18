// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// #242, the remaining expression surfaces on SQL Server: index filter expressions and column defaults.
/// <para><b>These tests measure before anything is wired.</b> The design orders this work by measured pain, and
/// two surfaces it listed turned out not to churn at all once tested — claiming a fix for a surface that was
/// already idempotent would be a lie in the release notes. So each case here is authored in natural form and
/// asserts the object is left alone; whichever of them reddens is a real defect, and only those get wired.</para>
/// <para>Detection is the change audit rather than a text match: SchemaSmith writes a row when it creates or
/// drops an object, so a re-created index or column shows up there whatever the log says.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class TableQuench_ExpressionSurfaceMeasurementTests : BaseTableQuenchTests
{
    private static string FilteredIndexJson(string table, string filter) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Status]", "DataType": "INT", "Nullable": true}
            ],
            "Indexes": [
                {"Name": "[IX_{{table}}_Status]", "IndexColumns": "[Status]", "FilterExpression": "{{filter}}"}
            ]
        }
        """;

    private static string DefaultJson(string table, string defaultValue) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Qty]", "DataType": "INT", "Nullable": true, "Default": "{{defaultValue}}"}
            ]
        }
        """;

    private static int AuditRowCount(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
                              WHERE ObjectName LIKE '%{table}%' AND ActionType IN ('created', 'dropped', 'modified')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearAudit(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{table}%'";
        cmd.ExecuteNonQuery();
    }

    // Every drop the deploy performs prints "Dropping ..." through RAISERROR, on every code path. That is the
    // one signal that is the same for the full quench and the index-only quench: the index-only path writes no
    // audit row when it drops an index, and a recreated statistic can be handed back its old stats_id, so
    // neither the audit nor the id can prove the object was left alone there.
    private readonly System.Collections.Generic.List<string> _messages = new();

    private int DropMessages(string table) =>
        _messages.FindAll(m => m.Contains("Dropping") && m.Contains(table)).Count;

    private void WithTable(string table, string createSql, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        _messages.Clear();
        ((Microsoft.Data.SqlClient.SqlConnection)conn).InfoMessage += (_, e) =>
        {
            foreach (Microsoft.Data.SqlClient.SqlError err in e.Errors) _messages.Add(err.Message);
        };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
            cmd.ExecuteNonQuery();
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.[{table}]";
            cmd.ExecuteNonQuery();
            ClearAudit(cmd, table);
            cmd.CommandText = $"DELETE FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}'";
            cmd.ExecuteNonQuery();
        }
    }

    // A filter authored the way a person writes it. SQL Server stores ([Status]>(0)); the index comparison
    // rebuilds the whole CREATE INDEX string from the catalog and compares it to the declared one, so the
    // filter's stored form is part of that string.
    [Test]
    public void AFilteredIndexAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfIdx_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredIndexJson(table, "Status > 0");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the filtered index was re-created for an unchanged declaration");
            }
        });
    }

    // A default authored as a bare literal. SQL Server stores ((0)).
    [Test]
    public void AColumnDefaultAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfDef_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)", cmd =>
        {
            var json = DefaultJson(table, "0");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the column default was re-created for an unchanged declaration");
            }
        });
    }

    // A default with a function call, which SQL Server reframes more aggressively than a literal.
    [Test]
    public void AFunctionDefaultAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfFn_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)", cmd =>
        {
            var json = DefaultJson(table, "datepart(year, getdate())");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd, table);

            for (var pass = 2; pass <= 3; pass++)
            {
                RunTableQuenchProc(cmd, json);
                Assert.That(AuditRowCount(cmd, table), Is.Zero,
                    $"pass {pass}: the function default was re-created for an unchanged declaration");
            }
        });
    }

    // The other direction, and the shape the Course 4 Recipe 2 lab teaches: a default driven by a token, whose
    // resolved value changes after the mapping has recorded the old one. The map must not vouch for the old value.
    [TestCase("90", "30", "30")]
    [TestCase("datepart(year, getdate())", "datepart(month, getdate())", "month")]
    public void ChangingADefaultAfterItWasRecorded_IsApplied(string before, string after, string expectedInLive)
    {
        var table = $"ExprSurfChg_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, DefaultJson(table, before));
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ExpressionMap WHERE [ObjectTable] = '{table}' AND [Slot] = 'default'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "setup: the default's mapping must be recorded");

            RunTableQuenchProc(cmd, DefaultJson(table, after));
            cmd.CommandText = $"SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = 'Qty'";
            Assert.That(cmd.ExecuteScalar() as string, Does.Contain(expectedInLive), "the changed default must be applied");
        });
    }

    private static string FilteredStatisticJson(string table, string filter) => $$"""
        {
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                {"Name": "[Status]", "DataType": "INT", "Nullable": true}
            ],
            "Statistics": [
                {"Name": "[ST_{{table}}_Status]", "Columns": "[Status]", "FilterExpression": "{{filter}}"}
            ]
        }
        """;

    private static int StatsId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT s.stats_id FROM sys.stats s
                                             WHERE s.[object_id] = OBJECT_ID('dbo.{table}') AND s.[name] = 'ST_{table}_Status'), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int IndexId(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"SELECT ISNULL((SELECT i.index_id FROM sys.indexes i
                                             WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.[name] = 'IX_{table}_Status'), 0)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // The index-only path (Template.IndexOnlyTableQuenches) has its OWN index comparison in IndexOnlyQuench,
    // separate from ModifiedTableQuench, and its temp tables live only inside that procedure -- so neither the
    // comparison nor the recording done for the full quench reaches it.
    [Test]
    public void AFilteredIndexInIndexOnlyMode_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfIO_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredIndexJson(table, "Status > 0");
            RunTableQuenchProc(cmd, json, indexOnly: true);
            var firstId = IndexId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the index must exist after the first index-only deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                _messages.Clear();
                RunTableQuenchProc(cmd, json, indexOnly: true);
                Assert.That(DropMessages(table), Is.Zero,
                    $"pass {pass}: index-only mode re-created the filtered index for an unchanged declaration");
            }
        });
    }

    // Filtered statistics carry a predicate too. Unmeasured until now.
    [Test]
    public void AFilteredStatisticAuthoredInNaturalForm_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfSt_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredStatisticJson(table, "Status > 0");
            RunTableQuenchProc(cmd, json);
            var firstId = StatsId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the statistic must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                _messages.Clear();
                RunTableQuenchProc(cmd, json);
                Assert.That(DropMessages(table), Is.Zero,
                    $"pass {pass}: the filtered statistic was re-created for an unchanged declaration");
            }
        });
    }

    [Test]
    public void AFilteredStatisticInIndexOnlyMode_IsNotReCreatedOnEveryDeploy()
    {
        var table = $"ExprSurfSIO_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredStatisticJson(table, "Status > 0");
            RunTableQuenchProc(cmd, json, indexOnly: true);
            var firstId = StatsId(cmd, table);
            Assert.That(firstId, Is.Not.Zero, "setup: the statistic must exist after the first index-only deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                _messages.Clear();
                RunTableQuenchProc(cmd, json, indexOnly: true);
                Assert.That(DropMessages(table), Is.Zero,
                    $"pass {pass}: index-only mode re-created the filtered statistic for an unchanged declaration");
            }
        });
    }

    // Control for the filtered cases above: the SAME statistic with no filter at all. If this churns too, the
    // defect is in the statistic comparison itself, not in expression handling.
    [TestCase(false)]
    [TestCase(true)]
    public void AnUnfilteredStatistic_IsNotReCreatedOnEveryDeploy(bool indexOnly)
    {
        var table = $"ExprSurfSU_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredStatisticJson(table, "").Replace(@", ""FilterExpression"": """"", "");
            RunTableQuenchProc(cmd, json, indexOnly: indexOnly);
            Assert.That(StatsId(cmd, table), Is.Not.Zero, "setup: the statistic must exist after the first deploy");

            for (var pass = 2; pass <= 3; pass++)
            {
                _messages.Clear();
                RunTableQuenchProc(cmd, json, indexOnly: indexOnly);
                Assert.That(DropMessages(table), Is.Zero,
                    $"pass {pass}: an UNFILTERED statistic was re-created for an unchanged declaration (indexOnly={indexOnly})");
            }
        });
    }

    // A rename pairs the declared index with the live one by comparing their whole CREATE INDEX scripts with the
    // name blanked out -- and the filter is in that script. A filter SQL Server rewrote never matched, so a rename
    // fell through to dropping the old index and building a new one.
    [TestCase(false)]
    [TestCase(true)]
    public void RenamingAFilteredIndex_IsARename_NotADropAndRebuild(bool indexOnly)
    {
        var table = $"ExprSurfRn_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            RunTableQuenchProc(cmd, FilteredIndexJson(table, "Status > 0"), indexOnly: indexOnly);
            Assert.That(IndexId(cmd, table), Is.Not.Zero, "setup: the filtered index must exist");

            _messages.Clear();
            var renamed = FilteredIndexJson(table, "Status > 0").Replace($"IX_{table}_Status", $"IX_{table}_Renamed");
            RunTableQuenchProc(cmd, renamed, indexOnly: indexOnly);

            Assert.That(DropMessages(table), Is.Zero,
                $"a rename must not drop the index (indexOnly={indexOnly}); messages: {string.Join(" | ", _messages)}");
            cmd.CommandText = $@"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'IX_{table}_Renamed'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the index must exist under its new name");
        });
    }

    // CONTROL for the rename case: identical, with no filter. Separates "the filter broke the rename pairing"
    // from "index-only renames misbehave regardless". Asserts the physical outcome (one index, under the new
    // name, still carrying its original index_id) as well as the messages.
    // The harness runs the full quench with @DropUnknownIndexes = 0 and index-only with 1, which is why this first
    // showed as index-only-specific. It is not: with DropUnknownIndexes on, the index just renamed is still in the
    // pre-rename snapshot under its OLD name and absent from the package, so it is selected as "unknown" and the
    // deploy logs dropping it. Covered here for both paths with the setting on.
    [TestCase(false)]
    [TestCase(true)]
    public void RenamingAnUnfilteredIndex_Control(bool indexOnly)
    {
        var table = $"ExprSurfRc_{Guid.NewGuid():N}"[..20];
        WithTable(table, $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Status] INT NULL)", cmd =>
        {
            var json = FilteredIndexJson(table, "").Replace(@", ""FilterExpression"": """"", "");
            RunTableQuenchProc(cmd, json, indexOnly: indexOnly);
            var idBefore = IndexId(cmd, table);
            Assert.That(idBefore, Is.Not.Zero, "setup");

            _messages.Clear();
            var renamedJson = json.Replace($"IX_{table}_Status", $"IX_{table}_Renamed");
            if (indexOnly)
                RunTableQuenchProc(cmd, renamedJson, indexOnly: true);
            else
            {
                cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{renamedJson.Replace("'", "''")}', @WhatIf = 0, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 1";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText = $@"SELECT STRING_AGG([name] + ':' + CAST(index_id AS VARCHAR(10)), ',') FROM sys.indexes
                                  WHERE [object_id] = OBJECT_ID('dbo.{table}') AND index_id > 0";
            var physical = cmd.ExecuteScalar() as string;
            Assert.Multiple(() =>
            {
                Assert.That(physical, Is.EqualTo($"IX_{table}_Renamed:{idBefore}"),
                    $"physical outcome (indexOnly={indexOnly})");
                Assert.That(DropMessages(table), Is.Zero,
                    $"drop messages (indexOnly={indexOnly}): {string.Join(" | ", _messages.FindAll(m => m.Contains("Dropping") || m.Contains("Renaming")))}");
            });
        });
    }

    // Found by the version-band fixture (GenuineOldBinary/ExpressionChurnAcrossVersionsTests), on every version from
    // 2008 R2 to 2025: a computed column authored without "Nullable" was dropped and re-added on every deploy. The
    // create path read an omitted Nullable as nullable while the comparison read it as NOT NULL, and a
    // non-persisted computed column's nullability -- which only the engine decides -- was compared at all.
    // Both the new-table path and the add-to-existing-table path build the column, so both are measured.
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void AComputedColumnAuthoredWithoutNullable_IsNotReAddedOnEveryDeploy(bool persisted, bool tableExistsFirst)
    {
        var table = $"ExprComp_{Guid.NewGuid():N}"[..24];
        var createSql = tableExistsFirst
            ? $"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL)"
            : "SELECT 1";
        WithTable(table, createSql, cmd =>
        {
            var json = $$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[{{table}}]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT"},
                        {"Name": "[Qty]", "DataType": "INT", "Nullable": true},
                        {"Name": "[Total]", "DataType": "INT", "ComputedExpression": "Qty * 2"{{(persisted ? ", \"Persisted\": true" : "")}}}
                    ]
                }
                """;
            int ColumnId()
            {
                cmd.CommandText = $"SELECT ISNULL(COLUMNPROPERTY(OBJECT_ID('dbo.{table}'), 'Total', 'ColumnId'), 0)";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }

            RunTableQuenchProc(cmd, json);
            var first = ColumnId();
            Assert.That(first, Is.Not.Zero, "setup: the computed column must exist");
            // An omitted Nullable leaves nullability to the engine, which derives it from the expression
            // ([Qty] is nullable, so Total is). The package never said NOT NULL, so SchemaSmith must not impose it.
            cmd.CommandText = $"SELECT COLUMNPROPERTY(OBJECT_ID('dbo.{table}'), 'Total', 'AllowsNull')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                "an omitted Nullable must leave the engine's derived nullability alone, not narrow it");

            for (var pass = 2; pass <= 3; pass++)
            {
                _messages.Clear();
                RunTableQuenchProc(cmd, json);
                Assert.Multiple(() =>
                {
                    Assert.That(ColumnId(), Is.EqualTo(first), $"pass {pass}: the computed column was dropped and re-added");
                    Assert.That(DropMessages(table), Is.Zero, $"pass {pass}: " + string.Join(" | ", _messages.FindAll(m => m.StartsWith("  "))));
                });
            }
        });
    }

    // The failure this fix exists for, measured end to end: a table deployed by an earlier version has a NULLABLE
    // persisted computed column and rows whose expression evaluates to NULL. A package that omits "Nullable" must
    // not touch it. Narrowing it meant DROP COLUMN, then an ADD ... NOT NULL that the data rejects -- and there is
    // no transaction, so the deploy aborted with the column gone.
    [Test]
    public void APersistedComputedColumnOnATableWithNullProducingRows_IsLeftAlone()
    {
        var table = $"ExprCompData_{Guid.NewGuid():N}"[..24];
        WithTable(table, $@"CREATE TABLE dbo.[{table}] ([Id] INT NOT NULL, [Qty] INT NULL);
                            INSERT INTO dbo.[{table}] ([Id], [Qty]) VALUES (1, 5), (2, NULL);
                            ALTER TABLE dbo.[{table}] ADD [Total] AS ([Qty] * 2) PERSISTED;", cmd =>
        {
            var json = $$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[{{table}}]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT"},
                        {"Name": "[Qty]", "DataType": "INT", "Nullable": true},
                        {"Name": "[Total]", "DataType": "INT", "ComputedExpression": "Qty * 2", "Persisted": true}
                    ]
                }
                """;
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, json),
                "the deploy must not fail trying to narrow a column the package never declared NOT NULL");

            cmd.CommandText = $"SELECT ISNULL(COLUMNPROPERTY(OBJECT_ID('dbo.{table}'), 'Total', 'ColumnId'), 0)";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Not.Zero, "the computed column must still exist");
            cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}]";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "and the rows must still be there");
        });
    }

    // The other direction still works: an author who declares NOT NULL gets it.
    [Test]
    public void APersistedComputedColumnDeclaredNotNull_IsCreatedNotNull()
    {
        var table = $"ExprCompNN_{Guid.NewGuid():N}"[..24];
        WithTable(table, "SELECT 1", cmd =>
        {
            var json = $$"""
                {
                    "Schema": "[dbo]",
                    "Name": "[{{table}}]",
                    "Columns": [
                        {"Name": "[Id]", "DataType": "INT"},
                        {"Name": "[Qty]", "DataType": "INT"},
                        {"Name": "[Total]", "DataType": "INT", "ComputedExpression": "Qty * 2", "Persisted": true, "Nullable": false}
                    ]
                }
                """;
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT COLUMNPROPERTY(OBJECT_ID('dbo.{table}'), 'Total', 'AllowsNull')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.Zero, "an explicit \"Nullable\": false must still produce NOT NULL");

            _messages.Clear();
            RunTableQuenchProc(cmd, json);
            Assert.That(DropMessages(table), Is.Zero, "and must not churn on the next deploy");
        });
    }
}
