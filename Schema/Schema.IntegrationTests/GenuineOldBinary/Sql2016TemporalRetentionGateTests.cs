// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Genuine-SQL-Server-2016 milestone test. SQL Server 2016 (major 13) had NO coverage anywhere: the sibling
// OldBinaryXml* fixtures deliberately Ignore at 2016+, and the CI container is SQL Server 2019. That left a
// blind spot on exactly the binary where two version axes cross.
//
// 2016 runs the XML ingest encoding (CompatEncoding routes major < 14 to XML, because the JSON path's
// STRING_AGG is 2017) — so it executes the same version-gated blocks the pre-2016 binaries do. But it
// clears a `fn_ServerMajorVersion() >= 13` gate that its older siblings do not. Any 2017-only catalog read
// placed behind a `>= 13` gate is therefore reachable on 2016 and nowhere else:
//
//   * temporal tables and sys.tables.temporal_type / history_table_id ARE 2016 (major 13), but
//   * sys.tables.history_retention_period / _unit / _unit_desc are 2017 (major 14).
//
// Retention is the younger feature, and reading it at ">= 13" fails on 2016 with "Invalid column name
// 'history_retention_period_unit_desc'" — unconditionally, on any table, because SQL Server binds the
// column for the whole statement whether or not a temporal table is present.
//
// Like its siblings this is [Explicit] and carries NO [Category("SqlServer")], so neither a normal run nor
// the CI Category=SqlServer leg touches it. Run it deliberately against a genuine 2016 instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14333 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~GenuineOldBinary
// Pointed at anything other than major 13 it Ignores: below 13 the >= 13 gates skip, and 14+ has the
// columns, so neither can reproduce the gap this proves.
[Explicit("Requires a genuine SQL Server 2016 instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class Sql2016TemporalRetentionGateTests
{
    private const string ProductName = "Sql2016RetentionGate";

    // Deliberately carries NO temporal table. The defect is unconditional — a 2017-only column referenced in
    // a statement binds (and fails) regardless of whether any table is system-versioned — so proving it with
    // an ordinary table is both the tighter test and the closer match to what a user actually hits.
    private const string TableJson = """
        [
          {
            "Schema": "[dbo]",
            "Name": "[Sql2016RetentionGateTest]",
            "CompressionType": "NONE",
            "Columns": [
              { "Name": "[TestID]", "DataType": "INT" },
              { "Name": "[Name]", "DataType": "VARCHAR(100)", "Nullable": true }
            ],
            "Indexes": [
              { "Name": "[PK_Sql2016RetentionGate]", "CompressionType": "NONE", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[TestID]" }
            ]
          }
        ]
        """;

    private string _masterConnectionString = "";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _connProps = new();
    private int _serverMajor;
    private readonly List<string> _createdDbs = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["SqlServer:Server"] ?? "127.0.0.1";
        _user = config["SqlServer:User"];
        _password = config["SqlServer:Password"];
        _port = config["SqlServer:Port"];
        _connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, _server, "master", _user, _password, _port, _connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        _serverMajor = TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable;
        conn.Close();
    }

    // The user-visible failure: an ordinary deploy to SQL Server 2016 aborts mid-template.
    [Test]
    public void TableQuench_OnGenuineSql2016_DoesNotReadSql2017RetentionColumns()
    {
        var (cmd, conn) = KindledDatabase("Sql2016Quench");
        using (conn)
        {
            Assert.DoesNotThrow(() => RunTableQuench(cmd, TableJson),
                "TableQuench must apply an ordinary (non-temporal) table on SQL Server 2016. A failure naming " +
                "history_retention_period_unit_desc means a 2017-only catalog column is being read behind a " +
                "fn_ServerMajorVersion() >= 13 gate that should be >= 14.");

            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.Sql2016RetentionGateTest','U') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(1), "the table must exist after the deploy");

            // The retention comparison in MissingIndexesAndConstraintsQuench runs on every pass, so a second
            // identical apply must also stay clean.
            Assert.DoesNotThrow(() => RunTableQuench(cmd, TableJson),
                "An idempotent re-run must converge without error on SQL Server 2016.");
            conn.Close();
        }
    }

    // The extraction/comparison side. GenerateTableXml stages its 2016 catalog reads through a >= 13 dynamic
    // block; the 2017 retention columns must not ride along in it.
    [Test]
    public void GenerateTableXml_OnGenuineSql2016_ExecutesWithoutSql2017CatalogColumns()
    {
        var (cmd, conn) = KindledDatabase("Sql2016Generate");
        using (conn)
        {
            cmd.CommandText = "CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(50) NULL)";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => ExecGenerate(cmd, "SchemaSmith.GenerateTableXml", "dbo", "PlainTable"),
                "GenerateTableXml must execute on SQL Server 2016. Its history_retention_period(_unit_desc) " +
                "reads are 2017-only and must sit behind their own major >= 14 gate, not the >= 13 gate that " +
                "carries the genuine 2016 reads (temporal_type, generated_always_type, masking, Always Encrypted).");
            conn.Close();
        }
    }

    // Data delivery's merge BUILD aggregates column lists with STRING_AGG, which is a SQL Server 2017
    // FUNCTION — absent on a 2016 binary at ANY compatibility level. The cliff probe used to ask only
    // "compatibility_level < 130", which answers "modern path" on 2016 (compat 130) and then dies with
    // "'STRING_AGG' is not a recognized built-in function name". These two builders are the public entry
    // points that had no below-cliff fallback at all. Pre-existing since the v2.4.0 floor lowering, not a
    // v2.5.0 regression — see the CHANGELOG entry.
    [Test]
    public void GetJsonSelectColumns_OnGenuineSql2016_AvoidsStringAgg()
    {
        var (cmd, conn) = KindledDatabase("Sql2016SelectCols");
        using (conn)
        {
            CreateDeliveryTable(cmd);
            string result = null;
            Assert.DoesNotThrow(() => result = MergeScriptHelper.GetJsonSelectColumns(Platform.SqlServer, cmd, "dbo", "DeliveryTable"),
                "GetJsonSelectColumns must build on SQL Server 2016 — STRING_AGG is 2017, so it needs the " +
                "same C#-side aggregation fallback its sibling builders already use.");
            Assert.That(result, Does.Contain("Id").And.Contain("Name"),
                "the fallback must return the same column list the STRING_AGG path would");
            conn.Close();
        }
    }

    [Test]
    public void GetJsonColumnDefinitions_OnGenuineSql2016_AvoidsStringAgg()
    {
        var (cmd, conn) = KindledDatabase("Sql2016ColDefs");
        using (conn)
        {
            CreateDeliveryTable(cmd);
            string result = null;
            Assert.DoesNotThrow(() => result = MergeScriptHelper.GetJsonColumnDefinitions(Platform.SqlServer, cmd, "dbo", "DeliveryTable"),
                "GetJsonColumnDefinitions must build on SQL Server 2016 — STRING_AGG is 2017, so it needs the " +
                "same C#-side aggregation fallback its sibling builders already use.");
            Assert.That(result, Does.Contain("Id").And.Contain("Name"),
                "the fallback must return the same OPENJSON WITH column types the STRING_AGG path would");
            conn.Close();
        }
    }

    private static void CreateDeliveryTable(IDbCommand cmd)
    {
        cmd.CommandText = "CREATE TABLE dbo.DeliveryTable (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(100) NULL, Amount DECIMAL(18,2) NULL, When2 DATETIME2(3) NULL)";
        cmd.ExecuteNonQuery();
    }

    private (IDbCommand cmd, IDbConnection conn) KindledDatabase(string prefix)
    {
        if (_serverMajor != 13)
            Assert.Ignore($"Detected SQL Server major {_serverMajor}; this fixture proves a gap that exists only on major 13 (2016) — below it the >= 13 gates skip, above it the 2017 retention columns exist.");

        var db = CreateDatabase(prefix);
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Let production choose the encoding rather than hard-coding one: CompatEncoding routes major 13 to
        // XML (the JSON path's STRING_AGG is 2017), and pinning JSON here would test a combination the
        // product never selects.
        var compat = TargetVersionDetector.Detect(cmd, Platform.SqlServer, db).CompatibilityLevel;
        var encoding = CompatEncoding.Select("auto", compat, _serverMajor);
        Assert.That(encoding, Is.EqualTo(IngestEncoding.Xml),
            "SQL Server 2016 must select the XML encoding; if this changes, this fixture's premise needs revisiting.");

        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding, _serverMajor, "warn");
        return (cmd, conn);
    }

    private static void ExecGenerate(IDbCommand cmd, string proc, string schema, string table)
    {
        cmd.CommandText = proc;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Clear();
        AddParam(cmd, "@p_Schema", schema);
        AddParam(cmd, "@p_Table", table);
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
        cmd.CommandType = CommandType.Text;
    }

    private static void RunTableQuench(IDbCommand cmd, string tableJson)
    {
        cmd.CommandText = "SchemaSmith.TableQuench";
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Clear();
        AddParam(cmd, "@ProductName", ProductName);
        AddParam(cmd, "@TableDefinitions", ModelXmlSerializer.ToIngestXml(tableJson, "Tables", "Table"));
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
        cmd.CommandType = CommandType.Text;
    }

    private static void AddParam(IDbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static int ScalarInt(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string CreateDatabase(string prefix)
    {
        var db = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}]";
        cmd.ExecuteNonQuery();
        conn.Close();
        _createdDbs.Add(db);
        return db;
    }

    private string DbConnectionString(string db) =>
        ConnectionString.Build(Platform.SqlServer, _server, db, _user, _password, _port, _connProps);

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // An idle pooled connection to the database makes ROLLBACK IMMEDIATE wait ~3s to kill it.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        foreach (var db in _createdDbs)
        {
            cmd.CommandText = $@"
IF DB_ID('{db}') IS NOT NULL
BEGIN
  ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE [{db}];
END";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }
}
