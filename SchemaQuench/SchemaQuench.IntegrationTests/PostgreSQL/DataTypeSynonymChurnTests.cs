// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using Npgsql;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL's declared-type synonym mapping, audited against the engine rather than against the list of
/// cases people happened to report.
///
/// <para>The comparison reads the catalog's <c>udt_name</c> — <c>int8</c>, <c>timestamptz</c>, <c>varchar</c>
/// — so every SQL-standard spelling a package might use has to be folded to that name on the authored side
/// before anything compares. <c>ParseTableJsonIntoTempTables</c> does that, and its list was written from
/// reported cases. Auditing it against the engine's own alias table found the four datetime spellings
/// missing: a column declared <c>TIMESTAMP WITH TIME ZONE</c> compared against <c>timestamptz</c> and was
/// re-altered on EVERY quench.</para>
///
/// <para><b>Asserted at the outcome, per Rule 32.</b> Not "the mapping returns X for Y" — declare the column
/// with the spelling, deploy twice, and require the second pass to classify nothing as modified.</para>
/// </summary>
[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.All)]
public class DataTypeSynonymChurnTests : BaseTableQuenchTests
{
    // Left is what a package declares; the comment is the udt_name the catalog reports, which is what the
    // compare actually sees. The first four were the audit's findings; the rest were already mapped and are
    // kept as guards -- a change to the mapping block that loses one of them fails here rather than in a
    // user's deploy log.
    [TestCase("timestamp with time zone", TestName = "Synonym_TIMESTAMP_WITH_TIME_ZONE_DoesNotChurn")]    // -> timestamptz
    [TestCase("time with time zone", TestName = "Synonym_TIME_WITH_TIME_ZONE_DoesNotChurn")]              // -> timetz
    [TestCase("timestamp without time zone", TestName = "Synonym_TIMESTAMP_WITHOUT_TIME_ZONE_DoesNotChurn")] // -> timestamp
    [TestCase("time without time zone", TestName = "Synonym_TIME_WITHOUT_TIME_ZONE_DoesNotChurn")]        // -> time
    [TestCase("timestamp(3) with time zone", TestName = "Synonym_TIMESTAMP_P_WITH_TIME_ZONE_DoesNotChurn")] // -> timestamptz(3)
    [TestCase("bigint", TestName = "Synonym_BIGINT_DoesNotChurn")]                                        // -> int8
    [TestCase("double precision", TestName = "Synonym_DOUBLE_PRECISION_DoesNotChurn")]                    // -> float8
    [TestCase("character varying(50)", TestName = "Synonym_CHARACTER_VARYING_DoesNotChurn")]              // -> varchar
    [TestCase("character(10)", TestName = "Synonym_CHARACTER_DoesNotChurn")]                              // -> bpchar
    [TestCase("decimal(10,2)", TestName = "Synonym_DECIMAL_DoesNotChurn")]                                // -> numeric
    [TestCase("bit varying(8)", TestName = "Synonym_BIT_VARYING_DoesNotChurn")]                           // -> varbit
    // BIT is not a synonym question at all -- it is a LENGTH the compare could not see, so bit(8) churned
    // whichever way it was spelled. The bare forms pin the family defaults: 'bit' IS 'bit(1)', so the two
    // must converge, while bare 'bit varying' is UNLIMITED and must NOT be confused with 'bit varying(1)'.
    [TestCase("bit(8)", TestName = "Synonym_BIT_WithLength_DoesNotChurn")]                                // -> bit(8)
    [TestCase("bit(1)", TestName = "Synonym_BIT_ExplicitDefaultLength_DoesNotChurn")]                     // -> bit
    [TestCase("bit", TestName = "Synonym_BIT_Bare_DoesNotChurn")]                                         // -> bit
    [TestCase("bit varying", TestName = "Synonym_BIT_VARYING_Bare_DoesNotChurn")]                         // -> varbit
    // The datetime family default, declared explicitly. 6 is what the catalog renders as bare, so a
    // package that spells it out compared unequal to its own deployment -- in both spellings.
    [TestCase("timestamp(6) with time zone", TestName = "Synonym_TIMESTAMP_DefaultPrecision_DoesNotChurn")]
    [TestCase("timestamptz(6)", TestName = "Synonym_TIMESTAMPTZ_DefaultPrecision_DoesNotChurn")]
    public void ColumnDeclaredWithASynonym_IsNotRealteredOnRedeploy(string synonym)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        const string schema = "public";
        var tableName = $"SynChurn_{uniqueId}";
        var pkName = $"PK_{tableName}";

