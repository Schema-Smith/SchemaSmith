// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration tests for MergeScriptHelper against PostgreSQL.
/// Uses dynamically created test databases via FixtureSetup.
/// Tests special data type handling: JSON, JSONB, XML.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class MergeScriptHelperIntegrationTests
{
    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        // This fixture simulates modern (>=15) PostgreSQL MERGE targets and executes the generated
        // scripts to verify they run. MERGE is a v15 feature, so on a real <15 server those scripts
        // cannot execute — they fail inside plpgsql ("only" is not a known variable). Skip the fixture
        // on an older server EXCEPT the real-version-adaptive test, which is exactly the intended
        // below-15 (legacy-upsert) coverage; generation-shape is fully covered on the modern CI leg.
        // Without this, the PostgreSQL 14 matrix leg runs modern-only execution tests against a server
        // that cannot run them (production is unaffected — it generates for the detected version).
        using var probe = _connection.CreateCommand();
        if (TargetVersionDetector.Detect(probe, Platform.PostgreSQL).ServerComparable < 15
            && TestContext.CurrentContext.Test.MethodName is not (nameof(BuildMergeScript_NativeVersion_DeletesRowsAbsentFromSource)
                or nameof(GetKeyColumns_TakesOnlyAWholeTableUniqueKey_AndTheUpsertKeepsEveryRow)))
            Assert.Ignore("MergeScriptHelper modern-MERGE execution tests require PostgreSQL 15+; the below-15 path is covered by BuildMergeScript_NativeVersion and the SchemaQuench legacy-upsert integration tests.");
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    #region Key Selection Tests

    // The merge key was the first unique index of any kind. A partial one is not unique across the table, so rows
    // sharing its value collapsed (below 15) or failed MERGE (15+); an expression's key part matched no column, so
    // the key shrank; and INCLUDE columns widened it, so a changed included value inserted a duplicate.
    [TestCase("CREATE UNIQUE INDEX {0} ON public.\"{1}\" (grp) WHERE v > 0")]
    [TestCase("CREATE UNIQUE INDEX {0} ON public.\"{1}\" (lower(name))")]
    [TestCase(null)]
    public void GetKeyColumns_TakesOnlyAWholeTableUniqueKey_AndTheUpsertKeepsEveryRow(string decoyIndex)
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_key_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE public.""{tableName}"" (id INT NOT NULL, grp INT NOT NULL, v INT NOT NULL, name TEXT NOT NULL);
                {(decoyIndex == null ? "" : string.Format(decoyIndex, $"\"{tableName[..30]}_d\"", tableName) + ";")}
                CREATE UNIQUE INDEX ""{tableName[..30]}_k"" ON public.""{tableName}"" (id) INCLUDE (v);
                INSERT INTO public.""{tableName}"" VALUES (1, 1, 5, 'A'), (2, 1, -2, 'b');
                SELECT current_setting('server_version_num')::int / 10000";
            var serverMajor = Convert.ToInt32(command.ExecuteScalar());

            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, command, "public", tableName);
            Assert.That(keyColumns, Is.EqualTo("\"id\""));

            const string tableData = @"[{""id"":1,""grp"":1,""v"":7,""name"":""A""},{""id"":2,""grp"":1,""v"":-2,""name"":""b""}]";
            command.CommandText = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, "public", tableName, tableData,
                keyColumns, mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false,
                mergeFilter: null, pgServerVersionNum: serverMajor);
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT STRING_AGG(id || ':' || grp || ':' || v || ':' || name, ',' ORDER BY id) FROM public.""{tableName}""";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:1:7:A,2:1:-2:b"));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS public.""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region JSON Tests

    [Test]
    public void BuildMergeScript_JsonColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_json_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""metadata"" JSON NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""metadata"":{""key"":""value"",""nested"":{""a"":1}}}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // JSON columns should use ::text cast for comparison
            Assert.That(script, Does.Contain("::text"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""metadata""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("key"));
            Assert.That(result, Does.Contain("value"));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Quoted Identifier Tests

    [Test]
    public void BuildMergeScript_TableNameWithSingleQuote_GeneratesValidScript()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz't_{Guid.NewGuid():N}";

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName.Replace("\"", "\"\"")}"" (
    ""id"" INT PRIMARY KEY,
    ""name"" VARCHAR(50) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Alpha""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            Assert.That(script, Is.Not.Null);
            Assert.That(script, Does.Contain("MERGE INTO"));
            Assert.That(script, Does.Contain(@"""name"""));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""name"" FROM ""public"".""{tableName.Replace("\"", "\"\"")}"" WHERE ""id"" = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Alpha"));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName.Replace("\"", "\"\"")}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region JSONB Tests

    [Test]
    public void BuildMergeScript_JsonbColumn_ScriptContainsJsonbCast()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_jsonb_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""settings"" JSONB NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""settings"":{""z_key"":""last"",""a_key"":""first""}}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // JSONB columns should use ::jsonb cast for comparison (not ::text, since JSONB normalizes key order)
            Assert.That(script, Does.Contain("::jsonb"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""settings""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = command.ExecuteScalar()?.ToString();
            // JSONB normalizes key ordering alphabetically
            Assert.That(result, Does.Contain("a_key"));
            Assert.That(result, Does.Contain("z_key"));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region XML Tests

    [Test]
    public void BuildMergeScript_XmlColumn_RoundTripWithTextCast()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_xml_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""config"" XML NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""config"":""<root><item key=\""a\"" value=\""1\"" /></root>""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // XML columns use ::text cast for comparison
            Assert.That(script, Does.Contain("::text"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""config""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("<root>"));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Network Type Verification Tests

    [Test]
    public void BuildMergeScript_NetworkTypes_CanBeExecuted_Verification()
    {
        using var cmd = _connection.CreateCommand();
        var tableName = $"_test_net_{Guid.NewGuid():N}"[..30];

        try
        {
            cmd.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""ip_addr"" INET,
    ""network"" CIDR,
    ""mac"" MACADDR
)";
            cmd.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""ip_addr"":""192.168.1.1/24"",""network"":""10.0.0.0/8"",""mac"":""08:00:2b:01:02:03""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            cmd.CommandText = script;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

            cmd.CommandText = $@"SELECT ""ip_addr""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("192.168.1.1/24"));

            cmd.CommandText = $@"SELECT ""network""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("10.0.0.0/8"));

            cmd.CommandText = $@"SELECT ""mac""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("08:00:2b:01:02:03"));
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            cmd.ExecuteNonQuery();
        }
    }

    #endregion

    #region Range Type Verification Tests

    [Test]
    public void BuildMergeScript_RangeTypes_CanBeExecuted_Verification()
    {
        using var cmd = _connection.CreateCommand();
        var tableName = $"_test_rng_{Guid.NewGuid():N}"[..30];

        try
        {
            cmd.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""int_range"" INT4RANGE,
    ""ts_range"" TSRANGE
)";
            cmd.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""int_range"":""[1,10)"",""ts_range"":""[2024-01-01 00:00:00,2024-12-31 23:59:59)""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            cmd.CommandText = script;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

            cmd.CommandText = $@"SELECT ""int_range""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[1,10)"));
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            cmd.ExecuteNonQuery();
        }
    }

    #endregion

    #region Multirange Type Verification Tests

    [Test]
    public void BuildMergeScript_MultirangeType_CanBeExecuted_Verification()
    {
        using var cmd = _connection.CreateCommand();
        var tableName = $"_test_mrng_{Guid.NewGuid():N}"[..30];

        try
        {
            cmd.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""int_multirange"" INT4MULTIRANGE
)";
            cmd.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""int_multirange"":""{[1,5),[10,20)}""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            cmd.CommandText = script;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

            cmd.CommandText = $@"SELECT ""int_multirange""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("{[1,5),[10,20)}"));
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            cmd.ExecuteNonQuery();
        }
    }

    #endregion

    #region Interval Type Verification Tests

    [Test]
    public void BuildMergeScript_IntervalType_CanBeExecuted_Verification()
    {
        using var cmd = _connection.CreateCommand();
        var tableName = $"_test_intv_{Guid.NewGuid():N}"[..30];

        try
        {
            cmd.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""duration"" INTERVAL
)";
            cmd.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""duration"":""1 year 2 mons 3 days 04:05:06""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, cmd,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            cmd.CommandText = script;
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));

            cmd.CommandText = $@"SELECT ""duration""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = cmd.ExecuteScalar()?.ToString();
            Assert.That(result, Does.Contain("1 year"));
            Assert.That(result, Does.Contain("2 mons"));
            Assert.That(result, Does.Contain("3 days"));
            Assert.That(result, Does.Contain("04:05:06"));
        }
        finally
        {
            cmd.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            cmd.ExecuteNonQuery();
        }
    }

    #endregion

    #region PG<17 Delete-on-Absence Fallback Tests (#241)

    [Test]
    public void BuildMergeScript_Pg16Fallback_DeletesRowsAbsentFromSource()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_del16_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""val"" INT NOT NULL
)";
            command.ExecuteNonQuery();
            command.CommandText = $@"INSERT INTO ""public"".""{tableName}"" (""id"", ""val"") VALUES (1, 10), (2, 20)";
            command.ExecuteNonQuery();

            // Source has only id=1 -> id=2 must be deleted on absence.
            var tableData = @"[{""id"":1,""val"":11}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null,
                disableRules: false, updateDescendents: false,
                destSchemaOverride: null, pgServerVersionNum: 16);

            Assert.That(script, Does.Contain("WHERE NOT EXISTS"));
            Assert.That(script, Does.Not.Contain("WHEN NOT MATCHED BY SOURCE"));

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "id=2 should have been deleted on absence");

            command.CommandText = $@"SELECT ""val"" FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(11), "id=1 should have been updated");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_NativeVersion_DeletesRowsAbsentFromSource()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_delnat_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""val"" INT NOT NULL
)";
            command.ExecuteNonQuery();
            command.CommandText = $@"INSERT INTO ""public"".""{tableName}"" (""id"", ""val"") VALUES (1, 10), (2, 20)";
            command.ExecuteNonQuery();

            var pgVer = TargetVersionDetector.Detect(command, Platform.PostgreSQL).ServerComparable;

            var tableData = @"[{""id"":1,""val"":11}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null,
                disableRules: false, updateDescendents: false,
                destSchemaOverride: null, pgServerVersionNum: pgVer);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1), "id=2 should have been deleted on absence");

            command.CommandText = $@"SELECT ""val"" FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(11), "id=1 should have been updated");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_Pg16Fallback_WithMergeFilter_RespectsTargetAlias()
    {
        // Execution-verifies that the "Target"-qualified mergeFilter resolves correctly in the
        // PG<17 fallback DELETE after D1's fix aliased the target table AS "Target".
        // Row id=2 is absent from source AND matches the filter (keep=true) -> must be deleted.
        // Row id=3 is absent from source but does NOT match the filter (keep=false) -> must survive.
        using var command = _connection.CreateCommand();
        var tableName = $"_test_delf16_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""val"" INT NOT NULL,
    ""keep"" BOOLEAN NOT NULL
)";
            command.ExecuteNonQuery();
            command.CommandText = $@"INSERT INTO ""public"".""{tableName}"" (""id"", ""val"", ""keep"") VALUES (1, 10, true), (2, 20, true), (3, 30, false)";
            command.ExecuteNonQuery();

            // Source has only id=1; mergeFilter restricts deletion to rows where keep = true.
            var tableData = @"[{""id"":1,""val"":11,""keep"":true}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: @"""Target"".""keep"" = true",
                disableRules: false, updateDescendents: false,
                destSchemaOverride: null, pgServerVersionNum: 16);

            Assert.That(script, Does.Contain("NOT EXISTS"), "fallback DELETE must be present");
            Assert.That(script, Does.Not.Contain("WHEN NOT MATCHED BY SOURCE"), "PG<17 path must not emit MERGE BY SOURCE");
            Assert.That(script, Does.Contain(@"""Target"""), "Target alias must appear in the emitted DELETE");

            // Must not throw "missing FROM-clause entry for table Target".
            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2),
                "id=2 (absent, keep=true) deleted; id=3 (absent, keep=false) survives");

            command.CommandText = $@"SELECT ""id"" FROM ""public"".""{tableName}"" ORDER BY ""id""";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetInt32(0), Is.EqualTo(1), "id=1 (in source) must survive");
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetInt32(0), Is.EqualTo(3), "id=3 (absent, keep=false) must survive");
            Assert.That(reader.Read(), Is.False, "no more rows");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region NULL-safe Key (*-prefix) MERGE-ON Tests

    [Test]
    public void BuildMergeScript_NullSafeStarKey_ModernMergePath_MatchesNullKeyRow()
    {
        // Exercises BuildPostgreSqlMatchColumns on the modern MERGE ON path (PG17+ native).
        // A '*'-prefixed key is a NULL-safe marker: the emitted column must strip the '*'
        // (so no literal "*key" reference) and both aliases must be quoted ("Source"/"Target").
        // The old broken builder emitted "Source"."*key" + an unquoted Source. -> PG error here.
        using var command = _connection.CreateCommand();
        var tableName = $"_test_nullkey_{Guid.NewGuid():N}"[..40];
        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""key"" INT,
    ""val"" INT NOT NULL
)";
            command.ExecuteNonQuery();
            // Seed a NULL-key row (to be matched/updated) and a normal-key row (must be untouched).
            command.CommandText = $@"INSERT INTO ""public"".""{tableName}"" (""key"", ""val"") VALUES (NULL, 10), (1, 20)";
            command.ExecuteNonQuery();

            // Source carries a NULL-key row with a new value; '*'-prefix on "key" requests NULL-safe match.
            var tableData = @"[{""key"":null,""val"":11},{""key"":1,""val"":21}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"*""key""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Modern path must emit the native MERGE ON, no literal '*' in the correspondence.
            Assert.That(script, Does.Contain("MERGE INTO"));
            Assert.That(script, Does.Not.Contain(@"""*"));

            // Would throw on the old broken SQL (non-existent "*key" column / unqualified Source.).
            command.CommandText = script;
            command.ExecuteNonQuery();

            // NULL-safe match fired: the NULL-key row was UPDATED (10 -> 11), not duplicated.
            command.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2),
                "NULL-key source row must match the existing NULL-key target row (no duplicate insert)");

            command.CommandText = $@"SELECT ""val"" FROM ""public"".""{tableName}"" WHERE ""key"" IS NULL";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(11),
                "NULL-key row must have been updated, proving the NULL-safe correspondence matched");

            command.CommandText = $@"SELECT ""val"" FROM ""public"".""{tableName}"" WHERE ""key"" = 1";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(21),
                "normal-key row must have been updated by its exact match");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Full-Sync Delivery Regression Tests (#329)

    [Test]
    public void BuildMergeScript_FullSyncDelivery_PreV17_OmitsByTarget_AndExecutes()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_fullsync_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""name"" VARCHAR(50) NOT NULL
)";
            command.ExecuteNonQuery();

            command.CommandText = $@"INSERT INTO ""public"".""{tableName}"" (""id"", ""name"") VALUES (1, 'stale'), (99, 'orphan')";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""fresh""},{""id"":2,""name"":""new""}]";

            // Simulate a PostgreSQL 16 target. pgServerVersionNum is a major-version-only int
            // throughout the codebase (TargetVersionDetector/VersionHelper divide PostgreSQL's raw
            // current_setting('server_version_num') by 10000 before it's ever threaded through), so
            // 16 (not the raw 160013 form) is what production callers (DatabaseQuench) actually pass.
            var preV17Script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: 16);

            // Bare "BY TARGET" also appears in the emitted script's explanatory SQL comment
            // regardless of version, so assert on the actual clause syntax, not the substring.
            Assert.That(preV17Script, Does.Not.Contain("WHEN NOT MATCHED BY TARGET"),
                "On PostgreSQL < 17, the INSERT clause must be a plain 'WHEN NOT MATCHED THEN' — 'BY TARGET' is v17+ only and errors with 42601 on 16.");

            // The <17 script must still run (this is the whole point of the fix).
            command.CommandText = preV17Script;
            command.ExecuteNonQuery();

            // Full-sync semantics: id=1 updated, id=2 inserted, id=99 (orphan) deleted.
            command.CommandText = $@"SELECT COUNT(*) FROM ""public"".""{tableName}""";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2),
                "Full-sync should leave exactly the 2 source rows (orphan deleted).");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_FullSyncDelivery_ModernVersion_UsesByTarget()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_modern_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""name"" VARCHAR(50) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""fresh""}]";

            // pgServerVersionNum: 0 => treat as modern (v17+); the emitted MERGE uses BY TARGET / BY SOURCE.
            var modernScript = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: true, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: 0);

            Assert.That(modernScript, Does.Contain("WHEN NOT MATCHED BY TARGET"),
                "On PostgreSQL 17+ the INSERT clause uses the explicit BY TARGET form.");
            Assert.That(modernScript, Does.Contain("WHEN NOT MATCHED BY SOURCE"),
                "On PostgreSQL 17+ the DELETE clause is the modern MERGE ... BY SOURCE form.");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region Time/Timestamp Precision Tests (C1-0f — is the C1-0a defect reachable here?)

    // MergeScriptHelper's PostgreSQL parseType builder (GetColumnMetadataPostgreSql) and its embedded-SQL
    // twin (GetJsonColumnDefinitionsPostgreSql) both already carry an explicit time/timestamp precision
    // case, unlike the SQL Server pair. These two tests pin that as permanent regression coverage rather
    // than leaving it unverified.

    [Test]
    public void BuildMergeScript_TimeColumnWithPrecision_RoundTripsSubSecondPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_time_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""t"" TIME(3) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""t"":""13:45:30.123""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT ""t""::text FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("13:45:30.123"),
                "A TIME(3) delivery must preserve its declared millisecond precision exactly.");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_TimestamptzColumnWithPrecision_RoundTripsSubSecondPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_tstz_{Guid.NewGuid():N}"[..40];

        try
        {
            command.CommandText = $@"CREATE TABLE ""public"".""{tableName}"" (
    ""id"" INT PRIMARY KEY,
    ""t"" TIMESTAMPTZ(3) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""t"":""2024-06-15T10:30:00.123+00:00""}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command,
                "public", tableName, tableData, @"""id""",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            command.CommandText = script;
            command.ExecuteNonQuery();

            // Read back at a fixed offset (UTC) so the assertion isn't hostage to the session timezone.
            command.CommandText = $@"SELECT to_char(""t"" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS') FROM ""public"".""{tableName}"" WHERE ""id"" = 1";
            var result = command.ExecuteScalar()?.ToString();
            Assert.That(result, Is.EqualTo("2024-06-15 10:30:00.123"),
                "A TIMESTAMPTZ(3) delivery must preserve its declared millisecond precision exactly.");
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS ""public"".""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #endregion
}
