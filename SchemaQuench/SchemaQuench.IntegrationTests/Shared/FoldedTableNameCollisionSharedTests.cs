// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using log4net;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using System;
using System.IO;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A package declaring two tables whose names differ only in case. On a server with lower_case_table_names = 0 they
/// are two tables and both deploy. On a server that folds table names they would be one table, so the deploy is
/// refused (exit 2) before it touches either.
/// </summary>
public abstract class FoldedTableNameCollisionSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private const string Declared = "FoldCollide";
    private const string Sibling = "foldcollide";

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void TablesDifferingOnlyInCase_DeployOnACaseSensitiveServer_AndAreRefusedWhereTheServerFoldsNames()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"FoldCollide_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            _progressLog.ClearReceivedCalls();
            _errorLog.ClearReceivedCalls();
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(FixtureConfig);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            BuildPackage(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(BaseConnectionString + "Database=information_schema;");
            conn.Open();
            conn.ChangeDatabase(MainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = "SELECT @@lower_case_table_names";
            var serverFoldsNames = Convert.ToInt32(cmd.ExecuteScalar()) != 0;

            try
            {
                Drop(cmd);
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = tempDir;
                Program.Main(["SkipKindlingForge"]);

                if (serverFoldsNames)
                {
                    _environment.Received().Exit(2);
                    _errorLog.Received().Error(Arg.Is<string>(s => s.Contains("differ only in case")));
                    Assert.That(TableCount(cmd), Is.EqualTo(0), "a refused deploy must not create either table");
                }
                else
                {
                    _environment.DidNotReceive().Exit(2);
                    Assert.That(TableCount(cmd), Is.EqualTo(2), "a case-sensitive server holds both tables");
                }
            }
            finally
            {
                Drop(cmd);
                FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>()["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // A server that folds names reports the database in lowercase, while Target.Databases holds the configured
    // spelling; the filter must still select it rather than reject the run as naming an unknown database.
    [Test]
    public void TargetDatabases_NamingTheConfiguredDatabase_SelectsItOnEveryServer()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"FoldTarget_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            _progressLog.ClearReceivedCalls();
            _errorLog.ClearReceivedCalls();
            _environment.ClearReceivedCalls();
            FactoryContainer.Register(FixtureConfig);
            FactoryContainer.Register(_environment);
            LogFactory.Register("ErrorLog", _errorLog);
            LogFactory.Register("ProgressLog", _progressLog);

            BuildPackage(tempDir, Declared);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(BaseConnectionString + "Database=information_schema;");
            conn.Open();
            conn.ChangeDatabase(MainDb);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                Drop(cmd);
                config["SchemaPackagePath"] = tempDir;
                config["Target:Databases:0"] = MainDb;
                Program.Main(["SkipKindlingForge"]);

                _environment.DidNotReceive().Exit(2);
                Assert.That(TableCount(cmd), Is.EqualTo(1), "the database Target.Databases names must be deployed to");
            }
            finally
            {
                TemplateTargetsTestSupport.ClearTargetFilters(config);
                Drop(cmd);
                config["SchemaPackagePath"] = string.Empty;
                Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    private void BuildPackage(string dest, params string[] names)
    {
        CopyDirectory(TestHelper.GetTestProductPath(ProductPlatformFolder, "DropProtection"), dest);
        var tables = Path.Join(dest, "Templates", "Main", "Tables");
        foreach (var file in Directory.GetFiles(tables)) File.Delete(file);
        foreach (var name in names.Length == 0 ? new[] { Declared, Sibling } : names)
            File.WriteAllText(Path.Join(tables, $"{name}_{(name == Declared ? "upper" : "lower")}.json"), $$"""
                {
                    "Name": "`{{name}}`",
                    "Engine": "InnoDB",
                    "Columns": [{"Name": "`Id`","DataType": "INT","Nullable": false}],
                    "Indexes": [{"Name": "PRIMARY","PrimaryKey": true,"IndexColumns": "`Id`"}]
                }
                """);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Join(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Join(dest, Path.GetFileName(dir)));
    }

    private int TableCount(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.TABLES WHERE {MySqlNameMatch.Folded("TABLE_SCHEMA", $"'{MainDb}'")} " +
                          $"AND LOWER(TABLE_NAME) = '{Sibling}'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Drop(System.Data.IDbCommand cmd)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS `{MainDb}`.`{Declared}`; DROP TABLE IF EXISTS `{MainDb}`.`{Sibling}`;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DELETE FROM `{MainDb}`.SchemaSmith_ProductOwnership WHERE LOWER(ObjectName) = '{Sibling}'";
        cmd.ExecuteNonQuery();
    }
}
