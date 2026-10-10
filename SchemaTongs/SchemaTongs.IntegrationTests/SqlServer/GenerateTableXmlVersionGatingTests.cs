// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Text;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

// Slice E1.1: the 2016-era catalog reads in GenerateTableXml (temporal_type, generated_always_type, the
// Always Encrypted metadata, and sys.masked_columns.masking_function) are staged through a
// fn_ServerMajorVersion() >= 13 guarded dynamic block so the procedure CREATEs on a genuine pre-2016 binary.
// The CREATE-time BINDING proof (a static reference would fail to compile below 2016) is inherently a
// genuine-old-binary check — the modern CI container has every column, so it can only exercise the GATING
// LOGIC. This proves that: baking the detected major below 13 skips the dynamic read, so a masked column's
// mask is dropped (as it must be on a target that cannot support masking), while at/above 13 it is emitted.
// Masking is the representative 2016 read here; temporal / Always Encrypted ride the same version guard.
[Category("SqlServer")]
[TestFixture]
public class GenerateTableXmlVersionGatingTests
{
    private string _integrationDb = "";
    private string _connectionString = "";
    private string _testConnectionString = "";

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = $"GenTableXmlGate_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        _testConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], _integrationDb, config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        // A table carrying a 2016 feature (dynamic data masking) so the version gate has something to drop.
        cmd.CommandText = "CREATE TABLE dbo.Masked (Id INT NOT NULL PRIMARY KEY, Secret VARCHAR(50) MASKED WITH (FUNCTION = 'default()') NULL);";
        cmd.ExecuteNonQuery();

        // A non-default-history-table-name temporal table so the history table identity/retention read
        // rides the same version guard as temporal_type/masking (#depth-gap).
        cmd.CommandText = @"
CREATE TABLE dbo.GatedTemporal (
    Id INT NOT NULL,
    Val VARCHAR(50) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    CONSTRAINT [PK_GatedTemporal] PRIMARY KEY NONCLUSTERED (Id),
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.GatedTemporal_Archive, HISTORY_RETENTION_PERIOD = 3 YEARS));";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void GenerateTableXml_GatesTemporalHistoryReadBelow2016_EmitsAtOrAbove()
    {
        // At/above 2016 the version-gated dynamic block runs, so a non-default history table name/schema
        // 14, not 13: this test asserts TWO gates. The history table name is 2016, but the retention
        // period columns are 2017 -- so 13 emits the name and not the retention, and the minimum that
        // satisfies both is 14. Still below the 2022 ledger gate, which is the point.
        // and an explicit retention period are read and emitted (#depth-gap).
        var gatedAt14 = GenerateWithBakedMajor(14, "GatedTemporal");
        Assert.Multiple(() =>
        {
            Assert.That(gatedAt14, Does.Contain("GatedTemporal_Archive"), "at major 13+ the history table name must be emitted");
            Assert.That(gatedAt14, Does.Contain("3 YEARS"), "at major 13+ the retention period must be emitted");
        });

        // Below 2016 the guard skips the dynamic read entirely (as it must -- a pre-2016 target cannot be
        // temporal at all; DegradeUnsupportedFeatures forces IsTemporal off on the apply side), so the
        // history table name/retention stay unset just like IsTemporal itself.
        var gatedAt12 = GenerateWithBakedMajor(12, "GatedTemporal");
        Assert.Multiple(() =>
        {
            Assert.That(gatedAt12, Does.Not.Contain("GatedTemporal_Archive"), "below major 13 the history table read is gated out");
            Assert.That(gatedAt12, Does.Not.Contain("HistoryRetentionPeriod"), "below major 13 no retention period must be emitted");
        });
    }

    [Test]
    public void GenerateTableXml_GatesMaskingReadBelow2016_EmitsAtOrAbove()
    {
        // At/above 2016 (major 13) the version-gated dynamic block runs, so the mask is read and emitted.
        var maskedAt13 = GenerateWithBakedMajor(13, "Masked");
        Assert.That(maskedAt13, Does.Contain("default()"),
            "at major 13+ the masking read runs, so DataMaskFunction must be emitted");

        // Below 2016 the guard skips the dynamic read (as it must — a pre-2016 target cannot mask), so #ColMeta
        // stays empty and the mask is dropped. This is the same gate that lets the proc CREATE on the old binary.
        var maskedAt12 = GenerateWithBakedMajor(12, "Masked");
        Assert.That(maskedAt12, Does.Not.Contain("default()"),
            "below major 13 the masking read is gated out, so DataMaskFunction must be dropped");
    }

    // BAKE THE MINIMUM THAT SATISFIES THE GATE UNDER TEST, never a comfortably-high number. The baked
    // value makes the proc believe the server is that version, so every gate at or below it opens --
    // including gates for catalogs this server does not have. These two tests baked 16 for gates that
    // are 13, which was harmless until a 2022 gate (ledger_type_desc) was added; it then read a column
    // that does not exist on the CI server (2019) and failed with 'Invalid column name'.
    // In production the baked value comes from detection against the live server, so it never lies.
    // Re-kindle fn_ServerMajorVersion with the baked major (the JSON kindle installs it on both encodings),
    // (re)create GenerateTableXml from its resource, and run it against the given table.
    private string GenerateWithBakedMajor(int major, string table)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Json, serverMajorVersion: major);
        ForgeKindler.KindleOneFile(cmd, "SchemaSmith.GenerateTableXml.sql", Platform.SqlServer);

        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableXml @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var xml = new StringBuilder();
        while (reader.Read()) xml.Append(reader.GetValue(0)?.ToString());
        conn.Close();
        return xml.ToString();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        conn.Close();
    }
}
