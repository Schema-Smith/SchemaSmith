// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.ServerSettings;

/// <summary>
/// A server whose innodb_default_row_format is COMPACT (or REDUNDANT) caps each column of an index key at 767 bytes, and
/// two tables kindling creates index a wider column: ScriptStamp's primary key (TemplateName, 1024 bytes) and
/// CompletedMigrationScripts' uk_script (the first 200 characters of ScriptPath, 800 bytes). Kindling must still succeed there. The test changes a
/// server-wide default, so it runs only against a throwaway container (scripts/run-server-settings-legs.sh).
/// </summary>
[NonParallelizable]
public abstract class CompactRowFormatKindlingSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }

    private IDbConnection _admin;
    private string _savedFormat;
    private string _db;

    private static IConfigurationRoot TestConfig => ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
    private string Setting(string key) => TestConfig[$"{ConfigPrefix}:{key}"];

    private IDbConnection Open(string database)
    {
        var connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(ConnectionString.Build(Platform, Setting("Server") ?? "127.0.0.1",
            database, Setting("User"), Setting("Password"), Setting("Port"), ConnectionString.ReadProperties(TestConfig, $"{ConfigPrefix}:ConnectionProperties")));
        connection.Open();
        return connection;
    }

    private static object Scalar(IDbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(IDbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [SetUp]
    public void CompactByDefault()
    {
        if (Environment.GetEnvironmentVariable("SCHEMASMITH_DISPOSABLE_SERVER") != "1")
            Assert.Ignore("This changes a server-wide default: run it with scripts/run-server-settings-legs.sh, never against a shared server.");
        _admin = Open(null);
        _savedFormat = Scalar(_admin, "SELECT @@GLOBAL.innodb_default_row_format")?.ToString();
        Exec(_admin, "SET GLOBAL innodb_default_row_format = 'COMPACT'");
        _db = $"kindle_compact_{Guid.NewGuid():N}"[..30];
        Exec(_admin, $"CREATE DATABASE `{_db}`");
    }

    [TearDown]
    public void PutItBack()
    {
        if (_admin == null) return;
        Exec(_admin, $"DROP DATABASE IF EXISTS `{_db}`");
        if (_savedFormat != null) Exec(_admin, $"SET GLOBAL innodb_default_row_format = '{_savedFormat}'");
        _admin.Dispose();
    }

    private void Kindle(bool force = false)
    {
        // A new connection, so the server's new default applies to the tables this session creates.
        using var connection = Open(_db);
        using var cmd = connection.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform, forceReKindle: force);
    }

    private string[] LongKeysMissing() =>
        new[] { ("SchemaSmith_ScriptStamp", "PRIMARY"), ("SchemaSmith_CompletedMigrationScripts", "uk_script") }
            .Where(k => Convert.ToInt32(Scalar(_admin, $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = '{_db}' "
                                                         + $"AND table_name = '{k.Item1}' AND index_name = '{k.Item2}'")) == 0)
            .Select(k => $"{k.Item1}.{k.Item2}").ToArray();

    private string RowFormat(string table) =>
        Scalar(_admin, $"SELECT UPPER(row_format) FROM information_schema.tables WHERE table_schema = '{_db}' AND table_name = '{table}'")?.ToString();

    [Test]
    public void KindlingSucceeds_WhenTheServerCreatesCompactTables()
    {
        Assert.That(() => Kindle(), Throws.Nothing);
        Assert.That(LongKeysMissing(), Is.Empty);
    }

    // A database kindled before, whose table is COMPACT, gains the long key when it is kindled again.
    [Test]
    public void ReKindling_AddsTheLongKeyToATableThatIsCompact()
    {
        Kindle();
        using (var connection = Open(_db))
        {
            // Two statements: MariaDB checks the dropped key against the new row format when both are in one.
            Exec(connection, "ALTER TABLE SchemaSmith_CompletedMigrationScripts DROP INDEX uk_script");
            Exec(connection, "ALTER TABLE SchemaSmith_CompletedMigrationScripts ROW_FORMAT=COMPACT");
        }
        Assert.That(RowFormat("SchemaSmith_CompletedMigrationScripts"), Is.EqualTo("COMPACT"), "the table must be COMPACT before kindling again");
        Assert.That(LongKeysMissing(), Is.EqualTo(new[] { "SchemaSmith_CompletedMigrationScripts.uk_script" }));

        Assert.That(() => Kindle(force: true), Throws.Nothing);
        Assert.That(LongKeysMissing(), Is.Empty);
    }
}

[Category("DisposableServerMySQL")]
public class MySqlCompactRowFormatKindlingTests : CompactRowFormatKindlingSharedTests
{
    protected override Platform Platform => Platform.MySQL;
    protected override string ConfigPrefix => "MySQL";
}

[Category("DisposableServerMariaDb")]
public class MariaDbCompactRowFormatKindlingTests : CompactRowFormatKindlingSharedTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string ConfigPrefix => "MariaDB";
}
