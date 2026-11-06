// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class DriftTests : DriftSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "DriftProduct";

    protected override string TableJson(string table, params string[] columns) => $$"""
        { "Schema": "[dbo]", "Name": "[{{table}}]",
          "Columns": [ { "Name": "[id]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false }{{string.Concat(columns.Select(c => $", {{ \"Name\": \"[{c}]\", \"DataType\": \"INT\", \"Nullable\": true }}"))}} ],
          "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;
    protected override string TableFileName(string table) => $"dbo.{table}.json";
    protected override string QualifiedColumn(string table, string column) => $"dbo.{table}.{column}";
    protected override string DropTable(string table) => $"DROP TABLE IF EXISTS dbo.{table}";
    protected override string DropColumn(string table, string column) => $"ALTER TABLE dbo.{table} DROP COLUMN {column}";
    protected override string ViewScript(string view, int value) => $"CREATE OR ALTER VIEW dbo.{view} AS SELECT {value} AS x";
    protected override string DropView(string view) => $"DROP VIEW IF EXISTS dbo.{view}";
    protected override string StampTable => "[SchemaSmith].[KindleStamp]";

    // #508 inside a drift check: with no map row to say the reworded CHECK is the one deployed, the scratch-copy probe
    // decides -- and inside the drift transaction it must run as a savepoint, not give up (which read as drift).
    [Test]
    public void Tier1_ACheckTheEngineRewords_IsNotDrift_WhenOnlyTheProbeCanTell()
    {
        const string table = "drift_ck";
        Exec(DropTable(table));
        WritePackage("");
        WriteFile("Tables", TableFileName(Table), TableJson(Table, "a", "b"));
        WriteFile("Tables", TableFileName(table), $$"""
            { "Schema": "[dbo]", "Name": "[{{table}}]",
              "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false }, { "Name": "[a]", "DataType": "INT", "Nullable": true } ],
              "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ],
              "CheckConstraints": [ { "Name": "[CK_{{table}}_a]", "Expression": "a > 0" } ] }
            """);
        try
        {
            Deploy();
            Exec($"DELETE FROM SchemaSmith.ExpressionMap WHERE ObjectTable = '{table}'");
            var run = RunDrift();
            Assert.Multiple(() =>
            {
                Assert.That(run.ExitCode, Is.EqualTo(0), run.Describe());
                Assert.That(run.Report?.Drifted, Is.False, run.Describe());
            });
        }
        finally
        {
            Exec(DropTable(table));
        }
    }
}