        var messages = new List<string>();
        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Notice += (_, e) => messages.Add(e.Notice.MessageText);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = $$"""
{
    "Schema": "{{schema}}",
    "Name": "{{tableName}}",
    "Columns": [
        { "Name": "id",  "DataType": "integer",     "Nullable": false },
        { "Name": "val", "DataType": "{{synonym}}",  "Nullable": true }
    ],
    "Indexes": [
        { "Name": "{{pkName}}", "PrimaryKey": true, "Unique": true, "IndexColumns": "id" }
    ]
}
""";

        try
        {
            // SchemaSmith creates the table on this pass, so nothing is "modified" yet either way. The
            // churn only shows on a redeploy of an unchanged declaration.
            RunTableQuenchProc(cmd, json);

            messages.Clear();
            RunTableQuenchProc(cmd, json);

            var modified = messages.FindAll(m => m.Contains("Modified columns found") && m.Contains(tableName));
            Assert.That(modified, Is.Empty,
                $"a column declared '{synonym}' was re-altered on a redeploy of an unchanged declaration -- the "
                + "catalog reports the base type name, and without that spelling in the synonym mapping the "
                + $"compare reads it as a type change every single time. Notices: {string.Join(" | ", messages)}");
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""{schema}"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    /// <summary>
    /// The other half of the BIT defect, and the worse half. Extraction reads the SAME
    /// <c>ColumnTypeArguments</c> the compare does, so a <c>bit(8)</c> column extracted as bare
    /// <c>bit</c> — and redeploying that package builds <c>bit(1)</c>. That is a silent truncation to
    /// one bit on a round-trip, not a diff someone can review.
    /// </summary>
    [Test]
    public void ExtractionMustNotDropABitColumnsLength()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tableName = $"BitRoundTrip_{uniqueId}";

        using var conn = (NpgsqlConnection)DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Built natively, so the catalog holds exactly what PostgreSQL itself would store.
        cmd.CommandText = $@"
DROP TABLE IF EXISTS ""public"".""{tableName}"";
CREATE TABLE ""public"".""{tableName}"" (
    ""id""    integer NOT NULL,
    ""flags"" bit(8),
    ""mask""  bit varying(16),
    ""one""   bit
);";
        cmd.ExecuteNonQuery();

        try
        {
            cmd.CommandText = $@"SELECT ""SchemaSmith"".""GenerateTableJSON""('public', '{tableName}', 'Name')";
            var json = cmd.ExecuteScalar()?.ToString() ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("bit(8)"),
                    "a bit(8) column must extract WITH its length -- extracted as bare 'bit', redeploying the "
                    + $"package builds bit(1) and silently truncates to one bit. JSON: {json}");
                Assert.That(json, Does.Contain("varbit(16)"),
                    "a bit varying(16) column must extract with its length -- bare 'varbit' is UNLIMITED, so "
                    + $"dropping it silently widens the column on a round-trip. JSON: {json}");
                Assert.That(json, Does.Not.Contain("bit(1)"),
                    "a bare bit column must stay bare -- bit IS bit(1), and emitting the redundant length "
                    + $"would churn every existing package that spells it the short way. JSON: {json}");
            });
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}"";";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }
}
