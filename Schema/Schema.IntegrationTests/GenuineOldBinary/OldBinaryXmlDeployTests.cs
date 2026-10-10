// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Genuine-old-binary RUNTIME deploy test (Slice E). The sibling OldBinaryXmlKindleTests proves the legacy
// (XML) helper set CREATEs on a real pre-2016 binary (the CREATE-time BINDING axis). This fixture proves the
// complementary RUNTIME axis: that actually APPLYING and then CONVERGING a schema on a genuine old binary
// works — the runtime catalog reads inside the apply procs (all ≤2008, with the 2016 temporal/masking/Always-
// Encrypted reads staged behind fn_ServerMajorVersion()>=13 dynamic blocks that must SKIP below 2016) execute
// without error. It kindles the XML helper set, then runs SchemaSmith.TableQuench (the full-apply orchestrator:
// Parse → MissingTableAndColumn → Modified → MissingIndexes → ForeignKey) across three phases:
//   1. CREATE from nothing — every apply proc's create path (table, clustered index, nonclustered PK, two more
//      nonclustered indexes, self-referencing FK, check, default, statistic).
//   2. Idempotent re-run with the SAME payload — convergence with no work; drives the #Existing* comparison
//      reads in ModifiedTableQuench (where the xml_index_type / is_temporary runtime bugs originally surfaced).
//   3. CONVERGE-BY-CHANGE with a MODIFIED payload — removes the FK, the check, the statistic and one index
//      (driving the guarded DROP CONSTRAINT / DROP INDEX / DROP STATISTICS DDL that replaced 2016 `DROP … IF
//      EXISTS`), renames a second index (structural rename detection), and widens a column (the ALTER COLUMN
//      path). This is the phase that actually EXECs the pre-2016 drop guards on a genuine old binary.
// Runs at the server's default compat AND compat 100 (the supported floor).
//
// [Explicit], no [Category("SqlServer")] — CI/normal runs never touch it; run deliberately against a genuine
// instance (Ignores on 2016+):
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~GenuineOldBinary
[Explicit("Requires a genuine pre-2016 SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryXmlDeployTests
{
    private const string ProductName = "OldBinDeploy";

    // Self-contained 2008-safe schema: one table exercising column create, a clustered index on a nullable
    // column, a nonclustered PK, a self-referencing FK (references the PK), a check constraint, a default, a
    // statistic, and two extra nonclustered indexes (one to drop, one to rename in phase 3). No FullTextIndex
    // (its catalog would not exist in a transient DB) and no 2016 features (temporal/masking/encryption/columnstore).
    private const string TableInitialJson = """
        [
          {
            "Schema": "[dbo]",
            "Name": "[OldBinDeployTest]",
            "CompressionType": "NONE",
            "Columns": [
              { "Name": "[TestID]", "DataType": "UNIQUEIDENTIFIER" },
              { "Name": "[ParentID]", "DataType": "UNIQUEIDENTIFIER", "Nullable": true },
              { "Name": "[DateCreated]", "DataType": "DATETIME", "Nullable": true },
              { "Name": "[Status]", "DataType": "TINYINT", "Default": "0" },
              { "Name": "[SomeText]", "DataType": "VARCHAR(2000)", "Nullable": true }
            ],
            "Indexes": [
              { "Name": "[CIX_OldBinDeploy_DateCreated]", "CompressionType": "NONE", "Clustered": true, "FillFactor": 100, "IndexColumns": "[DateCreated]" },
              { "Name": "[PK_OldBinDeploy]", "CompressionType": "NONE", "PrimaryKey": true, "Unique": true, "IndexColumns": "[TestID]" },
              { "Name": "[IX_OldBinDeploy_Drop]", "CompressionType": "NONE", "IndexColumns": "[Status]" },
              { "Name": "[IX_OldBinDeploy_Rename]", "CompressionType": "NONE", "IndexColumns": "[ParentID]" }
            ],
            "ForeignKeys": [
              { "Name": "[FK_OldBinDeploy_Self]", "Columns": "[ParentID]", "RelatedTableSchema": "[dbo]", "RelatedTable": "[OldBinDeployTest]", "RelatedColumns": "[TestID]" }
            ],
            "CheckConstraints": [
              { "Name": "[CK_OldBinDeploy_Status]", "Expression": "[Status]<(20)" }
            ],
            "Statistics": [
              { "Name": "[ST_OldBinDeploy_Status]", "Columns": "[Status]", "SampleSize": 100 }
            ]
          }
        ]
        """;

    // Convergence-by-change target: FK, check, statistic and IX_OldBinDeploy_Drop are GONE (drop paths);
    // IX_OldBinDeploy_Rename becomes IX_OldBinDeploy_Renamed with the same structure (structural rename);
    // [SomeText] widens VARCHAR(2000) -> VARCHAR(4000) (ALTER COLUMN path). Core (table/cols/PK/clustered/default)
    // stays.
    private const string TableModifiedJson = """
        [
          {
            "Schema": "[dbo]",
            "Name": "[OldBinDeployTest]",
            "CompressionType": "NONE",
            "Columns": [
              { "Name": "[TestID]", "DataType": "UNIQUEIDENTIFIER" },
              { "Name": "[ParentID]", "DataType": "UNIQUEIDENTIFIER", "Nullable": true },
              { "Name": "[DateCreated]", "DataType": "DATETIME", "Nullable": true },
              { "Name": "[Status]", "DataType": "TINYINT", "Default": "0" },
              { "Name": "[SomeText]", "DataType": "VARCHAR(4000)", "Nullable": true }
            ],
            "Indexes": [
              { "Name": "[CIX_OldBinDeploy_DateCreated]", "CompressionType": "NONE", "Clustered": true, "FillFactor": 100, "IndexColumns": "[DateCreated]" },
              { "Name": "[PK_OldBinDeploy]", "CompressionType": "NONE", "PrimaryKey": true, "Unique": true, "IndexColumns": "[TestID]" },
              { "Name": "[IX_OldBinDeploy_Renamed]", "CompressionType": "NONE", "IndexColumns": "[ParentID]" }
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

    [Test]
    public void FullTableApply_OnGenuineOldBinary_AtServerDefaultCompat() => DeployAndAssert(setCompat100: false);

    [Test]
    public void FullTableApply_OnGenuineOldBinary_AtCompat100() => DeployAndAssert(setCompat100: true);

    private void DeployAndAssert(bool setCompat100)
    {
        if (_serverMajor >= 13)
            Assert.Ignore($"Detected SQL Server major {_serverMajor} (2016+); this old-binary runtime path only matters below 2016.");

        var db = CreateDatabase(setCompat100 ? "OldBinDeploy100" : "OldBinDeployDflt", setCompat100);
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;

        // Kindle the legacy helper set (baked major, as production does).
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml, _serverMajor, "warn");

        var initialXml = ModelXmlSerializer.ToIngestXml(TableInitialJson, "Tables", "Table");
        var modifiedXml = ModelXmlSerializer.ToIngestXml(TableModifiedJson, "Tables", "Table");
        var at = setCompat100 ? " at compatibility level 100." : ".";

        // Phase 1 — create from nothing.
        Assert.DoesNotThrow(() => RunTableQuench(cmd, initialXml),
            $"TableQuench must apply the schema on SQL Server major {_serverMajor}{at}");
        AssertInitialSchema(cmd, "after first apply");

        // Phase 2 — idempotent re-run (same payload): convergence with no work.
        Assert.DoesNotThrow(() => RunTableQuench(cmd, initialXml),
            "A second identical TableQuench (idempotent re-run) must converge without error on the old binary.");
        AssertInitialSchema(cmd, "after idempotent re-run");

        // Phase 3 — converge by change: this drives the guarded DROP CONSTRAINT / DROP INDEX / DROP STATISTICS
        // DDL (which replaced 2016 `DROP … IF EXISTS`), a structural index rename, and an ALTER COLUMN, all at
        // RUNTIME on a genuine old binary.
        Assert.DoesNotThrow(() => RunTableQuench(cmd, modifiedXml),
            "Converge-by-change (drop FK/check/statistic/index, rename index, widen column) must run without error on the old binary.");
        AssertModifiedSchema(cmd, "after converge-by-change");

        conn.Close();
    }

    private static void RunTableQuench(IDbCommand cmd, string tableXml)
    {
        cmd.CommandText = "SchemaSmith.TableQuench";
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Clear();
        AddParam(cmd, "@ProductName", ProductName);
        AddParam(cmd, "@TableDefinitions", tableXml);
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

    private static void AssertInitialSchema(IDbCommand cmd, string phase)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.OldBinDeployTest','U') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(1), $"table dbo.OldBinDeployTest must exist ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.OldBinDeployTest')"),
                Is.EqualTo(5), $"all 5 columns must exist ({phase})");
            Assert.That(IndexExists(cmd, "PK_OldBinDeploy"), Is.True, $"PK index must exist ({phase})");
            Assert.That(IndexExists(cmd, "CIX_OldBinDeploy_DateCreated"), Is.True, $"clustered index must exist ({phase})");
            Assert.That(IndexExists(cmd, "IX_OldBinDeploy_Drop"), Is.True, $"IX_OldBinDeploy_Drop must exist ({phase})");
            Assert.That(IndexExists(cmd, "IX_OldBinDeploy_Rename"), Is.True, $"IX_OldBinDeploy_Rename must exist ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.FK_OldBinDeploy_Self','F') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(1), $"self-referencing FK must exist ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.CK_OldBinDeploy_Status','C') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(1), $"check constraint must exist ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.OldBinDeployTest')"),
                Is.GreaterThanOrEqualTo(1), $"the [Status] default must exist ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM sys.stats WHERE object_id = OBJECT_ID('dbo.OldBinDeployTest') AND name = 'ST_OldBinDeploy_Status'"),
                Is.EqualTo(1), $"the statistic must exist ({phase})");
        });
    }

    private static void AssertModifiedSchema(IDbCommand cmd, string phase)
    {
        Assert.Multiple(() =>
        {
            // Core survives.
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.OldBinDeployTest','U') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(1), $"table must still exist ({phase})");
            Assert.That(IndexExists(cmd, "PK_OldBinDeploy"), Is.True, $"PK must survive ({phase})");
            Assert.That(IndexExists(cmd, "CIX_OldBinDeploy_DateCreated"), Is.True, $"clustered index must survive ({phase})");

            // Drops actually happened (the guarded DROP DDL ran).
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.FK_OldBinDeploy_Self','F') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(0), $"FK must be dropped ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT CASE WHEN OBJECT_ID('dbo.CK_OldBinDeploy_Status','C') IS NULL THEN 0 ELSE 1 END"),
                Is.EqualTo(0), $"check constraint must be dropped ({phase})");
            Assert.That(IndexExists(cmd, "IX_OldBinDeploy_Drop"), Is.False, $"IX_OldBinDeploy_Drop must be dropped ({phase})");
            Assert.That(ScalarInt(cmd, "SELECT COUNT(*) FROM sys.stats WHERE object_id = OBJECT_ID('dbo.OldBinDeployTest') AND name = 'ST_OldBinDeploy_Status'"),
                Is.EqualTo(0), $"the statistic must be dropped ({phase})");

            // Rename converged (end state is the renamed index present, the old name gone — whether via
            // sp_rename or drop+recreate).
            Assert.That(IndexExists(cmd, "IX_OldBinDeploy_Renamed"), Is.True, $"renamed index must exist ({phase})");
            Assert.That(IndexExists(cmd, "IX_OldBinDeploy_Rename"), Is.False, $"old index name must be gone ({phase})");

            // Column widened (ALTER COLUMN path). VARCHAR(4000) -> max_length 4000.
            Assert.That(ScalarInt(cmd, "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.OldBinDeployTest') AND name = 'SomeText'"),
                Is.EqualTo(4000), $"[SomeText] must be widened to VARCHAR(4000) ({phase})");
        });
    }

    private static bool IndexExists(IDbCommand cmd, string indexName) =>
        ScalarInt(cmd, $"SELECT CASE WHEN INDEXPROPERTY(OBJECT_ID('dbo.OldBinDeployTest'),'{indexName}','IndexID') IS NULL THEN 0 ELSE 1 END") == 1;

    private static int ScalarInt(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string CreateDatabase(string prefix, bool setCompat100)
    {
        var db = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}];" + (setCompat100 ? $" ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100;" : "");
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
            // Classic guard, not DROP DATABASE IF EXISTS (2016 syntax) — this fixture targets pre-2016 binaries.
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
