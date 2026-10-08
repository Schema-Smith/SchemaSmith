// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace DataTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// End-to-end integration tests for DataTongs against PostgreSQL.
/// Tests the complete workflow: extract -> generate script -> apply -> verify.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class DataTongsEndToEndTests
{
    private string _integrationDb = "";
    private string _connectionString = "";
    private Dictionary<string, string> _connProps = null!;
    private IDbConnection _connection = null!;
    // The container's real PG major, threaded into BuildMergeScript so the execute-type tests generate
    // the version-correct upsert (MERGE on 15+, a manual INSERT/UPDATE upsert below 15) for the container.
    private int _pgServerVersionNum;
    private global::DataTongs.DataTongs _dataTongs = null!;
    private string _testOutputDir = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], _connProps);
        _integrationDb = GenerateUniqueDBName("DTE2E");

        CreateTestDatabase();
    }

    [SetUp]
    public void SetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var dbConnString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _integrationDb, config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], _connProps);
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(dbConnString);
        _connection.Open();
        using (var verCmd = _connection.CreateCommand())
        {
            verCmd.CommandText = "SELECT current_setting('server_version_num')::int / 10000";
            _pgServerVersionNum = Convert.ToInt32(verCmd.ExecuteScalar());
        }
        _dataTongs = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        _testOutputDir = Path.Combine(Path.GetTempPath(), $"DataTongsE2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testOutputDir);
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();

        if (Directory.Exists(_testOutputDir))
        {
            try { Directory.Delete(_testOutputDir, true); } catch { /* ignore */ }
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
    }

    [Test]
    public void EndToEnd_ExtractAndReapply_DataMatches()
    {
        using var command = _connection.CreateCommand();
        var sourceTable = $"E2ESrc_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"E2ETgt_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create source table with test data
            command.CommandText = $@"
                CREATE TABLE public.""{sourceTable}"" (
                    ""Id"" INT PRIMARY KEY,
                    ""Code"" VARCHAR(20) NOT NULL,
                    ""Name"" VARCHAR(100),
                    ""Amount"" DECIMAL(10,2),
                    ""CreatedDate"" DATE,
                    ""LastUpdate"" TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO public.""{sourceTable}"" (""Id"", ""Code"", ""Name"", ""Amount"", ""CreatedDate"") VALUES
                (1, 'A001', 'Item One', 100.50, '2024-01-15'),
                (2, 'A002', 'Item Two', 200.75, '2024-02-20'),
                (3, 'A003', 'Item Three', 300.00, '2024-03-25')";
            command.ExecuteNonQuery();

            // Create empty target table with same structure
            command.CommandText = $@"
                CREATE TABLE public.""{targetTable}"" (
                    ""Id"" INT PRIMARY KEY,
                    ""Code"" VARCHAR(20) NOT NULL,
                    ""Name"" VARCHAR(100),
                    ""Amount"" DECIMAL(10,2),
                    ""CreatedDate"" DATE,
                    ""LastUpdate"" TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            // Extract data from source
            var selectColumns = _dataTongs.GetSelectColumns(command, "public", sourceTable);
            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, command, "public", sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", sourceTable, keyColumns, null);

            // Write content file
            var contentFile = Path.Combine(_testOutputDir, $"{sourceTable}.tabledata");
            File.WriteAllText(contentFile, json);

            // Generate merge script for target table (Upsert: mergeUpdate=true, mergeDelete=false)
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, "public", targetTable, json, keyColumns,
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

            // Write script file
            var scriptFile = Path.Combine(_testOutputDir, $"Populate {targetTable}.sql");
            File.WriteAllText(scriptFile, script);

            // Execute script against target table
            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify data matches
            command.CommandText = $@"SELECT COUNT(*) FROM public.""{targetTable}""";
            var targetCount = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(targetCount, Is.EqualTo(3));

            // Verify specific values
            command.CommandText = $@"SELECT ""Code"", ""Name"", ""Amount"" FROM public.""{targetTable}"" WHERE ""Id"" = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("A001"));
            Assert.That(reader.GetString(1), Is.EqualTo("Item One"));
            Assert.That(reader.GetDecimal(2), Is.EqualTo(100.50m));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS public.""{sourceTable}""";
            command.ExecuteNonQuery();
            command.CommandText = $@"DROP TABLE IF EXISTS public.""{targetTable}""";
            command.ExecuteNonQuery();
        }
    }

    // Types whose extraction or delivery used to lose them: bit(8) delivered as bit (one bit), "char" above 127 as an
    // escape older servers cannot read, arrays flattened on '*,*' (dimensions, bounds and delimiter-bearing elements
    // lost), the native point / polygon handed to PostGIS functions, and an unconstrained numeric cast to numeric(0, 0).
    // The script runs with only pg_catalog on the search_path, so the public enum resolves only if the cast names its schema.
    [TestCase("Json")]
    [TestCase("Xml")]
    public void EndToEnd_RoundTrip_TypesThatNeedTheirOwnRendering(string encoding)
    {
        using var command = _connection.CreateCommand();
        var sourceTable = $"E2ETyS_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"E2ETyT_{Guid.NewGuid():N}".Substring(0, 30);
        var moodType = $"e2e_mood_{Guid.NewGuid():N}".Substring(0, 30);
        var columns = $@"""Mood"" public.{moodType}, ""Id"" INT PRIMARY KEY, ""Bits"" BIT(8), ""Flag"" ""char"", ""Grid"" INT[][],
                    ""Offset"" INT[], ""Words"" TEXT[], ""Spot"" POINT, ""Shape"" POLYGON, ""Amount"" NUMERIC";
        try
        {
            command.CommandText = $@"CREATE TYPE public.{moodType} AS ENUM ('calm', 'busy');
                CREATE TABLE public.""{sourceTable}"" ({columns});
                CREATE TABLE public.""{targetTable}"" ({columns});
                INSERT INTO public.""{sourceTable}"" VALUES
                ('busy', 1, B'10100101', (-56)::""char"", '{{{{1,2}},{{3,4}}}}', '[0:1]={{5,6}}', '{{""a*,*b"",c,NULL}}',
                    '(1.5,2)', '((0,0),(1,0),(1,1))', 12345678901234567890.123456789),
                ('calm', 2, B'00000001', 'x', NULL, '{{}}', '{{""""}}', NULL, NULL, 0.5)";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, "public", sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", sourceTable, "\"Id\"", null);
            var content = encoding == "Xml" ? MergeScriptHelper.JsonPayloadToXml(json) : json;
            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, "public", targetTable, content, "\"Id\"",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum, contentEncoding: encoding);
            command.CommandText = "SET search_path TO pg_catalog";
            command.ExecuteNonQuery();
            command.CommandText = script;
            command.ExecuteNonQuery();
            command.CommandText = "RESET search_path";
            command.ExecuteNonQuery();

            command.CommandText = $@"SELECT COALESCE(STRING_AGG(s.""Id"" || '.' || col, ',' ORDER BY s.""Id"", col), '')
                FROM public.""{sourceTable}"" s LEFT JOIN public.""{targetTable}"" d ON d.""Id"" = s.""Id""
                CROSS JOIN LATERAL (VALUES
                    ('Bits', s.""Bits""::text, d.""Bits""::text), ('Flag', s.""Flag""::int::text, d.""Flag""::int::text),
                    ('Grid', s.""Grid""::text, d.""Grid""::text), ('Offset', s.""Offset""::text, d.""Offset""::text),
                    ('Words', s.""Words""::text, d.""Words""::text), ('Spot', s.""Spot""::text, d.""Spot""::text),
                    ('Shape', s.""Shape""::text, d.""Shape""::text), ('Amount', s.""Amount""::text, d.""Amount""::text), ('Mood', s.""Mood""::text, d.""Mood""::text),
                    ('row', '1', CASE WHEN d.""Id"" IS NULL THEN NULL ELSE '1' END)) v(col, src, dst)
                WHERE v.src IS DISTINCT FROM v.dst";
            Assert.That(Convert.ToString(command.ExecuteScalar()), Is.Empty,
                "the list names Id.column for each value that did not arrive equal; extracted: " + json);
        }
        finally
        {
            command.CommandText = $@"RESET search_path; DROP TABLE IF EXISTS public.""{sourceTable}""; DROP TABLE IF EXISTS public.""{targetTable}"";
                DROP TYPE IF EXISTS public.{moodType}";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_PreservesDecimalPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"E2EDec_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with precise decimals
            command.CommandText = $@"
                CREATE TABLE public.""{tableName}"" (
                    ""Id"" INT PRIMARY KEY,
                    ""Price"" DECIMAL(10,4) NOT NULL,
                    ""Quantity"" DECIMAL(15,6)
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO public.""{tableName}"" VALUES
                (1, 123.4567, 9999.123456),
                (2, 0.0001, 0.000001)";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, "public", tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", tableName, "\"Id\"", null);

            // Clear and re-insert via script (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $@"DELETE FROM public.""{tableName}""";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, "public", tableName, json, "\"Id\"",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify precision preserved
            command.CommandText = $@"SELECT ""Price"", ""Quantity"" FROM public.""{tableName}"" WHERE ""Id"" = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetDecimal(0), Is.EqualTo(123.4567m));
            Assert.That(reader.GetDecimal(1), Is.EqualTo(9999.123456m));
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS public.""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_HandlesNullValues()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"E2ENull_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with nullable columns
            command.CommandText = $@"
                CREATE TABLE public.""{tableName}"" (
                    ""Id"" INT PRIMARY KEY,
                    ""OptionalText"" VARCHAR(100),
                    ""OptionalNumber"" INT,
                    ""OptionalDate"" DATE
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO public.""{tableName}"" VALUES
                (1, 'has value', 42, '2024-01-01'),
                (2, NULL, NULL, NULL)";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, "public", tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", tableName, "\"Id\"", null);

            // Clear and re-insert (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $@"DELETE FROM public.""{tableName}""";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform.PostgreSQL, command, "public", tableName, json, "\"Id\"",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null, pgServerVersionNum: _pgServerVersionNum);

            command.CommandText = script;
            command.ExecuteNonQuery();

            // Verify nulls preserved
            command.CommandText = $@"SELECT ""OptionalText"", ""OptionalNumber"" FROM public.""{tableName}"" WHERE ""Id"" = 2";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.IsDBNull(0), Is.True);
            Assert.That(reader.IsDBNull(1), Is.True);
        }
        finally
        {
            command.CommandText = $@"DROP TABLE IF EXISTS public.""{tableName}""";
            command.ExecuteNonQuery();
        }
    }

    #region Database Setup

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"CREATE DATABASE ""{_integrationDb}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{_integrationDb}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    #endregion
}
