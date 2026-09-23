// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.Globalization;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
[SetUpFixture]
public class FixtureSetup
{
    private string _integrationMainDb = "";
    private string _integrationSecondaryDb = "";
    private string _connectionString;

    // All four engine SetUpFixtures publish to the SAME global Target:* / ScriptTokens:* keys on the
    // shared IConfigurationRoot (last-writer-wins). In the full unfiltered run a sibling engine
    // fixture's OneTimeSetUp (parallel worker lane) can overwrite them while a PostgreSQL schema-template
    // test is mid-quench. This captured snapshot lets those tests re-assert PostgreSQL's target under
    // SharedLockObject before the quench reads Target:* live. See ApplyTargetConfig.
    private static Dictionary<string, string> _targetConfig;

    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var pgConnProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");

        _integrationSecondaryDb = GenerateUniqueDBName("TestSecondary");
        _integrationMainDb = GenerateUniqueDBName("TestMain");

        _targetConfig = new Dictionary<string, string>
        {
            ["Target:Server"] = config["PostgreSQL:Server"] ?? "127.0.0.1",
            ["Target:Port"] = config["PostgreSQL:Port"],
            ["Target:User"] = config["PostgreSQL:User"],
            ["Target:Password"] = config["PostgreSQL:Password"],
            ["ScriptTokens:MainDB"] = _integrationMainDb,
            ["ScriptTokens:SecondaryDB"] = _integrationSecondaryDb,
        };
        foreach (var prop in pgConnProps)
            _targetConfig[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;

        // Publish under the shared lock so a concurrently-initialising sibling engine fixture can't
        // interleave a half-written Target block (each fixture's write is now lock-guarded).
        lock (FactoryContainer.SharedLockObject)
            ApplyTargetConfig(config);

        _connectionString = ConnectionString.Build(Platform.PostgreSQL, _targetConfig["Target:Server"], "postgres", _targetConfig["Target:User"], _targetConfig["Target:Password"], _targetConfig["Target:Port"], pgConnProps);

        DropStaleTestDatabases();
        CreateTestDatabases();
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
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;

            cmd.CommandText = "SELECT datname FROM pg_database WHERE datname ~ '^(TestMain|TestSecondary)_[0-9]{8}_'";
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

    /// <summary>
    /// Re-applies PostgreSQL's Target:* / ScriptTokens:* onto the shared config. All four engine
    /// SetUpFixtures write these same global keys, so a sibling fixture's OneTimeSetUp can overwrite
    /// them mid-run; PostgreSQL schema-template tests call this while holding SharedLockObject so the
    /// quench connects to PostgreSQL rather than a sibling engine's target. Caller MUST hold
    /// FactoryContainer.SharedLockObject.
    /// </summary>
    internal static void ApplyTargetConfig(IConfigurationRoot config)
    {
        if (_targetConfig == null) return;
        foreach (var kv in _targetConfig)
            config[kv.Key] = kv.Value;
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE ""{_integrationSecondaryDb}"";

CREATE DATABASE ""{_integrationMainDb}"";
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationMainDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        cmd.CommandText = @"
CREATE DOMAIN ""Flag"" AS BOOLEAN NOT NULL;
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationSecondaryDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationSecondaryDb);
        DropOneDatabase(cmd, _integrationMainDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{dbName}"";";
        cmd.ExecuteNonQuery();
    }
}
