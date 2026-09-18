// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchemaQuench.IntegrationTests.PostgreSQL;

// Verifies the sticky per-table PreventDrop guard: a protected table removed from the package is
// NOT dropped (protection is persisted in ownership tracking, not the package JSON), while an
// unprotected sibling IS dropped by absence. Product- and env-level DropTablesRemovedFromProduct
// are both true here, so the ONLY thing keeping ProtectedTable alive is its sticky marker.
[Category("PostgreSQL")]
public class TableQuench_StickyPreventDropTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    public TableQuench_StickyPreventDropTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["Target:Server"], "postgres",
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    [Test]
    public void ProtectedTable_RemovedFromPackage_IsNotDropped_AndRunSucceeds()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"StickyPreventDrop_NotDropped_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            CopyFixtureTo(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                // First quench: establish both tables with product ownership.
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                RunSchemaQuench();

                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True, "Setup: ProtectedTable should exist.");
                Assert.That(TableExists(cmd, "RemovableTable"), Is.True, "Setup: RemovableTable should exist.");

                // Remove BOTH tables' JSON from the temp package so drop-by-absence targets both.
                File.Delete(Path.Join(tempDir, "Templates", "Main", "Tables", "public.ProtectedTable.json"));
                File.Delete(Path.Join(tempDir, "Templates", "Main", "Tables", "public.RemovableTable.json"));

                // Clear first-quench call history so the DidNotReceive assertions below target only the second quench.
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                Assert.Multiple(() =>
                {
                    Assert.That(TableExists(cmd, "ProtectedTable"), Is.True,
                        "Sticky PreventDrop must veto the drop: ProtectedTable must still exist.");
                    Assert.That(TableExists(cmd, "RemovableTable"), Is.False,
                        "Unprotected RemovableTable must be dropped by absence.");
                    Assert.That(TableExists(cmd, "KeeperTable"), Is.True,
                        "KeeperTable stays in the package (keeps the drop pass active) and must survive.");
                });
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                conn.Close();
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void ProtectedTable_StaysProtected_AcrossASecondAbsentRun()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"StickyPreventDrop_Persists_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            CopyFixtureTo(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                // First quench: establish both tables.
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                RunSchemaQuench();

                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True, "Setup: ProtectedTable should exist.");

                // Remove both tables' JSON so ProtectedTable is absent from the package.
                File.Delete(Path.Join(tempDir, "Templates", "Main", "Tables", "public.ProtectedTable.json"));
                File.Delete(Path.Join(tempDir, "Templates", "Main", "Tables", "public.RemovableTable.json"));

                // Second quench with ProtectedTable absent: sticky marker keeps it alive.
                _environment.ClearReceivedCalls();
                RunSchemaQuench();
                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True,
                    "ProtectedTable must survive the first absent run.");

                // Third quench, still absent: protection is persisted (not the package flag), so it holds.
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True,
                    "Sticky protection is persisted in ownership: ProtectedTable must survive a second absent run.");
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                conn.Close();
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void UnProtect_TwoStep_ThenRemoved_Drops()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"StickyPreventDrop_UnProtect_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            CopyFixtureTo(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                // First quench: establish ProtectedTable with PreventDrop: true (sticky marker set true).
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                RunSchemaQuench();

                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True, "Setup: ProtectedTable should exist.");

                // Step 1 of un-protect: flip PreventDrop to false IN PLACE while the table is still in the package.
                SetTablePreventDrop(tempDir, "public.ProtectedTable.json", false);

                // Second quench with the table still present refreshes the sticky marker to false.
                _environment.ClearReceivedCalls();
                RunSchemaQuench();
                Assert.That(TableExists(cmd, "ProtectedTable"), Is.True,
                    "ProtectedTable still present after the un-protect refresh run.");

                // Step 2 of un-protect: now remove the table from the package.
                File.Delete(Path.Join(tempDir, "Templates", "Main", "Tables", "public.ProtectedTable.json"));

                // Third quench: no longer protected, so it drops by absence.
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.That(TableExists(cmd, "ProtectedTable"), Is.False,
                    "After the two-step un-protect, ProtectedTable must drop by absence.");
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                conn.Close();
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // A tracked object must never have two owners. On PostgreSQL this is now enforced structurally by
    // the ProductOwnership unique key (Schema, TableName, IndexName) with NULLS NOT DISTINCT (applied by
    // kindling, version-adaptively: NULLS NOT DISTINCT on PG15+, an equivalent functional index below it)
    // — parity with SQL Server (one ProductName
    // extended property per object) and MySQL (uk_object on ObjectType/ObjectSchema/ObjectName).
    [Test]
    public void SecondOwnerForSameObject_IsStructurallyRejected()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"OwnUnique_{Guid.NewGuid():N}");
        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            CopyFixtureTo(tempDir);
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            try
            {
                // Deploy establishes StickyPreventDrop ownership of KeeperTable (one ownership row).
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                RunSchemaQuench();
                Assert.That(TableExists(cmd, "KeeperTable"), Is.True, "Setup: KeeperTable should exist and be owned.");

                // A different product claiming the same (Schema, TableName) physical object must be rejected
                // by the unique key — NULLS NOT DISTINCT makes the NULL IndexName (a table) collide.
                cmd.CommandText = @"INSERT INTO ""SchemaSmith"".""ProductOwnership""
                    (""Schema"", ""TableName"", ""IndexName"", ""ProductName"", template_name, ""PreventDrop"")
                    VALUES ('public', 'KeeperTable', NULL, 'OtherProduct', 'OtherTemplate', FALSE);";
                Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception,
                    "A second owner for the same (Schema, TableName) must violate the ProductOwnership unique key.");
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                conn.Close();
                FactoryContainer.Resolve<IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private static void CopyFixtureTo(string dest)
    {
        var src = TestHelper.GetTestProductPath("PostgreSQL", "StickyPreventDrop");
        CopyDirectory(src, dest);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Join(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Join(dest, Path.GetFileName(dir)));
    }

    private static void SetTablePreventDrop(string packageDir, string tableFileName, bool value)
    {
        var path = Path.Join(packageDir, "Templates", "Main", "Tables", tableFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["PreventDrop"] = value;
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TableExists(System.Data.IDbCommand cmd, string tableName)
    {
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{tableName}')";
        return (bool)cmd.ExecuteScalar()!;
    }

    private static void DropTablesAndCleanup(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = @"
DROP TABLE IF EXISTS public.""RemovableTable"";
DROP TABLE IF EXISTS public.""ProtectedTable"";
DROP TABLE IF EXISTS public.""KeeperTable"";";
        cmd.ExecuteNonQuery();
    }
}
