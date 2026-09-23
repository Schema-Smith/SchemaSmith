// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.Globalization;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.Shared;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// MariaDb integration fixture — the MariaDb-engine twin of the MySQL FixtureSetup. Same setup
/// (unique per-run databases, kindled forge, shared Sakila-mirroring schema); differs only in the
/// config prefix (MariaDB:*), category, and Platform.MariaDb. Its own static state so it can run in
/// the same process as the MySQL fixture without collision.
/// </summary>
[Category("MariaDb")]
[SetUpFixture]
public class FixtureSetup
{
    private static string _integrationMainDb = "";
    private static string _integrationSecondaryDb = "";
    private static string _connectionString = "";
    private static bool _initialized;
    private static readonly object _lock = new();

    public static string MainDb
    {
        get
        {
            EnsureInitialized();
            return _integrationMainDb;
        }
    }

    public static string SecondaryDb
    {
        get
        {
            EnsureInitialized();
            return _integrationSecondaryDb;
        }
    }

    public static string ConnectionString
    {
        get
        {
            EnsureInitialized();
            return _connectionString;
        }
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            new FixtureSetup().Initialize();
            _initialized = true;
        }
    }

    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        EnsureInitialized();
    }

    private void Initialize()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

        var server = config["MariaDB:Server"] ?? "127.0.0.1";
        var port = config["MariaDB:Port"] ?? "3306";
        var user = config["MariaDB:User"] ?? "TestUser";
        var password = config["MariaDB:Password"] ?? "aCa2d805-41E5@40c4!98e7#92F93zzxo176";

        var mariaProps = Schema.DataAccess.ConnectionString.ReadProperties(config, "MariaDB:ConnectionProperties");
        var extraProps = string.Join("", mariaProps.Select(p => $"{p.Key}={p.Value};"));
        // Non-pooled: the suite creates a unique database per test, and retained idle pool connections
        // across those per-DB pools otherwise pile up past the server's max_connections ceiling.
        _connectionString = $"Server={server};Port={port};User={user};Password={password};AllowUserVariables=true;Pooling=false;{extraProps}";

        // Use the literal base token, NOT config["ScriptTokens:*"]: this method overwrites that key
        // below with the full generated name, and ConfigHelper returns a shared config instance reused
        // by the sibling MySQL fixture. Reading the key back would compound base-on-base
        // (`TestMain_Test_..._MariaTest_...`) past the 64-char identifier limit when both the MySQL and
        // MariaDb categories run in one process (error 1059). The `_MariaTest_`/`_Test_` prefix in
        // GenerateUniqueDBName already keeps the two engines' database names distinct.
        _integrationSecondaryDb = GenerateUniqueDBName("TestSecondary");
        _integrationMainDb = GenerateUniqueDBName("TestMain");

        // Map MariaDB config to Target:* keys used by tools (mutate existing config, don't replace —
        // replacing would lose SqlServer:* and PostgreSQL:* keys needed by other test assemblies).
        // Publish under the shared lock: the four engine fixtures write these same global Target:* /
        // ScriptTokens:* keys, and in the full unfiltered run this OneTimeSetUp can run on the parallel
        // worker lane while a SqlServer/PostgreSQL schema-template test holds the lock mid-quench.
        // Guarding the write means it lands strictly before or after that locked test body, never during.
        lock (FactoryContainer.SharedLockObject)
        {
            config["Target:Server"] = server;
            config["Target:Port"] = port;
            config["Target:User"] = user;
            config["Target:Password"] = password;
            foreach (var prop in mariaProps)
                config[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
            // Product-side connections the quench opens per target DB are non-pooled too (same ceiling reason).
            config["Target:ConnectionProperties:Pooling"] = "false";
            config["ScriptTokens:MainDB"] = _integrationMainDb;
            config["ScriptTokens:SecondaryDB"] = _integrationSecondaryDb;
        }

        DropStaleTestDatabases();
        CreateTestDatabases();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static bool _cleanedUp;

    private static void Cleanup()
    {
        if (_cleanedUp || !_initialized) return;
        _cleanedUp = true;
        new FixtureSetup().DropTestDatabases();
    }

    /// <summary>How old a test database must be before this sweep will drop it.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// Drop test databases abandoned by earlier runs. <c>OneTimeTearDown</c> drops this run's database, but
    /// it never runs when the process is killed or <c>OneTimeSetUp</c> throws — so they accumulate (~180
    /// were cleared by hand across the four engines on 2026-09-19). They are not merely untidy: on the
    /// MySQL family every INFORMATION_SCHEMA read costs roughly 1.8ms per database ON THE SERVER, so strays
    /// tax every quench, and they quietly corrupted several performance measurements. The age cut is what
    /// makes this safe at startup — the generated name embeds <c>yyyyMMdd_HHmmss</c>, so a database
    /// belonging to a sibling suite running right now is far too young to match. Unparseable names are left
    /// alone.
    /// </summary>
    private void DropStaleTestDatabases()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;

            cmd.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA "
                              + "WHERE SCHEMA_NAME REGEXP '^(TestMain|TestSecondary)_[0-9]{8}_'";
            var stale = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    if (IsOlderThanCutoff(name)) stale.Add(name);
                }

            foreach (var db in stale)
            {
                try
                {
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`";
                    cmd.ExecuteNonQuery();
                }
                catch (DbException) { /* in use by a live run; leave it */ }
            }
        }
        catch (DbException)
        {
            // Housekeeping must never stop the suite from starting.
        }
    }

    /// <summary>True when a generated name's embedded timestamp is older than <see cref="StaleAfter"/>.</summary>
    private static bool IsOlderThanCutoff(string databaseName)
    {
        // <prefix>_yyyyMMdd_HHmmss_<8 hex>
        var parts = databaseName.Split('_');
        if (parts.Length < 3) return false;
        var stamp = $"{parts[^3]}_{parts[^2]}";
        if (!DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var created))
            return false;

        // S6561 warns against DateTime.Now in elapsed-time maths, which is about benchmarking. This is
        // wall-clock staleness, and it MUST be local: the name is stamped with DateTime.Now, so comparing
        // in UTC would misjudge every name by the machine's offset.
#pragma warning disable S6561
        return DateTime.Now - created > StaleAfter;
#pragma warning restore S6561
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_integrationSecondaryDb}`;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_integrationMainDb}`;";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationMainDb);
        ForgeKindler.KindleTheForge(cmd, Platform.MariaDb);

        MySqlFamilyTestSchema.Create(cmd, _integrationMainDb);

        conn.ChangeDatabase(_integrationSecondaryDb);
        ForgeKindler.KindleTheForge(cmd, Platform.MariaDb);

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_MariaTest_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            DropOneDatabase(cmd, _integrationSecondaryDb);
            DropOneDatabase(cmd, _integrationMainDb);

            conn.Close();
        }
        catch
        {
            // Ignore errors during cleanup
        }
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        if (string.IsNullOrEmpty(dbName)) return;
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{dbName}`;";
        cmd.ExecuteNonQuery();
    }

    public static string GetMainDbConnectionString()
    {
        EnsureInitialized();
        return _connectionString + $"Database={_integrationMainDb};";
    }

    public static string GetSecondaryDbConnectionString()
    {
        EnsureInitialized();
        return _connectionString + $"Database={_integrationSecondaryDb};";
    }
}
