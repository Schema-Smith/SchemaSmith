// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// A statistic declared without SampleSize was created WITH SAMPLE 0 PERCENT, which SQL Server documents as a statistic
// with no data: the optimizer had a histogram of nothing until auto-update happened to replace it. Omitted (and 0)
// means the server's default sampling; a positive size is the percentage.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_StatisticsSampleTests : BaseTableQuenchTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AStatisticWithoutSampleSize_IsBuiltFromTheTablesRows(bool indexOnly)
    {
        var table = $"StatsSample_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = $@"CREATE TABLE [dbo].[{table}] ([Id] INT NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY, [Code] INT NOT NULL);
                INSERT INTO [dbo].[{table}] ([Id], [Code]) SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 7 FROM sys.all_objects a CROSS JOIN sys.all_objects b;";
            cmd.ExecuteNonQuery();

            var json = $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false },
      { "Name": "[Code]", "DataType": "INT", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
    "Statistics": [
      { "Name": "[ST_{{table}}_Default]", "Columns": "[Code]" },
      { "Name": "[ST_{{table}}_Half]", "Columns": "[Code], [Id]", "SampleSize": 50 }
    ]
  }
]
""";
            RunTableQuenchProc(cmd, json, indexOnly: indexOnly);

            cmd.CommandText = $@"SELECT s.[name] + ':' + CAST(ISNULL(p.rows_sampled, 0) AS VARCHAR(20))
                                   FROM sys.stats s CROSS APPLY sys.dm_db_stats_properties(s.[object_id], s.stats_id) p
                                  WHERE s.[object_id] = OBJECT_ID('dbo.{table}') AND s.[name] = 'ST_{table}_Default'";
            var defaultSample = cmd.ExecuteScalar()?.ToString();
            cmd.CommandText = $@"SELECT ISNULL(p.rows_sampled, 0)
                                   FROM sys.stats s CROSS APPLY sys.dm_db_stats_properties(s.[object_id], s.stats_id) p
                                  WHERE s.[object_id] = OBJECT_ID('dbo.{table}') AND s.[name] = 'ST_{table}_Half'";
            var halfSample = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(defaultSample, Does.StartWith($"ST_{table}_Default:").And.Not.EndWith(":0"),
                    "a statistic with no SampleSize is built with the server's default sampling, not from zero rows");
                Assert.That(halfSample, Is.GreaterThan(0), "a declared SampleSize is still applied");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}];";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
    }
}
