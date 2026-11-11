// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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
    private IDbConnection _connection = null!;
    private string _testDb = null!;
    private int _serverVersionNum;

    // The row source the product picks: JSON_TABLE on MySQL 8.0+ and MariaDB 10.6+, except the MariaDB patches whose
    // JSON_TABLE misreads strings, which shred with the recursive CTE used below 10.6.
    private int _rowSourceVersionNum;
    private bool UsesJsonTable => MergeScriptHelper.MySqlFamilyHasJsonTable(_rowSourceVersionNum);

    // MySQL patches whose JSON_TABLE misreads scalars read every column as JSON instead of its own type.
    private bool _readsScalarsAsJson;

    // The JSON_TABLE column a test expects, as the row source spells it on this server.
    private string JsonTableColumn(string typed) =>
        _readsScalarsAsJson ? Regex.Replace(typed, @"^(`[^`]+`) .*? PATH ", "$1 JSON PATH ") : typed;

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
        _rowSourceVersionNum = MergeScriptHelper.RowSourceVersionNumMySql(versionCmd, _serverVersionNum);
        _readsScalarsAsJson = MergeScriptHelper.JsonTableMisreadsScalarsMySql(versionCmd, _serverVersionNum);
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

    // A table and columns whose names hold every character a name can carry that SQL treats specially: a quote, a percent
    // sign, the backtick, a dot, a backslash, a comma, a space and non-ASCII letters. The key list the caller names, or
    // the one the catalog gives.
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void AMergeInsertsUpdatesAndDeletes_UnderHostileNames(bool keysFromTheCatalog, bool deferred)
    {
        const string h = "a'b%c`d.e\\f,g h \u00e9\u03a9";
        const string table = "mh" + h, key = "k" + h, value = "v" + h, other = "o" + h;
        static string B(string name) => "`" + name.Replace("`", "``") + "`";
        static string J(string name) => name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var target = $"{B(_testDb)}.{B(table)}";
        using var command = _connection.CreateCommand();
        try
        {
            command.CommandText = $"DROP TABLE IF EXISTS {target}; CREATE TABLE {target} ({B(key)} INT NOT NULL PRIMARY KEY, {B(value)} VARCHAR(40) NULL, {B(other)} INT NULL) DEFAULT CHARSET=utf8mb4; INSERT INTO {target} VALUES (1, 'one', 1), (2, 'two', 2);";
            command.ExecuteNonQuery();
            var data = $"[{{\"{J(key)}\":1,\"{J(value)}\":\"uno\",\"{J(other)}\":10}},{{\"{J(key)}\":3,\"{J(value)}\":\"tres\",\"{J(other)}\":30}}]";
            var keys = keysFromTheCatalog ? MergeScriptHelper.GetKeyColumns(Platform, command, _testDb, table) : B(key);
            var script = BuildMergeScript(command, _testDb, table, data, keys,
                mergeUpdate: true, mergeDelete: true, disableTriggers: false, tokenizeScripts: false, mergeFilter: null);
            if (deferred)
            {
                command.CommandText = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform), command, "MySQL", _testDb, table,
                    data, keys, false, new List<string> { other });
                command.ExecuteNonQuery();
            }

            command.CommandText = script;
            command.ExecuteNonQuery();

            command.CommandText = $"SELECT GROUP_CONCAT(CONCAT({B(key)}, ':', {B(value)}, ':', {B(other)}) ORDER BY {B(key)} SEPARATOR '|') FROM {target}";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:uno:10|3:tres:30"),
                "row 1 updated, row 2 deleted, row 3 inserted");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS {target}";
            command.ExecuteNonQuery();
        }
    }

    // B27: a script written for another database renamed its table by replacing the qualified name everywhere in the
    // script, so a value in the data that spelled the qualified name was rewritten too.
    [Test]
    public void BuildMergeScript_RetargetedScript_LeavesDataThatSpellsTheTableNameAlone()
    {
        if (_serverVersionNum is > 0 and < 800)
            Assert.Ignore($"Automatic data delivery is gated below MySQL 8.0 (detected {_serverVersionNum}).");
        using var command = _connection.CreateCommand();
        var tableName = $"rq_{Guid.NewGuid():N}"[..20];
        var spelled = $"`{_testDb}`.`{tableName}`";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT PRIMARY KEY, note VARCHAR(200) NULL)";
            command.ExecuteNonQuery();

            var tableData = $"[{{\"id\":1,\"note\":\"{spelled}\"}}]";
            var script = MergeScriptHelper.BuildMergeScript(Platform, command, _testDb, tableName, tableData, "`id`",
                true, true, false, false, null!, destSchemaOverride: "", mySqlServerVersionNum: _serverVersionNum);

            command.CommandText = $"USE `{_testDb}`";
            command.ExecuteNonQuery();
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT note FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo(spelled));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
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

    // The chunked delete stages the delivered keys in a temporary table whose string keys were VARCHAR(255): a longer key
    // was cut short (strict mode: "Data too long"; otherwise two keys sharing their first 255 characters matched each
    // other, and the row the payload no longer held survived while the wrong comparison decided the rest).
    [Test]
    public void BuildMergeScript_ChunkedDelete_ComparesLongStringKeysWhole()
    {
        if (UsesJsonTable)
            Assert.Ignore("Only the recursive-CTE shred (MariaDB 10.2-10.5) delivers in chunks.");
        using var command = _connection.CreateCommand();
        var tableName = $"zz_longkey_{Guid.NewGuid():N}";
        var prefix = new string('k', 300);
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (code VARCHAR(500) NOT NULL PRIMARY KEY, n INT NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = $"INSERT INTO `{_testDb}`.`{tableName}` (code, n) VALUES ('{prefix}-kept', 1), ('{prefix}-gone', 2)";
            command.ExecuteNonQuery();

            var rows = Enumerable.Range(1, MergeScriptHelper.MariaDbShredChunkRows + 10)
                .Select(i => "{\"code\":\"filler-" + i + "\",\"n\":" + i + "}")
                .Prepend("{\"code\":\"" + prefix + "-kept\",\"n\":1}");
            var script = BuildMergeScript(command, _testDb, tableName, "[" + string.Join(",", rows) + "]", "`code`", true, true, false, false, null!);
            Assert.That(script, Does.Contain("chunks of up to"), "the test must exercise the chunked path");

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT GROUP_CONCAT(RIGHT(code, 5) ORDER BY code) FROM `{_testDb}`.`{tableName}` WHERE code LIKE 'kkk%'";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("-kept"), "the key the payload holds stays, the one it does not is deleted");
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

    // The deferred (FK-cycle) builder kept its own column handling, which the single-pass builder had outgrown:
    // DATETIME/TIME were read without their fraction, BIT arrived as the character '5', a geometry lost its SRID
    // (fatal on a MySQL SRID column), and a backslash in the payload was read as an escape.
    [Test]
    public void BuildDeferredMergeScript_DeliversEveryValueExactly()
    {
        if (_serverVersionNum is > 0 and < 800)
            Assert.Ignore($"Automatic data delivery is gated below MySQL 8.0 (detected {_serverVersionNum}).");
        using var command = _connection.CreateCommand();
        var tableName = $"zz_deferred_exact_{Guid.NewGuid():N}";
        var srid = Platform == Platform.MySQL ? 4326 : 0;
        try
        {
            command.CommandText = $@"
CREATE TABLE `{_testDb}`.`{tableName}` (
    id INT PRIMARY KEY,
    ParentId INT NULL,
    dt6 DATETIME(6) NULL,
    t3 TIME(3) NULL,
    b BIT(8) NULL,
    pt POINT {(srid == 0 ? "" : $"SRID {srid} ")}NULL,
    note VARCHAR(40) NULL,
    CONSTRAINT `fk_{tableName}_self` FOREIGN KEY (ParentId) REFERENCES `{_testDb}`.`{tableName}`(id)
)";
            command.ExecuteNonQuery();

            var tableData = "[{\"id\":1,\"ParentId\":1,\"dt6\":\"2024-03-05T10:11:12.123456\",\"t3\":\"01:02:03.456\",\"b\":165,"
                            + "\"pt\":\"POINT(1 2)\",\"pt.STSrid\":" + srid + ",\"note\":\"a\\\\b \\\"q\\\"\"}]";
            var script = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform), command, "MySQL", _testDb, tableName,
                tableData, "`id`", false, new List<string> { "ParentId" });

            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $@"SELECT CONCAT_WS(',',
                IF(dt6 <=> '2024-03-05 10:11:12.123456', NULL, 'dt6'), IF(t3 <=> '01:02:03.456', NULL, 't3'),
                IF(b + 0 <=> 165, NULL, 'b'), IF(ST_AsText(pt) = 'POINT(1 2)' AND ST_SRID(pt) = {srid}, NULL, 'pt'),
                IF(note <=> CONCAT('a', CHAR(92), 'b ""q""'), NULL, 'note'), IF(ParentId IS NULL, NULL, 'ParentId'))
                FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.Empty,
                "the list names each column that did not arrive as delivered (ParentId is deferred, so pass 1 leaves it NULL)");
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // MySQL's JSON_TABLE, on the patches below the fixes: a JSON null into a number column fails the insert under strict mode
    // (before 8.0.20), into a text column arrives as the text 'null' (before 8.0.21), and an integer of 2^31 or more wraps
    // with no warning (before 8.0.13). Every engine must store each value as delivered.
    [Test]
    public void BuildMergeScript_NullsAndLargeIntegers_ArriveUnchanged()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz_bigint_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT PRIMARY KEY, s BIGINT NULL, u BIGINT UNSIGNED NULL, n INT NULL, nu INT UNSIGNED NULL, d DECIMAL(10,2) NULL, t VARCHAR(10) NULL)";
            command.ExecuteNonQuery();

            var script = BuildMergeScript(command, _testDb, tableName,
                "[{\"id\":1,\"s\":2147483648,\"u\":18446744073709551615,\"n\":2147483647,\"nu\":4294967295}," +
                "{\"id\":2,\"s\":9223372036854775807,\"u\":9223372036854775808,\"n\":-2147483648,\"nu\":2147483648}," +
                "{\"id\":3,\"s\":-2147483649,\"u\":4294967296,\"n\":0,\"nu\":0}," +
                "{\"id\":4,\"s\":-9223372036854775808,\"u\":0,\"n\":null,\"nu\":null,\"d\":null,\"t\":null}," +
                "{\"id\":5,\"s\":null,\"u\":null,\"n\":7,\"nu\":7,\"d\":1.50,\"t\":\"x\"}]",
                "`id`", true, false, false, false, null!);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT GROUP_CONCAT(id, ':', COALESCE(s, '<NULL>'), ':', COALESCE(u, '<NULL>'), ':', COALESCE(n, '<NULL>'), ':', COALESCE(nu, '<NULL>'), ':', COALESCE(d, '<NULL>'), ':', COALESCE(t, '<NULL>') ORDER BY id SEPARATOR ' ') FROM `{_testDb}`.`{tableName}`";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo(
                "1:2147483648:18446744073709551615:2147483647:4294967295:<NULL>:<NULL> " +
                "2:9223372036854775807:9223372036854775808:-2147483648:2147483648:<NULL>:<NULL> " +
                "3:-2147483649:4294967296:0:0:<NULL>:<NULL> " +
                "4:-9223372036854775808:0:<NULL>:<NULL>:<NULL>:<NULL> " +
                "5:<NULL>:<NULL>:7:7:1.50:x"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void BuildMergeScript_JsonBooleans_ArriveAsNumbersInNumberColumns()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz_bool_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT PRIMARY KEY, ti TINYINT NULL, i INT NULL, d DECIMAL(5,2) NULL, t VARCHAR(10) NULL)";
            command.ExecuteNonQuery();

            var script = BuildMergeScript(command, _testDb, tableName,
                "[{\"id\":1,\"ti\":true,\"i\":false,\"d\":true,\"t\":true},{\"id\":2,\"ti\":false,\"i\":true,\"d\":false,\"t\":false}]",
                "`id`", true, false, false, false, null!);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT GROUP_CONCAT(id, ':', ti, ':', i, ':', d, ':', t ORDER BY id SEPARATOR ' ') FROM `{_testDb}`.`{tableName}`";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:1:0:1.00:true 2:0:1:0.00:false"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // Read as text, YEAR 0 is the string '0', which a YEAR column stores as 2000.
    [Test]
    public void BuildMergeScript_YearZero_ArrivesAsZero()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz_year_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT PRIMARY KEY, y YEAR NULL)";
            command.ExecuteNonQuery();

            var script = BuildMergeScript(command, _testDb, tableName,
                "[{\"id\":1,\"y\":0},{\"id\":2,\"y\":1999},{\"id\":3,\"y\":null}]",
                "`id`", true, false, false, false, null!);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT GROUP_CONCAT(id, ':', COALESCE(y, '<NULL>') ORDER BY id SEPARATOR ' ') FROM `{_testDb}`.`{tableName}`";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:0000 2:1999 3:<NULL>"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // Without JSON_TABLE (MariaDB 10.2-10.5) the shred hands every value over as text, and text into a BIT column is
    // stored as its character codes: 165 arrived as 0x31.
    [Test]
    public void BuildMergeScript_BitColumn_DeliversItsNumber()
    {
        using var command = _connection.CreateCommand();
        var tableName = $"zz_bit_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $"CREATE TABLE `{_testDb}`.`{tableName}` (id INT PRIMARY KEY, b BIT(8) NULL, b1 BIT(1) NULL)";
            command.ExecuteNonQuery();

            var script = BuildMergeScript(command, _testDb, tableName, "[{\"id\":1,\"b\":165,\"b1\":1},{\"id\":2,\"b\":0,\"b1\":0}]",
                "`id`", true, false, false, false, null!);
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT GROUP_CONCAT(id, ':', b + 0, ':', b1 + 0 ORDER BY id) FROM `{_testDb}`.`{tableName}`";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("1:165:1,2:0:0"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS `{_testDb}`.`{tableName}`";
            command.ExecuteNonQuery();
        }
    }

    // #567 on the deferred path, which a self-referencing table takes: an object or array value for a text column arrives as
    // its compact JSON text, numbers as written, from an indented payload -- not NULL.
    [Test]
    public void BuildDeferredMergeScript_AnObjectForATextColumn_ArrivesAsItsCompactText()
    {
        if (_serverVersionNum is > 0 and < 800)
            Assert.Ignore($"Automatic data delivery is gated below MySQL 8.0 (detected {_serverVersionNum}); manual data scripts are the supported path.");
        using var command = _connection.CreateCommand();
        var tableName = $"zz_deferred_obj_{Guid.NewGuid():N}";
        try
        {
            command.CommandText = $@"
CREATE TABLE `{_testDb}`.`{tableName}` (
    id INT PRIMARY KEY,
    ParentId INT NULL,
    Notes TEXT NULL,
    Tags VARCHAR(100) NULL,
    CONSTRAINT `fk_{tableName}_self` FOREIGN KEY (ParentId) REFERENCES `{_testDb}`.`{tableName}`(id)
)";
            command.ExecuteNonQuery();

            var tableData = "[\n  {\n    \"id\": 1,\n    \"ParentId\": null,\n    \"Notes\": { \"price\": 2.50, \"tags\": [ \"a\" ] },\n    \"Tags\": [ 1, 2 ]\n  }\n]";
            var script = DeferredMergeBuilder.Build(new MergeScriptHelperAdapter(Platform), command, "MySQL", _testDb, tableName,
                tableData, "`id`", false, new List<string> { "ParentId" });
            foreach (var batch in script.Split(new[] { ";\r\n", ";\n" }, StringSplitOptions.RemoveEmptyEntries)
                         .Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            command.CommandText = $"SELECT CONCAT(COALESCE(Notes, '<null>'), ' | ', COALESCE(Tags, '<null>')) FROM `{_testDb}`.`{tableName}` WHERE id = 1";
            Assert.That(command.ExecuteScalar()?.ToString(), Is.EqualTo("{\"price\":2.50,\"tags\":[\"a\"]} | [1,2]"));
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
            if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`Notes` LONGTEXT CHARACTER SET utf8mb4 PATH '$.Notes'")));

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
            Assert.That(script, Does.Contain(JsonTableColumn("`first_name` VARCHAR(45) CHARACTER SET utf8mb4 PATH '$.first_name'")));
            Assert.That(script, Does.Contain(JsonTableColumn("`last_name` VARCHAR(45) CHARACTER SET utf8mb4 PATH '$.last_name'")));
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
        if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`country` VARCHAR(50) CHARACTER SET utf8mb4 PATH '$.country'")));
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
            if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`location` LONGTEXT CHARACTER SET utf8mb4 PATH '$.location'")));

            // JSON_TABLE must also read the "<col>.STSrid" companion the extractors emit beside the WKT
            if (UsesJsonTable)
                Assert.That(script, Does.Contain(JsonTableColumn("`location.STSrid` INT PATH '$.\"location.STSrid\"'")),
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
            if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`picture` LONGTEXT CHARACTER SET utf8mb4 PATH '$.picture'")));

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

        if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`amount` DECIMAL(5,2) PATH '$.amount'")));
    }

    [Test]
    public void BuildMergeScript_DatetimeType_GeneratesCorrectFormat()
    {
        using var command = _connection.CreateCommand();
        var tableData = @"[{""first_name"":""TEST"",""last_name"":""ACTOR"",""last_update"":""2024-01-01 12:30:45""}]";

        var script = BuildMergeScript(command, _testDb, "actor",
            tableData, "", false, false, false, false, null!);

        if (UsesJsonTable) Assert.That(script, Does.Contain(JsonTableColumn("`last_update` TIMESTAMP PATH '$.last_update'")));
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
            Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(0), "YEAR 0 is the zero year on every engine, not 2000");
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
