// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

[Category("MariaDb")]
[Category("Integration")]
[NonParallelizable]
public class LockTimeoutDeployTests : LockTimeoutDeploySharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string ConfigPrefix => "MariaDB";
    protected override string ProductName => "LockTimeoutProduct";

    protected override string TableFileName => "lk_t.json";
    protected override string SmallType => "INT";
    protected override string BigType => "BIGINT";
    protected override string TableJson(string type) => $$"""
        { "Name": "lk_t",
          "Columns": [ { "Name": "id", "DataType": "INT", "Nullable": false }, { "Name": "a", "DataType": "{{type}}", "Nullable": true } ],
          "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" } ] }
        """;
    // A read inside an open transaction holds the table's metadata lock until the transaction ends.
    protected override string HoldLock => $"SELECT * FROM `{MainDb}`.lk_t";
    protected override string ColumnType() => Scalar($"SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = 'lk_t' AND COLUMN_NAME = 'a'")?.ToString();
    protected override string DropTable => $"DROP TABLE IF EXISTS `{MainDb}`.lk_t";
}
