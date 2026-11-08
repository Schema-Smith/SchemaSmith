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
    private static string Json(string table, string dataType) => $$"""
[ { "Schema": "[dbo]", "Name": "[{{table}}]",
    "Columns": [ { "Name": "[Id]", "DataType": "{{dataType}}", "Nullable": false }, { "Name": "[Note]", "DataType": "VARCHAR(20)", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ] } ]
""";

    // The spellings compare equal; a real change behind one still applies.
    [Test]
    public void ATypeChangeBehindAnIdentitySpelling_IsApplied()
    {
        var table = $"IdentChange_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, "INT IDENTITY"));
            RunTableQuenchProc(cmd, Json(table, "BIGINT IDENTITY(1,1)"));
            cmd.CommandText = $"SELECT TYPE_NAME(system_type_id) + '|' + CAST(is_identity AS VARCHAR(1)) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [name] = 'Id'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("bigint|1"));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }

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
            Assert.That(before, Is.Not.Null.And.Not.EqualTo(DBNull.Value), "setup: the first deploy built the key");

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
