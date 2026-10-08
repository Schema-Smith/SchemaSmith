// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Collections.Generic;
using System.Linq;

using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Shared MergeScriptHelper integration tests for the MySQL/MariaDb family. The MySQL and MariaDb
/// subclasses supply the platform + fixture accessors; every [Test] body here runs on both engines.
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
public abstract class MergeScriptHelperSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    // JSON_TABLE coerces the JSON numeric literal 0 into a YEAR column differently per engine:
    // MySQL yields 0 (the YEAR "zero" value), MariaDB yields 2000. Documented engine divergence
    // (only the value 0 diverges; real YEAR values 1901-2155 agree).
    protected virtual int ExpectedYearZeroValue => 0;

    private IDbConnection _connection = null!;
    private string _testDb = null!;
    private int _serverVersionNum;

    // JSON_TABLE exists on MySQL 8.0+ / MariaDB 10.6+; below that (MariaDB 10.2-10.5) the merge shreds
    // @json_data with a recursive CTE instead. 0 = unknown (defaults to JSON_TABLE).
    private bool UsesJsonTable => _serverVersionNum == 0 || (_serverVersionNum >= 1000 ? _serverVersionNum >= 1006 : _serverVersionNum >= 800);

    // JSON columns are distinguishable from longtext only on MySQL and MariaDB >= 10.4.3 (implicit json_valid
    // CHECK). On MariaDB 10.2-10.4.2 a JSON column is stored as plain longtext with no marker, so it is
    // delivered as text (data round-trips correctly) but the JSON-aware order-insensitive upsert comparison
    // is unavailable. Granularity can't see the .3 patch; treat >= 10.4 as detectable (accepted edge).
    private bool JsonColumnsDetectable => _serverVersionNum == 0 || _serverVersionNum < 1000 || _serverVersionNum >= 1004;

    [SetUp]
    public void SetUp()
    {
        _testDb = MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        using var versionCmd = _connection.CreateCommand();
        versionCmd.CommandText = "SELECT SchemaSmith_ServerVersionNum()";
        _serverVersionNum = Convert.ToInt32(versionCmd.ExecuteScalar());
    }

    // Routes every test's merge build through the detected server version (major*100+minor). Below MySQL 8.0
    // automatic data delivery is gated (no JSON_TABLE, no recursive CTE), so those tests self-skip on a genuine
    // 5.7 target; MariaDB (>= 1000) always proceeds -- 10.2-10.5 exercise the recursive-CTE fallback.
    protected string BuildMergeScript(IDbCommand command, string db, string table, string tableData,
        string keyColumns, bool mergeUpdate, bool mergeDelete, bool disableTriggers, bool tokenizeScripts, string mergeFilter)
    {
        if (_serverVersionNum is > 0 and < 800)
            Assert.Ignore($"Automatic data delivery is gated below MySQL 8.0 (detected {_serverVersionNum}); manual data scripts are the supported path.");
        return MergeScriptHelper.BuildMergeScript(Platform, command, db, table, tableData, keyColumns,
            mergeUpdate, mergeDelete, disableTriggers, tokenizeScripts, mergeFilter, mySqlServerVersionNum: _serverVersionNum);
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void BuildMergeScript_TableNameWithSingleQuote_GeneratesValidScript()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz't_{Guid.NewGuid():N}";

        try
        {
            command.CommandText = $@"
CREATE TABLE `{_testDb}`.`{tableName.Replace("`", "``")}` (
    id INT PRIMARY KEY,
    name VARCHAR(50) NOT NULL
)";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Alpha""}]";
            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, true, false, false, null!);

            Assert.That(script, Is.Not.Null);
            Assert.That(script, Does.Contain($"`{_testDb}`.`{tableName}`"));

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT name FROM `{_testDb}`.`{tableName.Replace("`", "``")}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Alpha"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName.Replace("`", "``")}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_Latin1StringKey_FullSyncDelete_ExecutesWithoutCollationError()
    {
        // Regression (#373): the DELETE key-match forced COLLATE utf8mb4_unicode_ci onto the key column
        // regardless of its charset, so a latin1 key column (latin1 is a common stock default on older
        // MySQL/MariaDB) raised "COLLATION 'utf8mb4_unicode_ci' is not valid for CHARACTER SET 'latin1'"
        // (error 1253), aborting data delivery. Same defect class as CHANGELOG #359.
        using var command = _connection.CreateCommand();
        var tableName = $"zz_latin1key_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $@"
CREATE TABLE `{_testDb}`.`{tableName}` (
    code VARCHAR(20) CHARACTER SET latin1 NOT NULL PRIMARY KEY,
    name VARCHAR(50)
)";
            command.ExecuteNonQuery();
            // Seed a stale row the full-sync delete must remove (absent from the delivered data).
            command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` (code, name) VALUES ('STALE', 'old')";
            command.ExecuteNonQuery();

            var tableData = @"[{""code"":""KEEP"",""name"":""Alpha""}]";
            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`code`", true, true, false, false, null!);

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT name FROM `{_testDb}`.`{tableName}` WHERE code = 'KEEP'";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("Alpha"), "The delivered latin1-keyed row must upsert.");
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}` WHERE code = 'STALE'";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "The full-sync delete must remove the stale latin1-keyed row.");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // Below MariaDB 10.6 a payload over MariaDbShredChunkRows is delivered in chunks, and the chunks were rebuilt from
    // parsed rows: a DECIMAL beyond double precision lost its tail digits. Each row now goes out as delivered.
    [Test]
    public void BuildMergeScript_ChunkedShred_DeliversEachValueExactly()
    {
        if (UsesJsonTable)
            Assert.Ignore("Only the recursive-CTE shred (MariaDB 10.2-10.5) delivers in chunks.");
        using var command = _connection.CreateCommand();
        var tableName = $"zz_chunk_{Guid.NewGuid():N}";
        const string amount = "12345678901234567890.123456789012345678";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT NOT NULL PRIMARY KEY, amt DECIMAL(38,18) NOT NULL, note VARCHAR(40) NOT NULL)";
            command.ExecuteNonQuery();

            var rowCount = MergeScriptHelper.MariaDbShredChunkRows + 10;
            var tableData = "[" + string.Join(",", Enumerable.Range(1, rowCount)
                .Select(i => "{\"id\":" + i + ",\"amt\":" + amount + ",\"note\":\"a \\\"q\\\" b\"}")) + "]";
            var script = BuildMergeScript(command, _testDb, tableName, tableData, "`id`", true, false, false, false, null!);
            Assert.That(script, Does.Contain("chunks of up to"), "the test must exercise the chunked path");

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}` WHERE amt = {amount} AND note = 'a \"q\" b'";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(rowCount), "rows whose amount and note arrived exactly");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildDeferredMergeScript_TextColumn_UsesTextInJsonTableAndCanBeExecuted()
    {
        if (_serverVersionNum is > 0 and < 800)
            Assert.Ignore($"Automatic data delivery is gated below MySQL 8.0 (detected {_serverVersionNum}); manual data scripts are the supported path.");
        using var command = _connection.CreateCommand();
        var tableName = $"zz_deferred_text_{Guid.NewGuid():N}";

        try
        {
            // A nullable self-referencing FK makes ParentId a *deferred* column, which routes delivery
            // through the deferred merge builder. `Notes` is a TEXT column whose JSON_TABLE column type
            // used to be emitted as CHAR(65535) — accepted by MySQL but rejected by MariaDB with
            // "Column length too big for column 'Notes' (max = 255); use BLOB or TEXT instead".
            command.CommandText = $@"
CREATE TABLE `{_testDb}`.`{tableName}` (
    id INT PRIMARY KEY,
    ParentId INT NULL,
    Notes TEXT NULL,
    CONSTRAINT `fk_{tableName}_self` FOREIGN KEY (ParentId) REFERENCES `{_testDb}`.`{tableName}`(id)
)";
            command.ExecuteNonQuery();

            var helper = new MergeScriptHelperAdapter(Platform);
            var tableData = @"[{""id"":1,""ParentId"":null,""Notes"":""hello""}]";
            var script = DeferredMergeBuilder.Build(helper, command, "MySQL", _testDb, tableName,
                tableData, "`id`", false, new List<string> { "ParentId" });

            // TEXT must be read as TEXT in JSON_TABLE, not an oversized CHAR that MariaDB rejects.
            // (MariaDB 10.2-10.5 uses the recursive-CTE shred instead of JSON_TABLE.)
            if (UsesJsonTable) Assert.That(script, Does.Contain("`Notes` LONGTEXT PATH '$.Notes'"));

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT Notes FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("hello"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void GetKeyColumns_ReturnsCorrectPrimaryKey_SingleColumn()
    {
        // Arrange - actor table has single-column primary key (actor_id)
        using var command = _connection.CreateCommand();

        // Act
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "actor");

        // Assert
        Assert.That(keyColumns, Is.EqualTo("`actor_id`"));
    }

    [Test]
    public void GetKeyColumns_ReturnsCorrectPrimaryKey_CompositeKey()
    {
        // Arrange - film_actor table has composite primary key (actor_id, film_id)
        using var command = _connection.CreateCommand();

        // Act
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "film_actor");

        // Assert
        Assert.That(keyColumns, Does.Contain("`actor_id`"));
        Assert.That(keyColumns, Does.Contain("`film_id`"));
    }

    [Test]
    public void GetKeyColumns_ReturnsEmpty_ForNonExistentTable()
    {
        using var command = _connection.CreateCommand();

        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, "non_existent_table");

        Assert.That(keyColumns, Is.EqualTo(""));
    }

    [Test]
    public void BuildMergeScript_ActorTable_GeneratesUpsertPlusDeleteSQL()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableData = @"[{""actor_id"":1,""first_name"":""TEST"",""last_name"":""ACTOR"",""last_update"":""2024-01-01 00:00:00""}]";

        // Act
        var script = BuildMergeScript(command, _testDb, "actor",
            tableData, "`actor_id`", true, true, false, false, null!);

        // Assert — must not use REPLACE INTO (breaks ON DELETE RESTRICT FKs)
        Assert.That(script, Does.Not.Contain("REPLACE INTO"));
        Assert.That(script, Does.Contain($"INSERT INTO `{_testDb}`.`actor`"));
        Assert.That(script, Does.Contain("ON DUPLICATE KEY UPDATE"));
        Assert.That(script, Does.Contain($"DELETE Target FROM `{_testDb}`.`actor` Target"));
        Assert.That(script, Does.Contain("NOT EXISTS"));
        if (UsesJsonTable)
        {
            Assert.That(script, Does.Contain("JSON_TABLE("));
            Assert.That(script, Does.Contain("`first_name` VARCHAR(45) PATH '$.first_name'"));
            Assert.That(script, Does.Contain("`last_name` VARCHAR(45) PATH '$.last_name'"));
        }
        else
        {
            // MariaDB 10.2-10.5: the JSON array is shredded with a recursive CTE instead of JSON_TABLE.
            Assert.That(script, Does.Contain("WITH RECURSIVE _ss_seq"));
            Assert.That(script, Does.Contain("`first_name`"));
        }
        // AUTO_INCREMENT columns are included so explicit ID values from data files are preserved.
        Assert.That(script, Does.Contain("`actor_id`"));
    }

    [Test]
    public void BuildMergeScript_IncludesAutoIncrementColumn()
    {
        // Arrange - country table has auto_increment country_id
        using var command = _connection.CreateCommand();
        var tableData = @"[{""country_id"":1,""country"":""Testland"",""last_update"":""2024-01-01 00:00:00""}]";

        // Act
        var script = BuildMergeScript(command, _testDb, "country",
            tableData, "`country_id`", false, false, false, false, null!);

        // Assert
        Assert.That(script, Does.Contain($"INSERT IGNORE INTO `{_testDb}`.`country`"));
        if (UsesJsonTable) Assert.That(script, Does.Contain("`country` VARCHAR(50) PATH '$.country'"));
        // AUTO_INCREMENT columns are included to preserve original IDs
        Assert.That(script, Does.Contain("`country_id`"));
    }

    [Test]
    public void BuildMergeScript_UpsertType_GeneratesValidSQL()
    {
        // Arrange
        using var command = _connection.CreateCommand();
        var tableData = @"[{""country"":""Updated Country"",""last_update"":""2024-01-01 00:00:00""}]";

        // Act
        var script = BuildMergeScript(command, _testDb, "country",
            tableData, "", true, false, false, false, null!);

        // Assert
        Assert.That(script, Does.Contain($"INSERT INTO `{_testDb}`.`country`"));
        Assert.That(script, Does.Contain("ON DUPLICATE KEY UPDATE"));
        Assert.That(script, Does.Contain("`country` = VALUES(`country`)"));
    }

    [Test]
    public void BuildMergeScript_GeneratedScript_CanBeExecuted()
    {
        // Arrange - Create a test table with data (not temporary, as temp tables don't show in INFORMATION_SCHEMA)
        using var command = _connection.CreateCommand();
        var tableName = $"_test_merge_data_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            // Create test table
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    name VARCHAR(100) NOT NULL,
                    value DECIMAL(10,2),
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            // Insert initial data
            command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` (name, value) VALUES ('initial', 10.50)";
            command.ExecuteNonQuery();

            var tableData = @"[{""name"":""test1"",""value"":20.00},{""name"":""test2"",""value"":30.00}]";

            // Generate and execute merge script
            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", false, false, false, false, null!);

            // Execute the generated script (batch execution)
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify data was inserted
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            var count = Convert.ToInt32(command.ExecuteScalar());

            // Assert - should have 3 rows (1 initial + 2 inserted)
            Assert.That(count, Is.EqualTo(3));
        }
        finally
        {
            // Cleanup
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_GeometryColumn_UsesTextInJsonTableAndStGeomFromTextInSelect()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_geom_meta_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    location POINT NOT NULL
                )";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Test"",""location"":""POINT(0 0)""}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, false, false, false, null!);

            // JSON_TABLE should read geometry as TEXT (WKT string)
            if (UsesJsonTable) Assert.That(script, Does.Contain("`location` LONGTEXT PATH '$.location'"));

            // JSON_TABLE must also read the "<col>.STSrid" companion the extractors emit beside the WKT
            if (UsesJsonTable)
                Assert.That(script, Does.Contain("`location.STSrid` INT PATH '$.\"location.STSrid\"'"),
                    "the SRID companion must be shredded so the geometry keeps its reference system");

            // SELECT should convert WKT back to geometry, in its own SRID rather than 0. COALESCE keeps
            // packages extracted before the companion existed behaving exactly as they did.
            Assert.That(script, Does.Contain("ST_GeomFromText(`location`, COALESCE(`location.STSrid`, 0))"));

            // INSERT INTO should have the plain column name
            Assert.That(script, Does.Contain($"INSERT INTO `{_testDb}`.`{tableName}` (`id`, `name`, `location`)"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_GeometryColumn_CanBeExecuted()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_geom_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    location POINT NOT NULL
                )";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Place A"",""location"":""POINT(10.5 20.3)""},{""id"":2,""name"":""Place B"",""location"":""POINT(-73.9857 40.7484)""}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, true, false, false, null!);

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify data was inserted
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            var count = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(count, Is.EqualTo(2));

            // Verify geometry can be read back as WKT
            command.CommandText = $"SELECT ST_AsText(location) FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            var wkt = command.ExecuteScalar()?.ToString();
            Assert.That(wkt, Does.Contain("POINT"));
            Assert.That(wkt, Does.Contain("10.5"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_BlobColumn_UsesTextInJsonTableAndFromBase64InSelect()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_blob_meta_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    picture BLOB
                )";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Test"",""picture"":""SGVsbG8gV29ybGQ=""}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, false, false, false, null!);

            // JSON_TABLE should read blob as TEXT (Base64 string)
            if (UsesJsonTable) Assert.That(script, Does.Contain("`picture` LONGTEXT PATH '$.picture'"));

            // SELECT should convert Base64 back to binary
            Assert.That(script, Does.Contain("FROM_BASE64(`picture`)"));

            // INSERT INTO should have the plain column name
            Assert.That(script, Does.Contain($"INSERT INTO `{_testDb}`.`{tableName}` (`id`, `name`, `picture`)"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_BlobColumn_CanBeExecuted()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_blob_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    data BLOB
                )";
            command.ExecuteNonQuery();

            // "Hello World" = SGVsbG8gV29ybGQ=, "Test Data" = VGVzdCBEYXRh
            var tableData = @"[{""id"":1,""name"":""Item A"",""data"":""SGVsbG8gV29ybGQ=""},{""id"":2,""name"":""Item B"",""data"":""VGVzdCBEYXRh""}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, true, false, false, null!);

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify data was inserted
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            var count = Convert.ToInt32(command.ExecuteScalar());
            Assert.That(count, Is.EqualTo(2));

            // Verify binary data can be read back
            command.CommandText = $"SELECT CAST(data AS CHAR) FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            var value = command.ExecuteScalar()?.ToString();
            Assert.That(value, Is.EqualTo("Hello World"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_DecimalType_GeneratesCorrectPrecision()
    {
        using var command = _connection.CreateCommand();
        var tableData = @"[{""amount"":100.50,""payment_date"":""2024-01-01 10:00:00""}]";

        // payment table has amount DECIMAL(5,2)
        var script = BuildMergeScript(command, _testDb, "payment",
            tableData, "`payment_id`", false, false, false, false, null!);

        if (UsesJsonTable) Assert.That(script, Does.Contain("DECIMAL(5,2)"));
    }

    [Test]
    public void BuildMergeScript_DatetimeType_GeneratesCorrectFormat()
    {
        using var command = _connection.CreateCommand();
        var tableData = @"[{""first_name"":""TEST"",""last_name"":""ACTOR"",""last_update"":""2024-01-01 12:30:45""}]";

        var script = BuildMergeScript(command, _testDb, "actor",
            tableData, "", false, false, false, false, null!);

        if (UsesJsonTable) Assert.That(script, Does.Contain("TIMESTAMP")); // last_update is TIMESTAMP (JSON_TABLE column type)
    }

    [Test]
    public void BuildMergeScript_JsonColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_json_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    name VARCHAR(100),
                    metadata JSON
                )";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""name"":""Test"",""metadata"":{""z_key"":""last"",""a_key"":""first"",""nested"":{""x"":10}}}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, false, false, false, null!);

            // JSON column upsert should use conditional comparison via the MySQL/MariaDb-portable
            // JSON_EXTRACT(x,'$') form (MariaDB rejects CAST(x AS JSON)). MariaDB 10.2-10.4.2 cannot
            // distinguish a JSON column from longtext (no implicit json_valid), so it is delivered as text
            // there -- the data still round-trips (asserted below); only this order-insensitive compare differs.
            if (JsonColumnsDetectable)
                Assert.That(script, Does.Contain("JSON_EXTRACT(VALUES(`metadata`), '$')"));

            // Execute the generated script
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify data was inserted
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1));

            // Verify JSON data preserved
            command.CommandText = $"SELECT JSON_EXTRACT(metadata, '$.a_key') FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Does.Contain("first"));

            command.CommandText = $"SELECT JSON_EXTRACT(metadata, '$.nested.x') FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("10"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // MariaDB names its JSON column check after the column, and CHECK names are per table there. A text column in
    // another table whose own check carries that same name was read as JSON, so its plain-text values went through
    // the JSON path.
    [Test]
    public void BuildMergeScript_TextColumnSharingACheckNameWithAnotherTablesJsonColumn_StaysText()
    {
        using var command = _connection.CreateCommand();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var textTable = $"_test_ckt_{suffix}";
        var jsonTable = $"_test_ckj_{suffix}";

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{jsonTable}` (id INT PRIMARY KEY, payload JSON);
                CREATE TABLE `{_testDb}`.`{textTable}` (id INT PRIMARY KEY, payload TEXT,
                    CONSTRAINT payload CHECK (CHAR_LENGTH(payload) > 0))";
            command.ExecuteNonQuery();

            var script = BuildMergeScript(command, _testDb, textTable,
                @"[{""id"":1,""payload"":""plain text, not JSON""}]", "`id`", true, false, false, false, null!);

            Assert.That(script, Does.Not.Contain("JSON_EXTRACT(VALUES(`payload`)"));
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }
            command.CommandText = $"SELECT payload FROM `{_testDb}`.`{textTable}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("plain text, not JSON"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{textTable}`, `{_testDb}`.`{jsonTable}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_YearColumn_RoundTrip()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"_test_year_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    release_year YEAR
                )";
            command.ExecuteNonQuery();

            var tableData = @"[{""id"":1,""release_year"":2024},{""id"":2,""release_year"":1901},{""id"":3,""release_year"":0}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, true, false, false, null!);

            // Execute the generated script
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Verify all rows inserted
            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(3));

            // Verify year values preserved
            command.CommandText = $"SELECT release_year FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2024));

            command.CommandText = $"SELECT release_year FROM `{_testDb}`.`{tableName}` WHERE id = 2";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1901));

            command.CommandText = $"SELECT release_year FROM `{_testDb}`.`{tableName}` WHERE id = 3";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(ExpectedYearZeroValue));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_FullSyncDelete_WithPortableMergeFilter_ScopesDeletionToFilterMatch()
    {
        // #333: a MergeFilter authored portably as `Target.<col>` must resolve on MySQL
        // (previously failed with "Unknown column 'Target.region'" — delete aliased the table `t`).
        // MergeFilter scopes which rows are even eligible for deletion (docs: schema-packages.md
        // MergeFilter) — rows outside the filter's scope are never removed, regardless of whether
        // they're present in the incoming source data.
        using var command = _connection.CreateCommand();
        var tableName = $"_test_merge_filter_{Guid.NewGuid():N}".Substring(0, 40);

        try
        {
            command.CommandText = $@"
                CREATE TABLE `{_testDb}`.`{tableName}` (
                    id INT PRIMARY KEY,
                    region VARCHAR(20) NOT NULL
                )";
            command.ExecuteNonQuery();

            command.CommandText = $@"
                INSERT INTO `{_testDb}`.`{tableName}` (id, region) VALUES
                    (1, 'KEEP'),
                    (2, 'KEEP'),
                    (3, 'OTHER')";
            command.ExecuteNonQuery();

            // Source data only carries row 1 (region 'KEEP'). Row 2 is in-scope (region 'KEEP')
            // but absent from source, so it must be deleted. Row 3 is out-of-scope (region
            // 'OTHER') so it must survive even though it's also absent from source.
            var tableData = @"[{""id"":1,""region"":""KEEP""}]";

            var script = BuildMergeScript(command, _testDb, tableName,
                tableData, "`id`", true, true, false, false, "Target.region = 'KEEP'");

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(batch => !string.IsNullOrWhiteSpace(batch)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}`";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2));

            command.CommandText = $"SELECT region FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("KEEP"));

            command.CommandText = $"SELECT COUNT(*) FROM `{_testDb}`.`{tableName}` WHERE id = 2";
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0));

            command.CommandText = $"SELECT region FROM `{_testDb}`.`{tableName}` WHERE id = 3";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("OTHER"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }
}
