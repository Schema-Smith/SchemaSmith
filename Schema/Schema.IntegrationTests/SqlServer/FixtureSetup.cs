// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

[Category("SqlServer")]
[SetUpFixture]
public class FixtureSetup
{
    private static string _integrationMainDb = "";
    private static string _masterConnectionString = "";
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

        _server = config["SqlServer:Server"] ?? "127.0.0.1";
        _port = config["SqlServer:Port"];
        _user = config["SqlServer:User"];
        _password = config["SqlServer:Password"];
        _connectionProperties = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");

        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, _server, "master", _user, _password, _port, _connectionProperties);
        _integrationMainDb = GenerateUniqueDBName("SchemaIntTest");

        DropStaleTestDatabases();
        CreateTestDatabase();
    }

    /// <summary>How old a test database must be before this sweep will drop it.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(3);

    /// <summary>
    /// Drop test databases abandoned by earlier runs.
    /// <para><see cref="RunAfterAnyTests"/> drops this run's database — but it never runs when the process
    /// is killed or <c>OneTimeSetUp</c> throws, so they accumulate. On 2026-09-19 roughly 180 strays were
    /// cleared by hand across the four engines, the oldest four days old, and more appeared the same day
    /// from interrupted runs.</para>
    /// <para>They are not merely untidy. On the MySQL family every <c>INFORMATION_SCHEMA</c> read costs
    /// roughly 1.8ms per database ON THE SERVER, so a pile of strays taxes every quench; on SQL Server each
    /// carries its own plan-cache entries. They also quietly corrupted several performance measurements
    /// before anyone noticed they were there.</para>
    /// <para>The age cut is what makes this safe to run at startup: the generated name embeds
    /// <c>yyyyMMdd_HHmmss</c>, so a database belonging to a sibling suite running right now in the same gate
    /// is far too young to match and is never touched. Anything unparseable is left alone.</para>
    /// </summary>
    private void DropStaleTestDatabases()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;

            cmd.CommandText = "SELECT [name] FROM sys.databases WHERE [name] LIKE 'SchemaIntTest[_]%'";
            var stale = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    if (IsOlderThanCutoff(name)) stale.Add(name);
                }

            foreach (var db in stale)
            {
                cmd.CommandText = $@"
IF DB_ID('{db}') IS NOT NULL
BEGIN
    ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{db}];
END";
                try { cmd.ExecuteNonQuery(); } catch (Exception) { /* in use by a live run; leave it */ }
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
        // wall-clock staleness, and it MUST be local: GenerateUniqueDBName stamps the name with
        // DateTime.Now, so comparing in UTC would misjudge every name by the machine's offset.
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationMainDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationMainDb);
        // Pass the DETECTED major version, as SchemaQuench and SchemaTongs both do. Kindling without it
        // bakes 0 into every version-gated helper, which makes them fall back to SERVERPROPERTY at
        // runtime -- so anything resolved at KINDLE time (a catalog column composed in or out) silently
        // disagrees with anything resolved at RUN time. That mismatch is not hypothetical: it made the
        // XML_COMPRESSION comparison read a NULL column as "off" and rebuild on every deploy.
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        var serverMajor = Convert.ToInt32(cmd.ExecuteScalar());
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, serverMajorVersion: serverMajor);

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @$"
IF DB_ID('{_integrationMainDb}') IS NOT NULL
  ALTER DATABASE [{_integrationMainDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{_integrationMainDb}];
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    /// <summary>
    /// Gets a connection string targeting the main test database.
    /// </summary>
    public static string GetMainDbConnectionString()
    {
        EnsureInitialized();
        return ConnectionString.Build(Platform.SqlServer, _server, _integrationMainDb, _user, _password, _port, _connectionProperties);
    }
}
