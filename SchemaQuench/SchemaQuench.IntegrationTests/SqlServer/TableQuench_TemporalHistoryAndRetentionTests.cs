// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Coverage for the temporal history-table-name / HISTORY_RETENTION_PERIOD depth gap: SchemaSmith modelled
// system-versioned temporal tables as a single bool, so a non-default history table name and a retention
// policy were both silently lost on an extract -> deploy round trip (a retention policy disappearing is
// compliance-shaped loss). Each test owns a UNIQUE product name so it is scoped to its own tables.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_TemporalHistoryAndRetentionTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_CreatesTemporalTableWithNonDefaultHistoryTableNameAndSchema()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"HistNameProduct_{uniqueId}";
        var table = $"HistName_{uniqueId}";
        var customHist = $"{table}_Archive";
        var defaultHist = $"{table}_Hist"; // SchemaSmith's own default -- must NOT exist once a custom name is declared

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            cmd.CommandText = "IF SCHEMA_ID('histtest') IS NULL EXEC('CREATE SCHEMA histtest')";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, WithCustomHistoryTable(table, "[histtest]", $"[{customHist}]"), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(TemporalType(cmd, "dbo", table), Is.EqualTo(2), "the table must be system-versioned");
                Assert.That(ObjectExists(cmd, "histtest", customHist), Is.True, "the declared custom history table must exist");
                Assert.That(ObjectExists(cmd, "dbo", defaultHist), Is.False, "SchemaSmith's own default-named history table must NOT also exist");
            });
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [histtest].[{customHist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_AppliesAndChangesHistoryRetentionPeriodInPlace()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"RetentionProduct_{uniqueId}";
        var table = $"Retention_{uniqueId}";
        var hist = $"{table}_Hist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, WithRetention(table, "1 YEARS"), productName: product);
            Assert.That(TemporalType(cmd, "dbo", table), Is.EqualTo(2), "Setup: table should be system-versioned.");
            Assert.That(RetentionText(cmd, table), Is.EqualTo("1 YEARS"), "the declared retention period must apply on creation");
            var objectIdAfterCreate = ObjectId(cmd, table);

            RunTableQuenchProc(cmd, WithRetention(table, "5 YEARS"), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(RetentionText(cmd, table), Is.EqualTo("5 YEARS"), "a changed retention period must be applied via an in-place ALTER");
                Assert.That(ObjectId(cmd, table), Is.EqualTo(objectIdAfterCreate), "the retention change must be in-place -- the table must not be dropped/recreated");
                Assert.That(ObjectExists(cmd, "dbo", hist), Is.True, "the history table must survive the retention change (it holds data)");
            });
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_IsTemporalOnlyPackageStillDeploysToDefaultHistoryTable()
    {
        // Backward-compat guard: a package declaring only IsTemporal (no HistoryTableSchema/Name/
        // HistoryRetentionPeriod -- the shape every existing package uses) must keep deploying to
        // <Table>_Hist in the same schema exactly as it always has.
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"BackCompatProduct_{uniqueId}";
        var table = $"BackCompat_{uniqueId}";
        var hist = $"{table}_Hist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, IsTemporalOnly(table), productName: product);

            Assert.Multiple(() =>
            {
                Assert.That(TemporalType(cmd, "dbo", table), Is.EqualTo(2), "the table must be system-versioned");
                Assert.That(ObjectExists(cmd, "dbo", hist), Is.True, "the IsTemporal-only package must still deploy to the default <Table>_Hist history table");
                Assert.That(RetentionText(cmd, table), Is.EqualTo("INFINITE"), "no retention declared must leave SQL Server's own default (INFINITE) untouched");
            });
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_ThrowsWhenHistoryTableIdentityDriftsOnAnAlreadyVersionedTable()
    {
        // SQL Server has no in-place ALTER to rename/move the history table of an already-versioned
        // table. SchemaSmith reissues the resolved HISTORY_TABLE unconditionally and lets the ENGINE be
        // the drift detector rather than pre-checking live state itself -- confirmed against a live SQL
        // Server: re-declaring a DIFFERENT history table on an already-versioned table raises the engine's
        // own Msg 13757 ("... already has history table defined. Consider dropping system_versioning
        // first if you want to use different history table."), which names the table, states the cause,
        // and gives the remedy.
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"DriftProduct_{uniqueId}";
        var table = $"Drift_{uniqueId}";
        var hist = $"{table}_Hist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, IsTemporalOnly(table), productName: product);
            Assert.That(TemporalType(cmd, "dbo", table), Is.EqualTo(2), "Setup: table should be system-versioned.");

            var ex = Assert.Throws<SqlException>(() => RunTableQuenchProc(cmd, WithCustomHistoryTable(table, "[dbo]", $"[{table}_Renamed]"), productName: product));
            Assert.That(ex!.Message, Does.Contain("already has history table defined"));
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_RetentionPeriodDeployIsIdempotent()
    {
        // #depth-gap trap: an extraction-only assertion (retention reads back correctly) would pass even
        // while the retention ALTER re-fires on every single deploy -- only a redeploy-with-zero-actions
        // assertion catches that churn. Deploy once with no retention declared (SQL Server's own INFINITE
        // default), then declare "3 YEARS" (a real change -- must audit exactly once), then redeploy the
        // identical "3 YEARS" package a second time (no change -- must add ZERO further audit rows).
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"IdempotentProduct_{uniqueId}";
        var table = $"Idempotent_{uniqueId}";
        var hist = $"{table}_Hist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            RunTableQuenchProc(cmd, IsTemporalOnly(table), productName: product);
            Assert.That(RetentionText(cmd, table), Is.EqualTo("INFINITE"), "Setup: no retention declared yet.");

            RunTableQuenchProc(cmd, WithRetention(table, "3 YEARS"), productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(RetentionText(cmd, table), Is.EqualTo("3 YEARS"), "the declared retention must apply");
                Assert.That(TemporalRetentionChangedAuditCount(cmd, table), Is.EqualTo(1), "a real retention change must audit exactly once");
            });

            RunTableQuenchProc(cmd, WithRetention(table, "3 YEARS"), productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(RetentionText(cmd, table), Is.EqualTo("3 YEARS"), "redeploying the SAME retention must leave it unchanged");
                Assert.That(TemporalRetentionChangedAuditCount(cmd, table), Is.EqualTo(1), "redeploying the SAME retention must add ZERO further audit rows -- the second pass is a true no-op");
            });
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void TableQuench_SingularRetentionUnitDoesNotChurnOnRedeploy()
    {
        // The exact trap: sys.tables reports the retention unit SINGULAR (history_retention_period_unit_desc
        // = 'YEAR'), but a naive comparison of the DECLARED text against a canonical-plural live read would
        // never match a singular-unit declaration ("5 YEAR") -- churning the ALTER (and the audit row)
        // forever. fn_NormalizeTemporalRetentionPeriod normalizes the declared side at PARSE time so a
        // singular declaration compares identically to its plural spelling on every redeploy.
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var product = $"SingularProduct_{uniqueId}";
        var table = $"Singular_{uniqueId}";
        var hist = $"{table}_Hist";

        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        try
        {
            // Deploy with the SINGULAR unit spelling ("5 YEAR") -- valid DDL, but never what a live-state
            // read would produce on its own.
            RunTableQuenchProc(cmd, WithRetention(table, "5 YEAR"), productName: product);
            Assert.Multiple(() =>
            {
                Assert.That(RetentionText(cmd, table), Is.EqualTo("5 YEARS"), "the singular declaration must apply (SQL Server's own DDL accepts both spellings)");
                Assert.That(TemporalRetentionChangedAuditCount(cmd, table), Is.EqualTo(0), "the initial apply rides the table's OFF->ON transition, not the separate retention-update block");
            });

            // Redeploy the IDENTICAL singular declaration again -- if the declared side weren't normalized
            // at parse time, this would compare "5 YEAR" against the canonical-plural live read "5 YEARS"
            // and churn on every single redeploy.
            RunTableQuenchProc(cmd, WithRetention(table, "5 YEAR"), productName: product);
            Assert.That(TemporalRetentionChangedAuditCount(cmd, table), Is.EqualTo(0), "redeploying the same singular-unit retention must add ZERO audit rows");
        }
        finally
        {
            cmd.CommandText = $@"
IF OBJECT_ID('dbo.{table}') IS NOT NULL AND OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2
    ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}];
DROP TABLE IF EXISTS [dbo].[{hist}];";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string IsTemporalOnly(string table) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "IsTemporal": true,
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string WithCustomHistoryTable(string table, string historySchema, string historyName) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "IsTemporal": true,
    "HistoryTableSchema": "{{historySchema}}",
    "HistoryTableName": "{{historyName}}",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static string WithRetention(string table, string retention) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "IsTemporal": true,
    "HistoryRetentionPeriod": "{{retention}}",
    "Columns": [
      { "Name": "[Id]",       "DataType": "INT",          "Nullable": false },
      { "Name": "[Somedata]", "DataType": "VARCHAR(100)", "Nullable": false }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "IndexColumns": "[Id]" } ]
  }
]
""";

    private static int TemporalType(IDbCommand cmd, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT CAST(ISNULL(OBJECTPROPERTY(OBJECT_ID('{schema}.{tableName}'), 'TableTemporalType'), -1) AS INT)";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool ObjectExists(IDbCommand cmd, string schema, string tableName)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('{schema}.{tableName}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static int ObjectId(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT OBJECT_ID('dbo.{tableName}')";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Reads the raw catalog columns and pluralizes in C# -- deliberately NOT a copy of the production
    // numeric-code CASE, which is exactly what went wrong the first time (a first pass mapped
    // 1/2/3/4->DAY/WEEK/MONTH/YEAR from documentation; measured live, the real codes are 3/4/5/6).
    // history_retention_period_unit_desc removes the numeric-code guesswork entirely: it returns
    // DAY/WEEK/MONTH/YEAR/INFINITE directly, and all four finite units pluralize by simple concatenation.
    private static string RetentionText(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT history_retention_period, history_retention_period_unit_desc
  FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var period = Convert.ToInt32(reader[0]);
        var unitDesc = reader[1].ToString();
        return unitDesc switch
        {
            "INFINITE" => "INFINITE",
            "DAY" or "WEEK" or "MONTH" or "YEAR" => $"{period} {unitDesc}S",
            _ => throw new InvalidOperationException($"Unrecognized SYSTEM_VERSIONING retention unit: {unitDesc}")
        };
    }

    // Counts the 'changed' audit rows the retention-update block itself inserts (ObjectType = 'temporal',
    // ObjectName = '<schema>.<table> (retention)') -- an extraction-only assertion would pass even while
    // this count kept climbing on every redeploy, which is exactly the churn the idempotency tests catch.
    private static int TemporalRetentionChangedAuditCount(IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ActionType = 'changed' AND ObjectType = 'temporal' AND ObjectName = '[dbo].[{tableName}] (retention)'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
