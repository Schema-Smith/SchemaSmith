// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// SS-046. SchemaTongs writes CompressionType MIXED for a table or index whose partitions are compressed differently, so
// a deploy leaves that compression alone instead of flattening it. The table create honoured that; the index
// compression fixups did not, and wrote DATA_COMPRESSION=MIXED, which failed the deploy on the first run.
[Category("SqlServer")]
public class TableQuench_MixedCompressionTests : BaseTableQuenchTests
{
    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void MixedCompression_DeploysAndRedeploys(string path)
    {
        var table = "MixedCmp" + path;
        var indexOnly = path != "TableQuench";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Drop(cmd, table);
        try
        {
            var json = $$"""
                [{ "Schema": "[dbo]", "Name": "[{{table}}]", "CompressionType": "MIXED",
                   "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                   "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]", "CompressionType": "MIXED" },
                                { "Name": "[IX_{{table}}_Val]", "IndexColumns": "[Val]", "CompressionType": "MIXED" } ] }]
                """;
            if (indexOnly)
            {
                Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, Val INT NULL)");
                Run(cmd, $"CREATE INDEX IX_{table}_Val ON dbo.{table} (Val)");
            }

            if (path == "IndexOnlyXmlQuench")
            {
                foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench())) Run(cmd, batch);
                var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table").Replace("'", "''");
                Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = '{_productName}', @TableDefinitions = N'{xml}'");
                Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = '{_productName}', @TableDefinitions = N'{xml}'");
            }
            else
            {
                RunTableQuenchProc(cmd, json, indexOnly: indexOnly);
                RunTableQuenchProc(cmd, json, indexOnly: indexOnly);
            }

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND index_id > 0";
            Assert.That(System.Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "both indexes are deployed");
        }
        finally
        {
            Drop(cmd, table);
        }
    }

    private static void Drop(IDbCommand cmd, string table) => Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
