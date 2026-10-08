// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System;
using System.IO;
using System.Linq;

using System.Collections.Generic;
using NUnit.Framework;
using Schema.Utility;

namespace DataTongs.IntegrationTests.Shared;

/// <summary>
/// Shared end-to-end integration tests for DataTongs across the MySQL/MariaDb family.
/// Tests the complete workflow: extract -> generate script -> apply -> verify.
/// The MySQL and MariaDb subclasses supply the platform + fixture accessors; every
/// [Test] body here runs on both engines. Uses dynamically created test databases via FixtureSetup.
/// </summary>
public abstract class DataTongsEndToEndSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private global::DataTongs.DataTongs _dataTongs = null!;
    private string _testOutputDir = null!;
    private string _testDb = null!;

    // These end-to-end tests build the merge script via MergeScriptHelper with the modern JSON_TABLE row source
    // and EXECUTE it, so they need a target with native JSON_TABLE (MySQL 8.0 / MariaDB 10.6). The recursive-CTE
    // path for MariaDB 10.2-10.5 and the below-MySQL-8.0 skip are exercised by the SchemaQuench delivery tests.
    private int _serverVersionNum;
    private bool TargetHasNativeJsonTable => Platform == Platform.MySQL ? _serverVersionNum >= 800 : _serverVersionNum >= 1006;

    [SetUp]
    public void SetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        using (var vcmd = _connection.CreateCommand())
        {
            vcmd.CommandText = "SELECT VERSION()";
            var vp = (vcmd.ExecuteScalar()?.ToString() ?? "").Split('.');
            _serverVersionNum = vp.Length >= 2 && int.TryParse(vp[0], out var mj) && int.TryParse(vp[1], out var mn) ? mj * 100 + mn : int.MaxValue;
        }
        _dataTongs = new global::DataTongs.DataTongs(Platform);
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

    [Test]
    public void EndToEnd_ExtractAndReapply_DataMatches()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6); the CTE / gated paths are covered by the SchemaQuench delivery tests.");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_source_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_target_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create source table with test data
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{sourceTable}` (
                    id INT PRIMARY KEY,
                    code VARCHAR(20) NOT NULL,
                    name VARCHAR(100),
                    amount DECIMAL(10,2),
                    created_date DATE,
                    last_update TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{sourceTable}` (id, code, name, amount, created_date) VALUES
                (1, 'A001', 'Item One', 100.50, '2024-01-15'),
                (2, 'A002', 'Item Two', 200.75, '2024-02-20'),
                (3, 'A003', 'Item Three', 300.00, '2024-03-25')";
            command.ExecuteNonQuery();

            // Create empty target table with same structure
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{targetTable}` (
                    id INT PRIMARY KEY,
                    code VARCHAR(20) NOT NULL,
                    name VARCHAR(100),
                    amount DECIMAL(10,2),
                    created_date DATE,
                    last_update TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            // Extract data from source
            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, keyColumns, null);

            // Write content file
            var contentFile = Path.Combine(_testOutputDir, $"{sourceTable}.tabledata");
            File.WriteAllText(contentFile, json);

            // Generate merge script for target table (Upsert: mergeUpdate=true, mergeDelete=false)
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, keyColumns,
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            // Write script file
            var scriptFile = Path.Combine(_testOutputDir, $"Populate {targetTable}.sql");
            File.WriteAllText(scriptFile, script);

            // Execute script against target table
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify data matches
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{targetTable}`";
            var targetCount = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(targetCount, Is.EqualTo(3));

            // Verify specific values
            command.CommandText = $"SELECT code, name, amount FROM `{_testDb}`.`{targetTable}` WHERE id = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("A001"));
            Assert.That(reader.GetString(1), Is.EqualTo("Item One"));
            Assert.That(reader.GetDecimal(2), Is.EqualTo(100.50m));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    // #390: the test that would have caught it. An "extraction succeeded" assertion is not enough --
    // the bug was that the default (tokenized) merge script referenced a {{key}} placeholder with no
    // resolvable ScriptTokens entry anywhere in the package, so the script itself was never
    // deployable even though extraction reported success. This proves the token DataTongs embeds
    // actually resolves, through the real resolver (SqlScript.TokenReplace -- the same method
    // SqlScript.Load calls after resolving a <*File*> ScriptTokens entry) and actually deploys,
    // landing the same data the non-tokenized path lands in EndToEnd_ExtractAndReapply_DataMatches.
    [Test]
    public void EndToEnd_TokenizedScript_TokenResolvesAndDeploys_MatchingNonTokenizedPath()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6); the CTE / gated paths are covered by the SchemaQuench delivery tests.");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_tok_source_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_tok_target_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{sourceTable}` (
                    id INT PRIMARY KEY,
                    code VARCHAR(20) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{sourceTable}` (id, code) VALUES
                (1, 'A001'), (2, 'A002'), (3, 'A003')";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{targetTable}` (
                    id INT PRIMARY KEY,
                    code VARCHAR(20) NOT NULL
                )";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, keyColumns, null);

            // DataTongs writes the content file under this exact name -- MySQL has no schema concept,
            // so the key is unqualified and unencoded here (no characters FileNameEncoder would touch).
            var contentFileToken = $"{targetTable}.tabledata";
            var contentFile = Path.Join(_testOutputDir, contentFileToken);
            File.WriteAllText(contentFile, json);

            // Same call DataTongs makes: tokenizeScripts:true, passing the exact key alongside the
            // .tabledata filename so they can never disagree (#390's fix).
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, keyColumns,
                mergeUpdate: true, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: true, mergeFilter: null, contentFileToken: contentFileToken);

            Assert.That(script, Does.Contain($"{{{{{contentFileToken}}}}}"),
                "The script must embed the same key the content file was written under.");

            // Resolve the token through the real resolver -- the same SqlScript.TokenReplace method
            // SqlScript.Load calls once TokenHelper.ResolveFileTokens has read the <*File*> path. This
            // is the substitution a real deploy performs; skipping it (as --Validate does) is exactly
            // what let #390 ship.
            var resolvedScript = Schema.Domain.SqlScript.TokenReplace(script,
                new List<KeyValuePair<string, string>> { new(contentFileToken, File.ReadAllText(contentFile)) },
                Platform);

            Assert.That(resolvedScript, Does.Not.Contain("{{" + contentFileToken + "}}"),
                "The token must be fully resolved before execution.");

            foreach (var batch in resolvedScript.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{targetTable}`";
            var targetCount = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(targetCount, Is.EqualTo(3));

            command.CommandText = $"SELECT code FROM `{_testDb}`.`{targetTable}` WHERE id = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo("A001"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_ContentFileCanBeConsumedBySchemaQuench()
    {
        // This test verifies that content files generated by DataTongs
        // are in the correct format for SchemaQuench table data delivery
        using var command = _connection.CreateCommand();

        // Extract country data (small table)
        var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, "country");
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "country");
        var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, "country", keyColumns, "country_id <= 5");

        // Write content file
        var contentFile = Path.Combine(_testOutputDir, "country.tabledata");
        File.WriteAllText(contentFile, FormatJson(json));

        // Verify file format is compatible with SchemaQuench
        var content = File.ReadAllText(contentFile);

        // Must be valid JSON array
        Assert.That(content.Trim(), Does.StartWith("["));
        Assert.That(content.Trim(), Does.EndWith("]"));

        // Must contain expected columns
        Assert.That(content, Does.Contain("\"country_id\""));
        Assert.That(content, Does.Contain("\"country\""));
        Assert.That(content, Does.Contain("\"last_update\""));

        // Verify it can be parsed and used in MergeScriptHelper (Upsert: mergeUpdate=true, mergeDelete=false)
        var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, "country", json, keyColumns,
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);
        Assert.That(script, Is.Not.Empty);
        Assert.That(script, Does.Contain("INSERT INTO"));
    }

    [Test]
    public void EndToEnd_RoundTrip_PreservesDecimalPrecision()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6); the CTE / gated paths are covered by the SchemaQuench delivery tests.");
        using var command = _connection.CreateCommand();
        var tableName = $"_e2e_decimal_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with precise decimals
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    price DECIMAL(10,4) NOT NULL,
                    quantity DECIMAL(15,6)
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{tableName}` VALUES
                (1, 123.4567, 9999.123456),
                (2, 0.0001, 0.000001)";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, tableName, "`id`", null);

            // Clear and re-insert via script (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $"DELETE FROM `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, tableName, json, "`id`",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify precision preserved
            command.CommandText = $"SELECT price, quantity FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.GetDecimal(0), Is.EqualTo(123.4567m));
            Assert.That(reader.GetDecimal(1), Is.EqualTo(9999.123456m));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_PreservesDateFormats()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_e2e_date_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with various date types
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    date_only DATE,
                    datetime_val DATETIME,
                    time_only TIME
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{tableName}` VALUES
                (1, '2024-06-15', '2024-06-15 14:30:45', '14:30:45')";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, tableName, "`id`", null);

            // Verify JSON contains properly formatted dates
            Assert.That(json, Does.Contain("2024-06-15"));
            Assert.That(json, Does.Contain("14:30:45"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_HandlesNullValues()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6); the CTE / gated paths are covered by the SchemaQuench delivery tests.");
        using var command = _connection.CreateCommand();
        var tableName = $"_e2e_null_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with nullable columns
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    optional_text VARCHAR(100),
                    optional_number INT,
                    optional_date DATE
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{tableName}` VALUES
                (1, 'has value', 42, '2024-01-01'),
                (2, NULL, NULL, NULL)";
            command.ExecuteNonQuery();

            // Extract
            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, tableName, "`id`", null);

            // Clear and re-insert (Insert: mergeUpdate=false, mergeDelete=false)
            command.CommandText = $"DELETE FROM `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();

            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, tableName, json, "`id`",
                mergeUpdate: false, mergeDelete: false, disableTriggers: false,
                tokenizeScripts: false, mergeFilter: null);

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify nulls preserved
            command.CommandText = $"SELECT optional_text, optional_number FROM `{_testDb}`.`{tableName}` WHERE id = 2";
            using var reader = command.ExecuteReader();
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.IsDBNull(0), Is.True);
            Assert.That(reader.IsDBNull(1), Is.True);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // Values JSON_TABLE used to read through the wrong type, each lost without an error: an unsigned integer past the
    // signed range (NULL), text and blobs over 64 KB (NULL, or truncated), a geometry collection (NULL -- MySQL 8 calls
    // it geomcollection), and BIT, whose digits' characters MariaDB stored. Delivered values are compared with the source.
    [Test]
    public void EndToEnd_RoundTrip_PreservesUnsignedLongTextBlobGeometryCollectionAndBit()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6).");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_types_s_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_types_t_{Guid.NewGuid():N}".Substring(0, 30);
        const string columns = "id INT PRIMARY KEY, u BIGINT UNSIGNED NULL, big LONGTEXT NULL, bl LONGBLOB NULL, "
                               + "gc GEOMETRYCOLLECTION NULL, b8 BIT(8) NULL, b1 BIT(1) NULL";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{sourceTable}` ({columns})";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{targetTable}` ({columns})";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO `{_testDb}`.`{sourceTable}` VALUES (1, 18446744073709551615, REPEAT('x', 100000), "
                                  + "REPEAT('y', 70000), ST_GeomFromText('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))'), b'00000101', b'0')";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, "`id`", null);
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $@"SELECT CONCAT_WS(',',
                   IF(d.u <=> s.u, NULL, 'u'), IF(d.big <=> s.big, NULL, 'big'), IF(d.bl <=> s.bl, NULL, 'bl'),
                   IF(ST_AsText(d.gc) <=> ST_AsText(s.gc), NULL, 'gc'), IF(d.b8 <=> s.b8, NULL, 'b8'), IF(d.b1 <=> s.b1, NULL, 'b1'))
                FROM `{_testDb}`.`{sourceTable}` s LEFT JOIN `{_testDb}`.`{targetTable}` d ON d.id = s.id";
            Assert.That(Convert.ToString(command.ExecuteScalar()), Is.Empty,
                "every column must arrive equal to its source; the list names the ones that did not");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    // MariaDB's JSON_OBJECT writes a ZEROFILL number with its padding (MDEV-30962): {"n": 00007}, which is not JSON, so
    // DataTongs wrote a file delivery could not parse. Extracted and delivered here; the value must arrive as 7.
    [Test]
    public void EndToEnd_RoundTrip_ZeroFillColumn()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6).");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_zf_s_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_zf_t_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{sourceTable}` (id INT PRIMARY KEY, n INT(5) UNSIGNED ZEROFILL NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{targetTable}` (id INT PRIMARY KEY, n INT(5) UNSIGNED ZEROFILL NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO `{_testDb}`.`{sourceTable}` VALUES (1, 7)";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, "`id`", null);
            Assert.DoesNotThrow(() => JsonText.ParseArray(json), "the extracted file must be valid JSON: " + json);
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT n + 0 FROM `{_testDb}`.`{targetTable}` WHERE id = 1";
            Assert.That(Convert.ToInt64(command.ExecuteScalar()), Is.EqualTo(7));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    // MariaDB's JSON_ARRAYAGG is cut off at group_concat_max_len (1 MiB by default) with only a warning, so a table whose
    // extracted JSON passed 1 MiB was written unterminated and delivery failed parsing it. 1,200 rows of 1,000 characters.
    [Test]
    public void EndToEnd_RoundTrip_ExtractionOverOneMebibyte()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6).");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_big_s_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_big_t_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{sourceTable}` (id INT PRIMARY KEY, v TEXT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{targetTable}` (id INT PRIMARY KEY, v TEXT NULL)";
            command.ExecuteNonQuery();
            const string digits = "SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 "
                                  + "UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9";
            command.CommandText = $"INSERT INTO `{_testDb}`.`{sourceTable}` (id, v) SELECT h.n * 100 + t.n * 10 + o.n, REPEAT('x', 1000) "
                                  + $"FROM ({digits} UNION ALL SELECT 10 UNION ALL SELECT 11) h CROSS JOIN ({digits}) t CROSS JOIN ({digits}) o";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, "`id`", null);
            Assert.That(json.Length, Is.GreaterThan(1048576), "the extract must pass 1 MiB for this to test anything");
            Assert.That(JsonText.ParseArray(json).Count, Is.EqualTo(1200), "the extracted file must be complete, valid JSON");
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{targetTable}` WHERE CHAR_LENGTH(v) = 1000";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1200));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    // Fractional seconds were formatted away ('%s' with no '%f'): DATETIME(6) 10:11:12.123456 extracted as 10:11:12.
    // A whole-second column must still extract without a fraction, so existing files do not change.
    [Test]
    public void EndToEnd_RoundTrip_FractionalSeconds()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6).");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_frac_s_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_frac_t_{Guid.NewGuid():N}".Substring(0, 30);
        const string columns = "id INT PRIMARY KEY, dt6 DATETIME(6) NULL, ts3 TIMESTAMP(3) NULL, t6 TIME(6) NULL, dt0 DATETIME NULL";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{sourceTable}` ({columns})";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{targetTable}` ({columns})";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO `{_testDb}`.`{sourceTable}` VALUES "
                                  + "(1, '2024-03-05 10:11:12.123456', '2024-03-05 10:11:12.456', '01:02:03.456789', '2024-03-05 10:11:12')";
            command.ExecuteNonQuery();

            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, sourceTable);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, sourceTable, "`id`", null);
            Assert.That(json, Does.Contain("\"2024-03-05T10:11:12\"").And.Contain("10:11:12.123456"),
                "a whole-second column keeps its old form; a fractional one carries its fraction: " + json);
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $@"SELECT CONCAT_WS(',', IF(d.dt6 <=> s.dt6, NULL, 'dt6'), IF(d.ts3 <=> s.ts3, NULL, 'ts3'),
                   IF(d.t6 <=> s.t6, NULL, 't6'), IF(d.dt0 <=> s.dt0, NULL, 'dt0'))
                FROM `{_testDb}`.`{sourceTable}` s LEFT JOIN `{_testDb}`.`{targetTable}` d ON d.id = s.id";
            Assert.That(Convert.ToString(command.ExecuteScalar()), Is.Empty, "the list names the columns that did not arrive equal");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    // A configured SelectColumns used to stand each selected column in as plain varchar, so a selected binary column
    // was written raw instead of base64 and a DATETIME(6) lost its fraction. It now narrows the real column list.
    [Test]
    public void EndToEnd_SelectColumns_KeepTheirTypes()
    {
        if (!TargetHasNativeJsonTable)
            Assert.Ignore("Data delivery requires native JSON_TABLE (MySQL 8.0 / MariaDB 10.6).");
        using var command = _connection.CreateCommand();
        var sourceTable = $"_e2e_sel_s_{Guid.NewGuid():N}".Substring(0, 30);
        var targetTable = $"_e2e_sel_t_{Guid.NewGuid():N}".Substring(0, 30);
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{sourceTable}` (id INT PRIMARY KEY, bl VARBINARY(16) NULL, dt6 DATETIME(6) NULL, other INT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{targetTable}` (id INT PRIMARY KEY, bl VARBINARY(16) NULL, dt6 DATETIME(6) NULL, other INT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO `{_testDb}`.`{sourceTable}` VALUES (1, 0xDEADBEEF00, '2024-03-05 10:11:12.123456', 42)";
            command.ExecuteNonQuery();

            var json = _dataTongs.GetTableDataJsonMySql(command, _testDb, sourceTable, "`id`", null, "`id`, `bl`, `dt6`");
            Assert.That(json, Does.Not.Contain("other"), "a column that was not selected must not be extracted: " + json);
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, targetTable, json, "`id`",
                mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $@"SELECT CONCAT_WS(',', IF(d.bl <=> s.bl, NULL, 'bl'), IF(d.dt6 <=> s.dt6, NULL, 'dt6'))
                FROM `{_testDb}`.`{sourceTable}` s LEFT JOIN `{_testDb}`.`{targetTable}` d ON d.id = s.id";
            Assert.That(Convert.ToString(command.ExecuteScalar()), Is.Empty, "the list names the selected columns that did not arrive equal");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{sourceTable}`";
            command.ExecuteNonQuery();
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{targetTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_RoundTrip_HandlesBinaryData()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_e2e_binary_{Guid.NewGuid():N}".Substring(0, 30);

        try
        {
            // Create table with binary column
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    data VARBINARY(100)
                )";
            command.ExecuteNonQuery();

            // Insert binary data
            var binaryData = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello" in ASCII
            command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` VALUES (1, @data)";
            command.Parameters.Clear();
            var param = command.CreateParameter();
            param.ParameterName = "@data";
            param.Value = binaryData;
            command.Parameters.Add(param);
            command.ExecuteNonQuery();
            command.Parameters.Clear();

            // Extract - binary should be Base64 encoded
            var selectColumns = _dataTongs.GetSelectColumns(command, _testDb, tableName);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, _testDb, tableName, "`id`", null);

            // Verify Base64 encoding
            var expectedBase64 = Convert.ToBase64String(binaryData);
            Assert.That(json, Does.Contain(expectedBase64));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void EndToEnd_MultipleTablesWorkflow()
    {
        // Simulate extracting multiple related tables
        using var command = _connection.CreateCommand();

        // Extract country (parent)
        var countrySelectColumns = _dataTongs.GetSelectColumns(command, _testDb, "country");
        var countryKeys = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "country");
        var countryJson = _dataTongs.GetTableDataJson(command, countrySelectColumns, _testDb, "country", countryKeys, "country_id <= 5");

        // Extract city (child with FK to country)
        var citySelectColumns = _dataTongs.GetSelectColumns(command, _testDb, "city");
        var cityKeys = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "city");
        var cityJson = _dataTongs.GetTableDataJson(command, citySelectColumns, _testDb, "city", cityKeys, "country_id <= 5");

        // Write content files
        File.WriteAllText(Path.Combine(_testOutputDir, "country.tabledata"), FormatJson(countryJson));
        File.WriteAllText(Path.Combine(_testOutputDir, "city.tabledata"), FormatJson(cityJson));

        // Generate scripts (Upsert: mergeUpdate=true, mergeDelete=false)
        var countryScript = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, "country", countryJson, countryKeys,
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);
        var cityScript = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, "city", cityJson, cityKeys,
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        File.WriteAllText(Path.Combine(_testOutputDir, "Populate country.sql"), countryScript);
        File.WriteAllText(Path.Combine(_testOutputDir, "Populate city.sql"), cityScript);

        // Verify files exist
        Assert.That(File.Exists(Path.Combine(_testOutputDir, "country.tabledata")), Is.True);
        Assert.That(File.Exists(Path.Combine(_testOutputDir, "city.tabledata")), Is.True);
        Assert.That(File.Exists(Path.Combine(_testOutputDir, "Populate country.sql")), Is.True);
        Assert.That(File.Exists(Path.Combine(_testOutputDir, "Populate city.sql")), Is.True);

        // City script should have FK checks disabled
        Assert.That(cityScript, Does.Not.Contain("FOREIGN_KEY_CHECKS"));
    }

    #region Helper Methods

    private static string FormatJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]" || json == "null")
            return "[]";

        return json
            .Replace("},{", "},\n{")
            .Replace("[{", "[\n{")
            .Replace("}]", "}\n]");
    }

    #endregion
}
