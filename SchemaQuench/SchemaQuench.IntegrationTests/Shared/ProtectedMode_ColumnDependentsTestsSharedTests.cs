// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.IO;
using System.Linq;

namespace SchemaQuench.IntegrationTests.Shared;

// Parity guard for #358 (a SQL-Server-only defect), MySQL-family engines. MySQL/MariaDB build their
// column-drop set (_SchemaSmith_ColumnsToDrop) already gated by DropColumnsRemovedFromProduct, and
// every dependent-cleanup pass keys off that set — so under env PreventDrop the set is empty and a
// preserved column keeps its dependents by construction. This test asserts that guarantee holds.
public abstract class ProtectedMode_ColumnDependentsTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    protected ProtectedMode_ColumnDependentsTestsSharedTests()
    {
        _connectionString = BaseConnectionString + "Database=information_schema;";
        _mainDb = MainDb;
    }

    [Test]
    public void ProtectedMode_RemovedColumn_KeepsAllDependents_Exit0()
    {
        if (!TestVersionGates.SupportsCheckConstraints(Platform, _connectionString))
            Assert.Ignore("Verifies a CHECK-constraint dependent survives; CHECK requires MySQL 8.0.16 — skipped below the floor.");
        var tempDir = Path.Join(Path.GetTempPath(), $"ProtMode_ColDeps_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            CopyFixtureTo(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                RunSchemaQuench();
                Assert.Multiple(() =>
                {
                    Assert.That(ColumnExists(cmd, "Child", "Extra"), Is.True, "Setup: Child.Extra should exist.");
                    Assert.That(IndexExists(cmd, "Child", "IX_Child_Extra"), Is.True, "Setup: IX_Child_Extra should exist.");
                    Assert.That(CheckOnColumnExists(cmd, "Extra"), Is.True, "Setup: CHECK on Extra should exist.");
                    Assert.That(DefaultOnColumnExists(cmd, "Child", "Extra"), Is.True, "Setup: DEFAULT on Extra should exist.");
                });

                var childJson = Path.Join(tempDir, "Templates", "Main", "Tables", "Child.json");
                RemoveColumnFromTable(childJson, "`Extra`");
                RemoveIndexFromTable(childJson, "IX_Child_Extra");
                // The CHECK is declared table-level (the column-level CheckExpression alias is deprecated), so
                // dropping the column from the package means dropping its constraint too -- otherwise the CHECK
                // would survive only because it is still declared, and this would stop proving #358.
                RemoveCheckConstraintFromTable(childJson, "CK_Child_Extra");

                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["PreventDrop"] = "true";
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.Multiple(() =>
                {
                    Assert.That(ColumnExists(cmd, "Child", "Extra"), Is.True,
                        "Protected: the Extra column must survive.");
                    Assert.That(IndexExists(cmd, "Child", "IX_Child_Extra"), Is.True,
                        "Protected: the preserved column's INDEX must survive (#358).");
                    Assert.That(CheckOnColumnExists(cmd, "Extra"), Is.True,
                        "Protected: the preserved column's CHECK must survive (#358).");
                    Assert.That(DefaultOnColumnExists(cmd, "Child", "Extra"), Is.True,
                        "Protected: the preserved column's DEFAULT must survive (#358).");
                });
            }
            finally
            {
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["PreventDrop"] = string.Empty;
                DropTablesAndCleanup(cmd);
                conn.Close();
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    private static void RemoveColumnFromTable(string tableJsonPath, string columnName)
    {
        var root = JObject.Parse(File.ReadAllText(tableJsonPath));
        ((JArray)root["Columns"]!).First(c => (string)c["Name"]! == columnName).Remove();
        File.WriteAllText(tableJsonPath, root.ToString());
    }

    private static void RemoveCheckConstraintFromTable(string tableJsonPath, string constraintName)
    {
        var root = JObject.Parse(File.ReadAllText(tableJsonPath));
        ((JArray)root["CheckConstraints"]!).First(c => (string)c["Name"]! == constraintName).Remove();
        File.WriteAllText(tableJsonPath, root.ToString());
    }

    private static void RemoveIndexFromTable(string tableJsonPath, string indexName)
    {
        var root = JObject.Parse(File.ReadAllText(tableJsonPath));
        ((JArray)root["Indexes"]!).First(i => (string)i["Name"]! == indexName).Remove();
        File.WriteAllText(tableJsonPath, root.ToString());
    }

    private bool ColumnExists(System.Data.IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = '{columnName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private bool IndexExists(System.Data.IDbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{tableName}' AND INDEX_NAME = '{indexName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private bool CheckOnColumnExists(System.Data.IDbCommand cmd, string columnName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{_mainDb}' AND CHECK_CLAUSE LIKE '%{columnName}%'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private bool DefaultOnColumnExists(System.Data.IDbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{tableName}' AND COLUMN_NAME = '{columnName}' AND COLUMN_DEFAULT IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void SetupSharedMocks()
    {
        _progressLog.ClearReceivedCalls();
        _errorLog.ClearReceivedCalls();
        _environment.ClearReceivedCalls();
        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private void CopyFixtureTo(string dest)
    {
        var src = TestHelper.GetTestProductPath(ProductPlatformFolder, "ColumnDependentProtection");
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

    private void DropTablesAndCleanup(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`Child`;";
        cmd.ExecuteNonQuery();
    }
}
