// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_DropExcludeAndStatisticsTests : BaseTableQuenchTests
{
    // Both exclude constraints are removed from the JSON in the same quench. ExclSuppressed sets
    // DropExcludeConstraintsRemovedFromProduct:false -> survives. ExclControl omits the flag -> drops.
    [Test]
    public void TableQuench_ShouldSuppressExcludeDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'excl_ExclSuppressed' AND contype = 'x')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "excl_ExclSuppressed should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_constraint WHERE conname = 'excl_ExclControl' AND contype = 'x')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "excl_ExclControl should be gone (no suppression flag).");

        conn.Close();
    }

    // Both statistics objects are removed from the JSON in the same quench. StatSuppressed sets
    // DropStatisticsRemovedFromProduct:false -> survives. StatControl omits the flag -> drops.
    [Test]
    public void TableQuench_ShouldSuppressStatisticsDropWhenTableFlagIsFalse()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_statistic_ext WHERE stxname = 'ST_StatSuppressed')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.True, "ST_StatSuppressed should still exist (suppressed by table flag).");

        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_statistic_ext WHERE stxname = 'ST_StatControl')";
        Assert.That(cmd.ExecuteScalar() as bool?, Is.False, "ST_StatControl should be gone (no suppression flag).");

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS btree_gist";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE SCHEMA ""DropExStTests"";
CREATE TABLE ""DropExStTests"".""ExclSuppressed"" (""Id"" INT, ""Val"" INT, CONSTRAINT ""excl_ExclSuppressed"" EXCLUDE USING gist (""Val"" WITH =));
CREATE TABLE ""DropExStTests"".""ExclControl"" (""Id"" INT, ""Val"" INT, CONSTRAINT ""excl_ExclControl"" EXCLUDE USING gist (""Val"" WITH =));
CREATE TABLE ""DropExStTests"".""StatSuppressed"" (""Id"" INT, ""Col2"" INT);
CREATE STATISTICS ""DropExStTests"".""ST_StatSuppressed"" ON ""Id"", ""Col2"" FROM ""DropExStTests"".""StatSuppressed"";
CREATE TABLE ""DropExStTests"".""StatControl"" (""Id"" INT, ""Col2"" INT);
CREATE STATISTICS ""DropExStTests"".""ST_StatControl"" ON ""Id"", ""Col2"" FROM ""DropExStTests"".""StatControl"";
";
        cmd.ExecuteNonQuery();

        // Each pair drops its exclude/statistics from the JSON; the *Suppressed table protects its own.
        var json = """
            [
            {
                "Schema": "DropExStTests", "Name": "ExclSuppressed",
                "DropExcludeConstraintsRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": true }, { "Name": "Val", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "DropExStTests", "Name": "ExclControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": true }, { "Name": "Val", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "DropExStTests", "Name": "StatSuppressed",
                "DropStatisticsRemovedFromProduct": false,
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": true }, { "Name": "Col2", "DataType": "INT", "Nullable": true } ]
            },
            {
                "Schema": "DropExStTests", "Name": "StatControl",
                "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": true }, { "Name": "Col2", "DataType": "INT", "Nullable": true } ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }
}
