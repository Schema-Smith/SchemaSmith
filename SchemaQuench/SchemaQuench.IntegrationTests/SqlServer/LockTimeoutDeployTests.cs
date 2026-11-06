// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Category("Integration")]
[NonParallelizable]
public class LockTimeoutDeployTests : LockTimeoutDeploySharedTests
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "LockTimeoutProduct";

    protected override string TableFileName => "dbo.lk_t.json";
    protected override string SmallType => "INT";
    protected override string BigType => "BIGINT";
    protected override string TableJson(string type) => $$"""
        { "Schema": "[dbo]", "Name": "[lk_t]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false }, { "Name": "[a]", "DataType": "{{type}}", "Nullable": true } ],
          "Indexes": [ { "Name": "[PK_lk_t]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
        """;
    protected override string HoldLock => "SELECT * FROM dbo.lk_t WITH (TABLOCKX, HOLDLOCK)";
    protected override string ColumnType() => Scalar("SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'lk_t' AND COLUMN_NAME = 'a'")?.ToString();
    protected override string DropTable => "DROP TABLE IF EXISTS dbo.lk_t";
}
