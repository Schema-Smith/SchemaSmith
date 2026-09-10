// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Data-type synonyms must not churn (Rule 20 parity with PostgreSQL).
///
/// <para>SQL Server accepts synonyms and <c>sys.types</c> reports the base type whatever the package
/// declared, so a column authored <c>INTEGER</c> comes back <c>int</c> and the comparison read that as
/// a type change on EVERY quench — an unnecessary column rewrite on every deploy, forever. Only
/// <c>ROWVERSION</c> was mapped before, and inline in three separate places.</para>
///
/// <para><b>Asserted at the outcome, per Rule 32</b> — declare with the synonym, deploy twice, and
/// require the column not to be rewritten. The mapping table itself was measured against SQL Server
/// 2022 rather than assumed, so re-asserting it here would only check my arithmetic against itself.</para>
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class DataTypeSynonymChurnTests : BaseTableQuenchTests
{
    // Left is what a user writes; the comment is what sys.types reports back.
    [TestCase("INTEGER", TestName = "Synonym_INTEGER_DoesNotChurn")]                        // -> int
    [TestCase("DEC(10,2)", TestName = "Synonym_DEC_DoesNotChurn")]                          // -> decimal
    [TestCase("CHARACTER VARYING(50)", TestName = "Synonym_CHARACTER_VARYING_DoesNotChurn")]// -> varchar
    [TestCase("NATIONAL CHARACTER(10)", TestName = "Synonym_NATIONAL_CHARACTER_DoesNotChurn")] // -> nchar
    [TestCase("BINARY VARYING(20)", TestName = "Synonym_BINARY_VARYING_DoesNotChurn")]      // -> varbinary
    [TestCase("DOUBLE PRECISION", TestName = "Synonym_DOUBLE_PRECISION_DoesNotChurn")]      // -> float
    // Kept as a guard on the fold: ROWVERSION was the ONE mapping SQL Server already had, spread over
    // three inline REPLACE calls. Folding them into the function must not lose it.
    [TestCase("ROWVERSION", TestName = "Synonym_ROWVERSION_StillDoesNotChurn")]             // -> timestamp
    public void ColumnDeclaredWithASynonym_IsNotRewrittenOnRedeploy(string synonym)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"SynProduct_{uid}";
        var table = $"SynTable_{uid}";
        var defs = TableWithColumnType(table, synonym);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var deployed = DeployedTypeName(cmd, table, "Val");
            Assert.That(deployed, Is.Not.Empty, "Setup: the column must deploy.");

            // The churn is invisible in the column's type -- the type is right either way. Watch the
            // audit: a second deploy of an unchanged declaration must record no column change at all.
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared {synonym} was altered on a redeploy of an unchanged declaration -- "
                + $"sys.types reports it as {deployed}, and without synonym normalization the compare "
                + "reads that as a type change every single time");
        }
        finally
        {
            DropTable(cmd, table);
        }
        conn.Close();
    }

    private static string TableWithColumnType(string table, string dataType) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT", "Nullable": false },
      { "Name": "[Val]", "DataType": "{{dataType}}", "Nullable": true }
    ]
  }
]
""";

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"IF OBJECT_ID('[dbo].[{table}]') IS NOT NULL DROP TABLE [dbo].[{table}];";
        cmd.ExecuteNonQuery();
    }

    private string DeployedTypeName(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = $@"
SELECT ISNULL((SELECT t.name FROM sys.columns c
                 JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID('[dbo].[{table}]') AND c.name = '{column}'), '')";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private void ClearChangeAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private int ColumnChangesRecorded(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM SchemaSmith.ChangeAudit
 WHERE SessionId = @@SPID AND ObjectType = 'column' AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
