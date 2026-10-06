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

// Regression for #359: deploying to a MySQL/MariaDB database whose default character set is latin1
// (MariaDB's stock compiled default) must succeed. The forge procs take stored-procedure VARCHAR
// parameters (p_DatabaseName, p_ProductName) in the TARGET database's charset, so on a latin1 database
// a bare `<param> COLLATE utf8mb4_unicode_ci` was rejected ("COLLATION 'utf8mb4_unicode_ci' is not
// valid for CHARACTER SET 'latin1'"), breaking the first table create. The forge kindles fine (its own
// tracking tables are utf8mb4-explicit); the failure is the reconciliation procs' parameter charset.
public abstract class Latin1CharsetDeployTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }
    protected abstract string ProductPlatformFolder { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void Deploy_ToLatin1Database_CreatesTablesAndConstraints_Exit0()
    {
        var latin1Db = "TestLatin1_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"Latin1Deploy_{Guid.NewGuid():N}");
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            CopyFixtureTo(tempDir);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();
            var savedMainDb = config["ScriptTokens:MainDB"];

            try
            {
                // A latin1 target database — the exact condition that broke the deploy (#359).
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{latin1Db}` CHARACTER SET latin1 COLLATE latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(latin1Db);
                // The forge kindles cleanly even on latin1 (its tables are utf8mb4-explicit).
                ForgeKindler.KindleTheForge(cmd, Platform);

                config["SchemaPackagePath"] = tempDir;
                config["ScriptTokens:MainDB"] = latin1Db;
                _environment.ClearReceivedCalls();
                RunSchemaQuench();

                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.Multiple(() =>
                {
                    Assert.That(TableExists(cmd, latin1Db, "KeeperTable"), Is.True,
                        "A table must deploy to a latin1 database (#359).");
                    Assert.That(IndexExists(cmd, latin1Db, "KeeperTable", "IX_KeeperTable_Notes"), Is.True,
                        "The table's index must deploy to a latin1 database (#359).");
                    // CHECK constraints require MySQL 8.0.16 — below the floor SchemaSmith degrades them, so verify
                    // the check only where the target stores it (the latin1 table+index coverage still runs on 5.7).
                    if (TestVersionGates.SupportsCheckConstraints(Platform, serverConnectionString))
                        Assert.That(CheckConstraintExists(cmd, latin1Db, "CK_KeeperTable_IdPos"), Is.True,
                            "The table's check constraint must deploy to a latin1 database (#359).");
                });
            }
            finally
            {
                config["ScriptTokens:MainDB"] = savedMainDb;
                config["SchemaPackagePath"] = string.Empty;
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{latin1Db}`;";
                    cmd.ExecuteNonQuery();
                }
                catch { /* best-effort cleanup */ }
                conn.Close();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
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
        FactoryContainer.Register(FixtureConfig);
        FactoryContainer.Register(_environment);
        LogFactory.Register("ErrorLog", _errorLog);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);

    private void CopyFixtureTo(string dest)
    {
        var src = TestHelper.GetTestProductPath(ProductPlatformFolder, "StickyPreventDrop");
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

    private static bool TableExists(System.Data.IDbCommand cmd, string db, string tableName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = '{tableName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool IndexExists(System.Data.IDbCommand cmd, string db, string tableName, string indexName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{db}' AND TABLE_NAME = '{tableName}' AND INDEX_NAME = '{indexName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool CheckConstraintExists(System.Data.IDbCommand cmd, string db, string checkName)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{db}' AND CONSTRAINT_NAME = '{checkName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    // Every catalog name in the target database passes through the name-key functions, including tables the package
    // does not manage. Their parameters must hold any identifier, whatever the database's default character set: a
    // latin1 parameter rejects a non-Latin name in strict mode (aborting the deploy) and turns it into '??' otherwise,
    // where two different names then compare equal.
    [Test]
    public void Deploy_ToLatin1DatabaseHoldingANonLatinTableName_Succeeds()
    {
        var latin1Db = "TestLatin1Names_" + Guid.NewGuid().ToString("N")[..12];
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";
        const string json = """
            [{ "Name": "AsciiTable", "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }]
            """;

        lock (FactoryContainer.SharedLockObject)
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                cmd.CommandText = $"CREATE DATABASE `{latin1Db}` CHARACTER SET latin1 COLLATE latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(latin1Db);
                ForgeKindler.KindleTheForge(cmd, Platform);
                cmd.CommandText = "CREATE TABLE `名前表` (`住所` INT)";
                cmd.ExecuteNonQuery();

                cmd.CommandText = $"CALL SchemaSmith_TableQuench('Latin1Names', '{latin1Db}', '{json}', 0, 0, 0)";
                Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), "an unmanaged non-Latin table name must not abort the deploy");
                Assert.That(TableExists(cmd, latin1Db, "AsciiTable"), Is.True, "the declared table must deploy");
            }
            finally
            {
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{latin1Db}`;";
                    cmd.ExecuteNonQuery();
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    // Names the package declares travel through the routines' parameters and local variables; on a latin1 database
    // those took the database's character set and the server rejected any non-Latin name before deploying anything.
    [Test]
    public void Deploy_ToLatin1Database_NonLatinDeclaredNames_DeployAsSpelled_AndRedeployCleanly()
    {
        var latin1Db = "TestLatin1Decl_" + Guid.NewGuid().ToString("N")[..12];
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";
        const string json = """
            [{ "Name": "親表", "Columns": [ { "Name": "番号", "DataType": "INT", "Nullable": false } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "番号" } ] },
             { "Name": "名前表",
               "Columns": [ { "Name": "番号", "DataType": "INT", "Nullable": false },
                            { "Name": "親番号", "DataType": "INT", "Nullable": true },
                            { "Name": "住所", "DataType": "VARCHAR(40)", "Nullable": true } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "番号" },
                            { "Name": "索引_住所", "IndexColumns": "住所" },
                            { "Name": "索引_親", "IndexColumns": "親番号" } ],
               "ForeignKeys": [ { "Name": "外部_親", "Columns": "親番号", "RelatedTable": "親表", "RelatedColumns": "番号" } ] }]
            """;

        lock (FactoryContainer.SharedLockObject)
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                cmd.CommandText = $"CREATE DATABASE `{latin1Db}` CHARACTER SET latin1 COLLATE latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(latin1Db);
                ForgeKindler.KindleTheForge(cmd, Platform);

                for (var deploy = 1; deploy <= 2; deploy++)
                {
                    cmd.CommandText = $"CALL SchemaSmith_TableQuench('Latin1Decl', '{latin1Db}', '{json}', 0, 0, 0)";
                    var current = deploy;
                    Assert.DoesNotThrow(() => cmd.ExecuteNonQuery(), $"deploy {current}");
                    Assert.Multiple(() =>
                    {
                        Assert.That(CatalogList(cmd, $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{latin1Db}' AND TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME NOT LIKE 'SchemaSmith%'"),
                            Is.EqualTo("名前表,親表"), $"deploy {current}: tables");
                        Assert.That(CatalogList(cmd, $"SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{latin1Db}' AND TABLE_NAME = '名前表'"),
                            Is.EqualTo("住所,番号,親番号"), $"deploy {current}: columns");
                        Assert.That(CatalogList(cmd, $"SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{latin1Db}' AND TABLE_NAME = '名前表'"),
                            Is.EqualTo("PRIMARY,索引_住所,索引_親"), $"deploy {current}: indexes");
                        Assert.That(CatalogList(cmd, $"SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{latin1Db}' AND TABLE_NAME = '名前表' AND CONSTRAINT_TYPE = 'FOREIGN KEY'"),
                            Is.EqualTo("外部_親"), $"deploy {current}: foreign key");
                    });
                }
            }
            finally
            {
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{latin1Db}`;";
                    cmd.ExecuteNonQuery();
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private static string CatalogList(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var names = new System.Collections.Generic.List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                names.Add(reader.GetString(0));
        names.Sort(StringComparer.Ordinal);
        return string.Join(",", names);
    }
}
