// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Data-type synonyms must not churn (Rule 20 parity with PostgreSQL, which has had this since its
/// parse step).
///
/// <para>Every engine accepts synonyms and the catalog reports the BASE name whatever the package
/// declared — a column authored <c>INTEGER</c> comes back <c>int</c>. Without normalization the
/// comparison saw a difference that was not one and drop/recreated the column on EVERY quench: not a
/// cosmetic diff, an unnecessary table rewrite on every deploy, forever.</para>
///
/// <para><b>Asserted at the outcome, per Rule 32.</b> Not "the helper returns X for Y" — declare the
/// column with the synonym, deploy twice, and require the column to be untouched. The mapping table
/// itself was measured against both engines rather than assumed, so a test that re-asserted the table
/// would only be checking my arithmetic against itself.</para>
/// </summary>
[Category("Integration")]
public abstract class DataTypeSynonymChurnSharedTests : BaseTableQuenchTests
{
    // Left column is what a user writes; the comment is what the engine reports back. Verified on
    // MySQL 8.0 and MariaDB 11.4 -- identical on both, modulo the integer display width MariaDB adds
    // and StripIntDisplayWidth already handles.
    [TestCase("INTEGER", TestName = "Synonym_INTEGER_DoesNotChurn")]              // -> int
    [TestCase("DEC(10,2)", TestName = "Synonym_DEC_DoesNotChurn")]                // -> decimal(10,2)
    [TestCase("NUMERIC(10,2)", TestName = "Synonym_NUMERIC_DoesNotChurn")]        // -> decimal(10,2)
    [TestCase("FIXED(10,2)", TestName = "Synonym_FIXED_DoesNotChurn")]            // -> decimal(10,2)
    [TestCase("BOOL", TestName = "Synonym_BOOL_DoesNotChurn")]                    // -> tinyint(1)
    [TestCase("BOOLEAN", TestName = "Synonym_BOOLEAN_DoesNotChurn")]              // -> tinyint(1)
    [TestCase("CHARACTER VARYING(50)", TestName = "Synonym_CHARACTER_VARYING_DoesNotChurn")] // -> varchar(50)
    [TestCase("CHARACTER(10)", TestName = "Synonym_CHARACTER_DoesNotChurn")]      // -> char(10)
    public void ColumnDeclaredWithASynonym_IsNotRewrittenOnRedeploy(string synonym)
    {
        var uid = Guid.NewGuid().ToString("N")[..8];
        var product = $"SynProduct_{uid}";
        var table = $"SynTable_{uid}";
        var defs = TableWithColumnType(table, synonym);

        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, defs, productName: product);
            var deployed = GetColumnDataType(cmd, _mainDb, table, "Val");
            Assert.That(deployed, Is.Not.EqualTo("UNKNOWN"), "Setup: the column must deploy.");

            // A rewrite is what churn looks like here, and it is invisible in the column's type --
            // the type is right either way. Watch the audit instead: a second deploy of an unchanged
            // declaration must record no column change at all.
            ClearChangeAudit(cmd);
            RunTableQuenchProc(cmd, defs, productName: product);

            Assert.That(ColumnChangesRecorded(cmd, table), Is.EqualTo(0),
                $"a column declared {synonym} was altered on a redeploy of an unchanged declaration -- "
                + $"the engine reports it as {deployed}, and without synonym normalization the compare "
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
    "Name": "{{table}}",
    "Columns": [
      { "Name": "`Id`",  "DataType": "int", "Nullable": false },
      { "Name": "`Val`", "DataType": "{{dataType}}", "Nullable": true }
    ],
    "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`Id`" } ]
  }
]
""";

    private void DropTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`;";
        cmd.ExecuteNonQuery();
    }

    private void ClearChangeAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private int ColumnChangesRecorded(IDbCommand cmd, string table)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM SchemaSmith_ChangeAudit
 WHERE SessionId = CONNECTION_ID()
   AND ObjectType = 'column'
   AND ObjectName LIKE '{table}.%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
