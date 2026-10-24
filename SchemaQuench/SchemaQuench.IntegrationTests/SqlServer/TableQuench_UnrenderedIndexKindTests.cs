// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class TableQuench_UnrenderedIndexKindTests : BaseTableQuenchTests
{
    // A deploy that drops unknown indexes once read a selective XML index as a primary one and dropped it while no package
    // could declare one (#471). Spatial (#526) and selective XML indexes can be declared now, so an undeclared one is
    // unknown like any other and is dropped -- the selective one as itself, not as a primary XML index. The plain unknown
    // index proves the step ran.
    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void ADeployThatDropsUnknownIndexes_DropsUndeclaredSpatialAndSelectiveXmlIndexes(string path)
    {
        var table = "UnrKind" + path;
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Drop(cmd, table);
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, Code INT NULL, g GEOMETRY NULL, sx XML NULL)");
            Run(cmd, $"CREATE INDEX IX_{table}_Code ON dbo.{table} (Code)");
            Run(cmd, $"CREATE SPATIAL INDEX SX_{table} ON dbo.{table} (g) WITH (BOUNDING_BOX = (0, 0, 100, 100))");
            Run(cmd, $"CREATE SELECTIVE XML INDEX SXI_{table} ON dbo.{table} (sx) FOR (p1 = '/a/b')");

            var json = $$"""
                [{ "Schema": "[dbo]", "Name": "[{{table}}]",
                   "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Code]", "DataType": "INT", "Nullable": true },
                                { "Name": "[g]", "DataType": "GEOMETRY", "Nullable": true }, { "Name": "[sx]", "DataType": "XML", "Nullable": true } ],
                   "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] }]
                """;
            switch (path)
            {
                case "TableQuench":
                    Run(cmd, $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 1");
                    break;
                case "IndexOnlyQuench":
                    RunTableQuenchProc(cmd, json, indexOnly: true);
                    break;
                default:
                    foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench())) Run(cmd, batch);
                    var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");
                    Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = '{_productName}', @TableDefinitions = N'{xml.Replace("'", "''")}', @DropUnknownIndexes = 1");
                    break;
            }

            Assert.Multiple(() =>
            {
                Assert.That(IndexExists(cmd, table, $"IX_{table}_Code"), Is.False, "precondition: the deploy dropped an unknown index it can declare");
                Assert.That(IndexExists(cmd, table, $"SX_{table}"), Is.False, "an undeclared spatial index is unknown, and dropped");
                Assert.That(IndexExists(cmd, table, $"SXI_{table}"), Is.False, "an undeclared selective XML index is unknown, and dropped");
            });
        }
        finally
        {
            Drop(cmd, table);
        }
    }

    private static bool IndexExists(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = '{index}'";
        return System.Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static void Drop(IDbCommand cmd, string table) => Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }
}
