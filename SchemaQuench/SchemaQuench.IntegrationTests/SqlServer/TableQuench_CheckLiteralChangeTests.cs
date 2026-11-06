// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// The check-expression fold removed spaces inside string literals too, so a check changed only inside a literal
// ('A, B' to 'A,B') compared equal to the deployed one and was never re-created: a missed change.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_CheckLiteralChangeTests : BaseTableQuenchTests
{
    private static string Json(string table, string literal) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",   "DataType": "INT",         "Nullable": false },
      { "Name": "[Code]", "DataType": "VARCHAR(20)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
    "CheckConstraints": [ { "Name": "[CK_{{table}}_Code]", "Expression": "[Code] = '{{literal}}'" } ]
  }
]
""";

    [Test]
    public void ACheckChangedOnlyInsideAStringLiteral_IsReapplied()
    {
        var table = $"CkLit_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            RunTableQuenchProc(cmd, Json(table, "A, B"));
            RunTableQuenchProc(cmd, Json(table, "A,B"));

            cmd.CommandText = $"SELECT definition FROM sys.check_constraints WHERE name = 'CK_{table}_Code'";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Does.Contain("'A,B'"), "the changed literal is deployed");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
            cmd.ExecuteNonQuery();
        }
    }
}
