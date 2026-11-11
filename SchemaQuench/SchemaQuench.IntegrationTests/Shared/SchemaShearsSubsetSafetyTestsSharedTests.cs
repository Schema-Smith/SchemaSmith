// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using SchemaShears;
using System;
using System.IO;

namespace SchemaQuench.IntegrationTests.Shared;

// Verifies that a SchemaShears-emitted patch is subset-safe: a table omitted from the patch
// manifest is NOT dropped when the patch is deployed, because PatchBuilder stamps
// DropTablesRemovedFromProduct: false on every category not listed in AllowDrops.
// Companion test confirms that with AllowDrops = ["Tables"] the drop does happen,
// proving the stamp is precisely what provides the protection.
public abstract class SchemaShearsSubsetSafetyTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    /// <summary>A connection string the test can open and switch to <see cref="MainDb"/> on.</summary>
    protected abstract string TestConnectionString { get; }
    /// <summary>The configuration a deploy reads; null where the fixture has already registered it.</summary>
    protected virtual Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig => null;
    protected abstract string ProductPlatformFolder { get; }
    /// <summary>The kept table's file in the patch manifest, named as the engine's package names it.</summary>
    protected virtual string KeptTableManifestEntry => "Templates/Main/Tables/ShearsKeep.json";
    protected virtual string TableExistsSql(string tableName) =>
        $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{MainDb}' AND TABLE_NAME = '{tableName}'";
    protected virtual string DropTablesSql => $@"
DROP TABLE IF EXISTS `{MainDb}`.`ShearsRemovable`;
DROP TABLE IF EXISTS `{MainDb}`.`ShearsKeep`;";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();
    private readonly string _connectionString;
    private readonly string _mainDb;

    protected SchemaShearsSubsetSafetyTestsSharedTests()
    {
        _connectionString = TestConnectionString;
        _mainDb = MainDb;
    }

    [Test]
    public void PatchWithAllowDropsEmpty_ProtectsOmittedTable()
    {
        var tempPatchDir = Path.Join(Path.GetTempPath(), $"ShearsSubset_Protected_{Guid.NewGuid():N}");
        var manifestFile = Path.Join(Path.GetTempPath(), $"ShearsManifest_{Guid.NewGuid():N}.txt");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                var sourcePath = TestHelper.GetTestProductPath(ProductPlatformFolder, "ShearsSource");

                // Deploy the full source product: establishes both tables with product ownership.
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = sourcePath;
                RunSchemaQuench();

                Assert.That(TableExists(cmd, "ShearsKeep"), Is.True, "Setup: ShearsKeep should exist after full deploy.");
                Assert.That(TableExists(cmd, "ShearsRemovable"), Is.True, "Setup: ShearsRemovable should exist after full deploy.");

                // Build a patch that includes only ShearsKeep; AllowDrops is empty so
                // DropTablesRemovedFromProduct is stamped false in the patch Product.json.
                File.WriteAllText(manifestFile, KeptTableManifestEntry);
                new PatchBuilder().Build(new PatchBuildRequest
                {
                    SourcePath = sourcePath,
                    ManifestPath = manifestFile,
                    OutputPath = tempPatchDir,
                    AllowDrops = Array.Empty<string>()
                });

                // Deploy the patch. Ambient env-level DropTablesRemovedFromProduct defaults true
                // (SchemaQuench.settings.json). The patch's stamped false must win.
                _environment.ClearReceivedCalls();
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = tempPatchDir;
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                Assert.Multiple(() =>
                {
                    Assert.That(TableExists(cmd, "ShearsRemovable"), Is.True,
                        "Patch stamp DropTablesRemovedFromProduct:false must veto the drop: ShearsRemovable must still exist.");
                    Assert.That(TableExists(cmd, "ShearsKeep"), Is.True,
                        "ShearsKeep (in the patch) must still exist.");
                });
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                if (File.Exists(manifestFile)) File.Delete(manifestFile);
                if (Directory.Exists(tempPatchDir)) Directory.Delete(tempPatchDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    [Test]
    public void PatchWithAllowDropsTables_DropsOmittedTable()
    {
        var tempPatchDir = Path.Join(Path.GetTempPath(), $"ShearsSubset_Allowed_{Guid.NewGuid():N}");
        var manifestFile = Path.Join(Path.GetTempPath(), $"ShearsManifest_{Guid.NewGuid():N}.txt");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                var sourcePath = TestHelper.GetTestProductPath(ProductPlatformFolder, "ShearsSource");

                // Deploy the full source product: establishes both tables with product ownership.
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = sourcePath;
                RunSchemaQuench();

                Assert.That(TableExists(cmd, "ShearsKeep"), Is.True, "Setup: ShearsKeep should exist after full deploy.");
                Assert.That(TableExists(cmd, "ShearsRemovable"), Is.True, "Setup: ShearsRemovable should exist after full deploy.");

                // Build a patch with AllowDrops = ["Tables"]: DropTablesRemovedFromProduct is
                // NOT stamped false, so the patch Product.json defaults true → drop executes.
                File.WriteAllText(manifestFile, KeptTableManifestEntry);
                new PatchBuilder().Build(new PatchBuildRequest
                {
                    SourcePath = sourcePath,
                    ManifestPath = manifestFile,
                    OutputPath = tempPatchDir,
                    AllowDrops = new[] { "Tables" }
                });

                // Deploy the patch. DropTablesRemovedFromProduct is not stamped false,
                // so the ambient true (and the patch Product.json default true) wins → drop.
                _environment.ClearReceivedCalls();
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = tempPatchDir;
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                Assert.Multiple(() =>
                {
                    Assert.That(TableExists(cmd, "ShearsRemovable"), Is.False,
                        "AllowDrops=[Tables] leaves DropTablesRemovedFromProduct unstamped (defaults true): ShearsRemovable must be dropped.");
                    Assert.That(TableExists(cmd, "ShearsKeep"), Is.True,
                        "ShearsKeep (in the patch) must still exist.");
                });
            }
            finally
            {
                DropTablesAndCleanup(cmd);
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                if (File.Exists(manifestFile)) File.Delete(manifestFile);
                if (Directory.Exists(tempPatchDir)) Directory.Delete(tempPatchDir, true);
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
        if (FixtureConfig != null) FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private bool TableExists(System.Data.IDbCommand cmd, string tableName)
    {
        cmd.CommandText = TableExistsSql(tableName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void DropTablesAndCleanup(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = DropTablesSql;
        cmd.ExecuteNonQuery();
    }
}
