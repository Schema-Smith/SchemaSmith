// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// #611: changing an index between rowstore and columnstore under one name failed every deploy, because the
// compression fix-up rebuilt the old index with the new kind's compression before replacing it.
// #614: a compression-only change previewed the in-place rebuild and then a drop the real run never makes.
[Category("SqlServer")]
public class TableQuench_IndexKindChangeTests : BaseTableQuenchTests
{
    private static string Json(string table, string index) => $$"""
        [{ "Schema": "[dbo]", "Name": "[{{table}}]",
           "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[SoldAt]", "DataType": "DATETIME2", "Nullable": true },
                        { "Name": "[Amount]", "DataType": "DECIMAL(10,2)", "Nullable": true } ],
           "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                        {{index}} ] }]
        """;

    private static string Rowstore(string table, string extra = "") => $$"""{ "Name": "[IX_{{table}}_Report]", "IndexColumns": "[SoldAt]", "IncludeColumns": "[Amount]"{{extra}} }""";
    private static string Columnstore(string table) => $$"""{ "Name": "[IX_{{table}}_Report]", "ColumnStore": true, "IncludeColumns": "[SoldAt],[Amount]" }""";

    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void AnIndexChangedBetweenRowstoreAndColumnstore_IsReplaced_BothWays(string path)
    {
        var table = "KindChg" + path;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, SoldAt DATETIME2 NULL, Amount DECIMAL(10,2) NULL)");
            Deploy(cmd, path, Json(table, Rowstore(table)));
            Assert.That(IndexType(cmd, table), Is.EqualTo(2), "setup: rowstore");

            Assert.DoesNotThrow(() => Deploy(cmd, path, Json(table, Columnstore(table))), "rowstore to columnstore");
            Assert.That(IndexType(cmd, table), Is.EqualTo(6), "now a nonclustered columnstore");

            Assert.DoesNotThrow(() => Deploy(cmd, path, Json(table, Rowstore(table))), "columnstore back to rowstore");
            Assert.That(IndexType(cmd, table), Is.EqualTo(2), "a rowstore again");
        }
        finally
        {
            Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        }
    }

    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    [TestCase("IndexOnlyXmlQuench")]
    public void ACompressionOnlyChange_PreviewsTheRebuild_AndNoDrop(string path)
    {
        var table = "CmpPreview" + path;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        try
        {
            Run(cmd, $"CREATE TABLE dbo.{table} (Id INT NOT NULL CONSTRAINT PK_{table} PRIMARY KEY, SoldAt DATETIME2 NULL, Amount DECIMAL(10,2) NULL)");
            Deploy(cmd, path, Json(table, Rowstore(table)));

            Run(cmd, "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
            Deploy(cmd, path, Json(table, Rowstore(table, ", \"CompressionType\": \"PAGE\"")), whatIf: true);
            cmd.CommandText = $"SELECT ActionType FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'index' AND ObjectName LIKE '%IX_{table}_Report%' ORDER BY ActionType";
            var actions = new System.Collections.Generic.List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) actions.Add(r.GetString(0));
            Assert.That(actions, Is.EqualTo(new[] { "wouldModify" }), "only the in-place rebuild, as the real run does");
        }
        finally
        {
            Run(cmd, $"DROP TABLE IF EXISTS dbo.{table}");
        }
    }

    private void Deploy(IDbCommand cmd, string path, string json, bool whatIf = false)
    {
        var w = whatIf ? 1 : 0;
        if (path == "TableQuench")
        {
            RunTableQuenchProc(cmd, json, whatIf: whatIf);
            return;
        }
        if (path == "IndexOnlyQuench")
        {
            Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{_productName}', @TableDefinitions = N'{json.Replace("'", "''")}', @WhatIf = {w}, @DropUnknownIndexes = 1");
            return;
        }
        foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench())) Run(cmd, batch);
        var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table").Replace("'", "''");
        Run(cmd, $"EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = '{_productName}', @TableDefinitions = N'{xml}', @WhatIf = {w}, @DropUnknownIndexes = 1");
    }

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        return conn;
    }

    private static int IndexType(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT [type] FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'IX_{table}_Report'";
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
