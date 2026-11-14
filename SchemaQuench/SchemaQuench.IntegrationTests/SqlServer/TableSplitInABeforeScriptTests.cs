// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class TableSplitInABeforeScriptTests : TableSplitInABeforeScriptSharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "TableSplitProduct";

    protected override string TableFileName(string table) => $"dbo.{table}.json";
    protected override string TableJson(string table) => $$"""
        { "Schema": "[dbo]", "Name": "[{{table}}]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;
    protected override string SplitScript => """
        IF OBJECT_ID('dbo.vs_customer', 'U') IS NOT NULL
        BEGIN
            DROP TABLE dbo.vs_customer;
            EXEC('CREATE VIEW dbo.vs_customer AS SELECT id FROM dbo.vs_keep');
        END
        """;
    protected override string DropAll => "DROP VIEW IF EXISTS dbo.vs_customer; DROP TABLE IF EXISTS dbo.vs_customer; DROP TABLE IF EXISTS dbo.vs_keep;";
    protected override string ViewCount => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'vs_customer'";
}
