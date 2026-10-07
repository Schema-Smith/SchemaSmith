// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// A server whose <c>sql_mode</c> changes how SQL text parses (<c>ANSI_QUOTES</c>, <c>PIPES_AS_CONCAT</c>,
/// <c>NO_BACKSLASH_ESCAPES</c>, MariaDB's <c>ORACLE</c>) is a legitimate configuration. A stored routine keeps the mode it
/// was created under, so the kindle used to bake that mode into every quench procedure, and data delivery escaped its
/// payload for a reading the server was not using. The session here stands in for such a server.
/// </summary>
public abstract class ParseAlteringSqlModeSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainConnectionString { get; }
    protected abstract IEnumerable<string> ParseAlteringModes { get; }

    private IDbConnection _connection = null!;
    private string _testDb = null!;

    private sealed class Column : IDeliverableColumn
    {
        public string Name { get; set; }
        public bool Nullable { get; set; }
    }

    private sealed class Table : IDeliverableTable
    {
        public string Name { get; set; }
        public string Schema { get; set; }
        public IReadOnlyList<DataDelivery> DataDeliveries { get; set; } = new List<DataDelivery>();
        public IReadOnlyList<IDeliverableColumn> DeliverableColumns { get; set; } = new List<IDeliverableColumn>();
        public IReadOnlyList<IDeliverableForeignKey> DeliverableForeignKeys { get; set; } = new List<IDeliverableForeignKey>();
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();
        _testDb = $"ss_sqlmode_{(Platform == Platform.MariaDb ? "mariadb" : "mysql")}";
    }

    [SetUp]
    public void SetUp()
    {
        Exec("SET SESSION sql_mode = DEFAULT");
        Exec($"DROP DATABASE IF EXISTS `{_testDb}`");
        Exec($"CREATE DATABASE `{_testDb}`");
        Exec($"USE `{_testDb}`");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            if (_connection is { State: ConnectionState.Open })
            {
                Exec("SET SESSION sql_mode = DEFAULT");
                Exec($"DROP DATABASE IF EXISTS `{_testDb}`");
            }
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    [Test]
    public void Kindle_UnderAParseAlteringMode_CreatesNeutralRoutines_RestoresTheSession_AndDeploys()
    {
        foreach (var mode in ParseAlteringModes)
        {
            SetUp();
            KindleAndDeployUnder(mode);
        }
    }

    private void KindleAndDeployUnder(string mode)
    {
        Exec($"SET SESSION sql_mode = '{mode}'");
        var sessionMode = Scalar("SELECT @@SESSION.sql_mode");

        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(Command(), Platform, forceReKindle: true));

        Assert.Multiple(() =>
        {
            Assert.That(Count($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_testDb}'"), Is.GreaterThan(0));
            foreach (var flag in MySqlSessionSettings.ParseAlteringModes)
                Assert.That(Count($"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{_testDb}' " +
                                  $"AND CONCAT(',', SQL_MODE, ',') LIKE '%,{flag},%'"),
                    Is.Zero, $"under '{mode}', no kindled routine may carry {flag}");
            Assert.That(Scalar("SELECT @@SESSION.sql_mode"), Is.EqualTo(sessionMode), "the session's own mode is restored");
        });

        Exec("""
             CALL SchemaSmith_TableQuench('ParseAlteringSqlModeProduct', DATABASE(),
               '[{ "Name": "`sqlmode_t`", "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false } ],
                   "Indexes": [ { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ] }]', 0, 0, 0)
             """);
        Assert.That(Count($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = 'sqlmode_t'"), Is.EqualTo(1),
            $"a deploy under '{mode}'");
    }

    // The payload is escaped for backslash-escape parsing. Read under NO_BACKSLASH_ESCAPES, every backslash in the data
    // arrived doubled.
    [Test]
    public void Delivery_UnderNoBackslashEscapes_KeepsBackslashesAsWritten()
    {
        ForgeKindler.KindleTheForge(Command(), Platform, forceReKindle: true);
        Exec("CREATE TABLE sqlmode_data (id INT NOT NULL PRIMARY KEY, val VARCHAR(50))");
        Exec("SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, ',NO_BACKSLASH_ESCAPES')");

        Deliver("sqlmode_data", """[{"id": 1, "val": "C:\\temp\\x"}]""");

        Exec("SET SESSION sql_mode = DEFAULT");
        Assert.That(Scalar("SELECT val FROM sqlmode_data WHERE id = 1"), Is.EqualTo(@"C:\temp\x"));
    }

    // A key-0 AUTO_INCREMENT row is a real row. Without NO_AUTO_VALUE_ON_ZERO the engine treated 0 as "generate one",
    // so each delivery added the row again under a new id.
    [Test]
    public void Delivery_OfAKeyZeroRow_KeepsIdZero_AcrossTwoDeliveries()
    {
        ForgeKindler.KindleTheForge(Command(), Platform, forceReKindle: true);
        Exec("CREATE TABLE sqlmode_zero (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, val VARCHAR(50))");
        const string data = """[{"id": 0, "val": "zero"}, {"id": 1, "val": "one"}]""";

        Deliver("sqlmode_zero", data);
        Deliver("sqlmode_zero", data);

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT COUNT(*) FROM sqlmode_zero"), Is.EqualTo(2));
            Assert.That(Scalar("SELECT val FROM sqlmode_zero WHERE id = 0"), Is.EqualTo("zero"));
        });
    }

    // A TIMESTAMP is written and read in the session's time zone. Data extracted at +00:00 and delivered on a +05:00
    // session landed five hours early. A delivery that records its extraction zone now runs in it; one that records
    // none still runs in the session's zone, so data written for that zone is unaffected.
    [Test]
    public void Delivery_RecordedTimeZone_LandsTimestampsUnshifted_AndUnmarkedDataKeepsTheSessionZone()
    {
        ForgeKindler.KindleTheForge(Command(), Platform, forceReKindle: true);
        Exec("CREATE TABLE tz_marked (id INT NOT NULL PRIMARY KEY, ts TIMESTAMP NULL)");
        Exec("CREATE TABLE tz_unmarked (id INT NOT NULL PRIMARY KEY, ts TIMESTAMP NULL)");
        const string data = """[{"id": 1, "ts": "2026-01-01 12:00:00"}]""";
        Exec("SET SESSION time_zone = '+05:00'");

        Deliver("tz_marked", data, timeZone: "+00:00", valueColumn: "ts");
        Deliver("tz_unmarked", data, valueColumn: "ts");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT @@SESSION.time_zone"), Is.EqualTo("+05:00"), "the session's zone is restored");
            Exec("SET SESSION time_zone = '+00:00'");
            Assert.That(Scalar("SELECT DATE_FORMAT(ts, '%Y-%m-%d %H:%i') FROM tz_marked"), Is.EqualTo("2026-01-01 12:00"));
            Exec("SET SESSION time_zone = '+05:00'");
            Assert.That(Scalar("SELECT DATE_FORMAT(ts, '%Y-%m-%d %H:%i') FROM tz_unmarked"), Is.EqualTo("2026-01-01 12:00"),
                "unmarked data is read in the session's zone, as before");
        });
        Exec("SET SESSION time_zone = DEFAULT");
    }

    private void Deliver(string tableName, string json, string timeZone = null, string valueColumn = "val")
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Join(tempDir, "data.tabledata"), json);
            using var command = Command();
            var table = new Table
            {
                Name = tableName,
                DeliverableColumns = new List<IDeliverableColumn>
                {
                    new Column { Name = "id", Nullable = false },
                    new Column { Name = valueColumn, Nullable = true }
                },
                DataDeliveries = new List<DataDelivery>
                {
                    new() { MergeType = "Insert/Update", ContentFile = "data.tabledata", MatchColumns = "id", TimeZone = timeZone }
                }
            };
            DataDeliveryProcessor.GetFromFactory().DeliverTables(new DataDeliveryContext
            {
                Tables = new List<IDeliverableTable> { table },
                Platform = "MySQL",
                Command = command,
                DatabaseName = _testDb,
                TemplateRootPath = tempDir,
                ScriptHelper = new MergeScriptHelperAdapter(Platform),
                ReadFileContent = File.ReadAllText,
                ExecuteScript = (_, sql) => { command.CommandText = sql; command.ExecuteNonQuery(); },
                ProgressLog = _ => { },
                ProgressLogError = _ => { }
            });
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private IDbCommand Command()
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string Scalar(string sql)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private long Count(string sql) => Convert.ToInt64(Scalar(sql));
}
