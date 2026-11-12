// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MySQL;

[Category("MySQL")]
[Category("Integration")]
[NonParallelizable]
public class TableSplitInABeforeScriptTests : TableSplitInABeforeScriptSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MySQL";
    protected override string ProductName => "TableSplitProduct";

    protected override string TableFileName(string table) => $"{table}.json";
    protected override string TableJson(string table) => $$"""
        { "Name": "{{table}}",
          "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
        """;
    protected override string SplitScript => $"DROP TABLE IF EXISTS `{MainDb}`.vs_customer;\nCREATE VIEW `{MainDb}`.vs_customer AS SELECT id FROM `{MainDb}`.vs_keep;";
    protected override string DropAll => $"DROP VIEW IF EXISTS `{MainDb}`.vs_customer; DROP TABLE IF EXISTS `{MainDb}`.vs_customer; DROP TABLE IF EXISTS `{MainDb}`.vs_keep;";
    protected override string ViewCount => $"SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = 'vs_customer'";
}
