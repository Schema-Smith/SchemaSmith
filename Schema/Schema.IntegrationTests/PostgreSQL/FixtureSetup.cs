// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Globalization;
using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[SetUpFixture]
public class FixtureSetup
{
    private static string _integrationMainDb = "";
    private static string _postgresConnectionString = "";
    private static string _server = "";
    private static string _port = "";
    private static string _user = "";
    private static string _password = "";
    private static Dictionary<string, string> _connectionProperties = new();
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

    /// <summary>
    /// Ensures the test database is initialized. Called automatically when accessing properties.
    /// </summary>
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

        _server = config["PostgreSQL:Server"] ?? "127.0.0.1";
        _port = config["PostgreSQL:Port"];
        _user = config["PostgreSQL:User"];
        _password = config["PostgreSQL:Password"];
        _connectionProperties = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        _postgresConnectionString = ConnectionString.Build(Platform.PostgreSQL, _server, "postgres", _user, _password, _port, _connectionProperties);
        _integrationMainDb = GenerateUniqueDBName("schemainttest");

        DropStaleTestDatabases();
        CreateTestDatabase();
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
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_postgresConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;

            cmd.CommandText = "SELECT datname FROM pg_database WHERE datname ~ '^(schemainttest)_[0-9]{8}_'";
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
                    // Terminate stragglers first: PostgreSQL refuses to drop a database with live backends.
                    cmd.CommandText = $@"SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                                          WHERE datname = '{db}' AND pid <> pg_backend_pid()";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = $@"DROP DATABASE IF EXISTS ""{db}""";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception) { /* in use by a live run; leave it */ }
            }
        }
        catch (Exception)
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
        DropTestDatabase();
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_postgresConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"CREATE DATABASE ""{_integrationMainDb}"";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationMainDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}".ToLowerInvariant();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_postgresConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = '{_integrationMainDb}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"DROP DATABASE IF EXISTS ""{_integrationMainDb}"";";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    /// <summary>
    /// Gets a connection string targeting the main test database.
    /// </summary>
    public static string GetMainDbConnectionString()
    {
        EnsureInitialized();
        return ConnectionString.Build(Platform.PostgreSQL, _server, _integrationMainDb, _user, _password, _port, _connectionProperties);
    }
}
