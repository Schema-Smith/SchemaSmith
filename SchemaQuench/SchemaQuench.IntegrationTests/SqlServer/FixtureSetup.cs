// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Globalization;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[SetUpFixture]
public class FixtureSetup
{
    private InMemoryKeyStoreProvider _aeProvider;
    private string _integrationMainDb = "";
    private string _integrationSecondaryDb = "";
    private string _connectionString;

    // All four engine SetUpFixtures publish to the SAME global Target:* / ScriptTokens:* keys on the
    // shared IConfigurationRoot (last-writer-wins). In the full unfiltered run a sibling engine
    // fixture's OneTimeSetUp (parallel worker lane) can overwrite them while a SqlServer schema-template
    // test is mid-quench. This captured snapshot lets those tests re-assert SqlServer's target under
    // SharedLockObject before the quench reads Target:* live. See ApplyTargetConfig.
    private static Dictionary<string, string> _targetConfig;

    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var sqlServerConnProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");

        _integrationSecondaryDb = GenerateUniqueDBName("TestSecondary");
        _integrationMainDb = GenerateUniqueDBName("TestMain");

        _targetConfig = new Dictionary<string, string>
        {
            ["Target:Server"] = config["SqlServer:Server"] ?? "127.0.0.1",
            ["Target:Port"] = config["SqlServer:Port"],
            ["Target:User"] = config["SqlServer:User"],
            ["Target:Password"] = config["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _integrationMainDb,
            ["ScriptTokens:SecondaryDB"] = _integrationSecondaryDb,
        };
        foreach (var prop in sqlServerConnProps)
            _targetConfig[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;

        // Publish under the shared lock so a concurrently-initialising sibling engine fixture can't
        // interleave a half-written Target block (each fixture's write is now lock-guarded).
        lock (FactoryContainer.SharedLockObject)
            ApplyTargetConfig(config);

        _connectionString = ConnectionString.Build(Platform.SqlServer, _targetConfig["Target:Server"], "master", _targetConfig["Target:User"], _targetConfig["Target:Password"], _targetConfig["Target:Port"], sqlServerConnProps);

        // Register in-memory key store provider for Always Encrypted tests
        _aeProvider = new InMemoryKeyStoreProvider();
        SqlConnection.RegisterColumnEncryptionKeyStoreProviders(
            new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>
            {
                { InMemoryKeyStoreProvider.ProviderName, _aeProvider }
            });

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
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;

            cmd.CommandText = "SELECT [name] FROM sys.databases "
                              + "WHERE [name] LIKE 'TestMain[_]%' OR [name] LIKE 'TestSecondary[_]%'";
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
    /// Re-applies SqlServer's Target:* / ScriptTokens:* onto the shared config. All four engine
    /// SetUpFixtures write these same global keys, so a sibling fixture's OneTimeSetUp can overwrite
    /// them mid-run; SqlServer schema-template tests call this while holding SharedLockObject so the
    /// quench connects to SqlServer rather than a sibling engine's target. Caller MUST hold
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE [{_integrationSecondaryDb}];

CREATE DATABASE [{_integrationMainDb}];
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationMainDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = "EXEC sys.sp_cdc_enable_db";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TYPE [Flag] FROM BIT NOT NULL

CREATE TABLE SchemaSmith.TestLog (Id INT IDENTITY(1,1) NOT NULL, Msg NVARCHAR(2000) NOT NULL)

CREATE FULLTEXT CATALOG [FT_Catalog]
CREATE FULLTEXT STOPLIST [SL_Test];
ALTER FULLTEXT STOPLIST [SL_Test] ADD '$' LANGUAGE 'Neutral';

CREATE FULLTEXT CATALOG [FT_Catalog2]
CREATE FULLTEXT STOPLIST [SL_Test2];
ALTER FULLTEXT STOPLIST [SL_Test2] ADD '$' LANGUAGE 'Neutral';
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE XML SCHEMA COLLECTION ManuInstructionsSchemaCollection AS
N'<?xml version=""1.0"" encoding=""UTF-16""?>
<xsd:schema targetNamespace=""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   xmlns          =""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   elementFormDefault=""qualified""
   attributeFormDefault=""unqualified""
   xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" >

    <xsd:complexType name=""StepType"" mixed=""true"" >
        <xsd:choice  minOccurs=""0"" maxOccurs=""unbounded"" >
            <xsd:element name=""tool"" type=""xsd:string"" />
            <xsd:element name=""material"" type=""xsd:string"" />
            <xsd:element name=""blueprint"" type=""xsd:string"" />
            <xsd:element name=""specs"" type=""xsd:string"" />
            <xsd:element name=""diag"" type=""xsd:string"" />
        </xsd:choice>
    </xsd:complexType>

    <xsd:element  name=""root"">
        <xsd:complexType mixed=""true"">
            <xsd:sequence>
                <xsd:element name=""Location"" minOccurs=""1"" maxOccurs=""unbounded"">
                    <xsd:complexType mixed=""true"">
                        <xsd:sequence>
                            <xsd:element name=""step"" type=""StepType"" minOccurs=""1"" maxOccurs=""unbounded"" />
                        </xsd:sequence>
                        <xsd:attribute name=""LocationID"" type=""xsd:integer"" use=""required""/>
                        <xsd:attribute name=""SetupHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""MachineHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LaborHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LotSize"" type=""xsd:decimal"" use=""optional""/>
                    </xsd:complexType>
                </xsd:element>
            </xsd:sequence>
        </xsd:complexType>
    </xsd:element>
</xsd:schema>';
";
        cmd.ExecuteNonQuery();

        // Create Always Encrypted infrastructure (CMK + CEK)
        var (_, encryptedCekHex) = _aeProvider.GenerateCekForDdl();
        cmd.CommandText = $@"
CREATE COLUMN MASTER KEY [TestCMK]
WITH (KEY_STORE_PROVIDER_NAME = '{InMemoryKeyStoreProvider.ProviderName}', KEY_PATH = '{InMemoryKeyStoreProvider.KeyPath}')

CREATE COLUMN ENCRYPTION KEY [TestCEK]
WITH VALUES (COLUMN_MASTER_KEY = [TestCMK], ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = {encryptedCekHex})
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationSecondaryDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = @"
CREATE TABLE SchemaSmith.TestLog (Id INT IDENTITY(1,1) NOT NULL, Msg NVARCHAR(2000) NOT NULL)
";
        cmd.ExecuteNonQuery();

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
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationSecondaryDb);
        DropOneDatabase(cmd, _integrationMainDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"
IF DB_ID('{dbName}') IS NOT NULL
  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{dbName}];
";
        cmd.ExecuteNonQuery();
    }
}
