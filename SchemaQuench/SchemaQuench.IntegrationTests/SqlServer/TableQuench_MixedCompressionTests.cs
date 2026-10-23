// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

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

    // MIXED means "leave the compression as it is", so an index compressed PAGE that the package declares MIXED keeps
    // PAGE. The index change comparison used to see the deployed side's DATA_COMPRESSION against nothing on the declared
    // side, so every deploy dropped the index and created it again uncompressed.
    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void MixedCompression_LeavesAnIndexCompressionAlone(string path)
    {
        var table = "MixedKeep" + path;
        var indexOnly = path != "TableQuench";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Drop(cmd, table);
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, Val INT NULL)");
            Run(cmd, $"CREATE INDEX IX_{table}_Val ON dbo.{table} (Val) WITH (DATA_COMPRESSION = PAGE)");
            var json = $$"""
                [{ "Schema": "[dbo]", "Name": "[{{table}}]",
                   "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Val]", "DataType": "INT", "Nullable": true } ],
                   "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                                { "Name": "[IX_{{table}}_Val]", "IndexColumns": "[Val]", "CompressionType": "MIXED" } ] }]
                """;

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

            cmd.CommandText = $@"SELECT p.data_compression_desc FROM sys.partitions p JOIN sys.indexes i ON i.[object_id] = p.[object_id] AND i.index_id = p.index_id
                                  WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.[name] = 'IX_{table}_Val'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("PAGE"), "an index declared MIXED must keep the compression it has");
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
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
    }
}
