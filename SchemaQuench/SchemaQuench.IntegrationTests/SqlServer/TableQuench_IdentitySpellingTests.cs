// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// INTEGER was folded to INT only alone, and IDENTITY compared as written, so "INTEGER IDENTITY(1,1)" or "INT IDENTITY"
// never matched the deployed "INT IDENTITY(1, 1)": the column was altered on every deploy, dropping the primary key and
// every foreign key that references it.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_IdentitySpellingTests : BaseTableQuenchTests
{
    [TestCase("INTEGER IDENTITY(1,1)")]
    [TestCase("INT IDENTITY")]
    [TestCase("int identity( 1 , 1 )")]
    public void AnIdentityWrittenAnotherWay_RedeploysWithoutTouchingTheKey(string dataType)
    {
        var table = $"IdentSpell_{Guid.NewGuid().ToString("N")[..8]}";
        var json = $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",   "DataType": "{{dataType}}", "Nullable": false },
      { "Name": "[Note]", "DataType": "VARCHAR(20)",  "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
  }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT OBJECT_ID('dbo.PK_{table}')";
            var before = cmd.ExecuteScalar();

            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT OBJECT_ID('dbo.PK_{table}')";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo(before), "the redeploy left the column and its key as they were");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
