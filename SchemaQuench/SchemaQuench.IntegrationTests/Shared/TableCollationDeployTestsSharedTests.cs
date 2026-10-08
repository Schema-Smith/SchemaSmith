// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.IO;
using System;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using log4net;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// A table's declared CharacterSet/Collation, and what happens to a column that overrides it.
/// <para>Found by deploying <c>Demos/MariaDB/Sakila</c> and <c>Demos/MySQL/Sakila</c> — the first thing ever
/// to deploy the shipped demo packages. Two defects, and the first is what makes the second fire:</para>
/// <list type="number">
/// <item><c>CREATE TABLE</c> emitted the whole table-option list (ENGINE, ROW_FORMAT, COMPRESSION,
/// ENCRYPTION, AUTO_INCREMENT, COMMENT, TABLESPACE, DATA DIRECTORY) and no character set or collation at
/// all, so a table declaring <c>utf8mb3_general_ci</c> was created at the SERVER default. Every string
/// column that relies on the table default therefore got the wrong collation, silently — and collation
/// decides case sensitivity, sort order, and so comparison and uniqueness.</item>
/// <item>The follow-up "change table collation" pass then emitted
/// <c>ALTER TABLE … CONVERT TO CHARACTER SET</c>, which rewrites EVERY character column in the table (and
/// re-encodes its data), wiping the per-column collation a package explicitly declared. Measured: deploy 1
/// created the override correctly, deploy 2 converted it away, deploy 3 put it back.</item>
/// </list>
/// <para>THE DATABASE DEFAULT MUST DIFFER FROM THE DECLARED COLLATION or this test passes vacuously — on a
/// server whose default already matches, a CREATE TABLE that emits nothing looks correct. latin1 is used
/// because it is MariaDB's stock compiled default and cannot be confused with either utf8mb3 value.</para>
/// <para>Asserted over THREE consecutive deploys, because two of them looked fine: the wipe landed on the
/// second and the repair on the third, so a one-deploy or two-deploy assertion misses it in both
/// directions.</para>
/// </summary>
public abstract class TableCollationDeployTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    private const string DeclaredTableCollation = "utf8mb3_general_ci";
    private const string DeclaredColumnCollation = "utf8mb3_bin";

    [Test]
    public void ADeclaredTableCollationIsCreated_AndAColumnOverrideSurvivesEveryRedeploy()
    {
        var db = "TestColl_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"TableCollation_{Guid.NewGuid():N}");
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db, "CollationProbe", $$"""
                {
                  "Name": "`CollationProbe`",
                  "Engine": "InnoDB",
                  "Collation": "{{DeclaredTableCollation}}",
                  "Columns": [
                    { "Name": "`Id`", "DataType": "int", "Nullable": false },
                    { "Name": "`Inherited`", "DataType": "varchar(40)", "Nullable": true },
                    { "Name": "`BinOverride`", "DataType": "varchar(40)", "Nullable": true,
                      "CharacterSet": "utf8mb3", "Collation": "{{DeclaredColumnCollation}}" }
                  ]
                }
                """);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET latin1 COLLATE latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform);

                config["SchemaPackagePath"] = tempDir;

                for (var deploy = 1; deploy <= 3; deploy++)
                {
                    _environment.ClearReceivedCalls();
                    RunSchemaQuench();
                    _environment.DidNotReceive().Exit(2);
                    _environment.DidNotReceive().Exit(3);

                    var tableCollation = ModernCollationName(ScalarOrNull(cmd,
                        $"SELECT TABLE_COLLATION FROM information_schema.TABLES WHERE TABLE_SCHEMA='{db}' AND TABLE_NAME='CollationProbe'"));
                    var overrideCollation = ModernCollationName(ScalarOrNull(cmd, ColumnCollationSql(db, "BinOverride")));
                    var inheritedCollation = ModernCollationName(ScalarOrNull(cmd, ColumnCollationSql(db, "Inherited")));

                    Assert.Multiple(() =>
                    {
                        Assert.That(tableCollation, Is.EqualTo(DeclaredTableCollation),
                            $"deploy {deploy}: the table must carry its DECLARED collation, not the server default "
                            + "-- CREATE TABLE emitted no character set or collation at all.");
                        Assert.That(overrideCollation, Is.EqualTo(DeclaredColumnCollation),
                            $"deploy {deploy}: a column declaring its own collation must keep it. CONVERT TO "
                            + "CHARACTER SET rewrites every character column, so this was wiped to the table's "
                            + "collation on the second deploy and restored on the third.");
                        Assert.That(inheritedCollation, Is.EqualTo(DeclaredTableCollation),
                            $"deploy {deploy}: a column declaring no collation must inherit the declared table "
                            + "collation, not the server default.");
                    });
                }
            }
            finally
            {
                config["SchemaPackagePath"] = string.Empty;
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`;";
                    cmd.ExecuteNonQuery();
                }
                // Typed rather than bare: a teardown drop can legitimately fail if the engine refuses it
                // (DbException) or the connection is no longer usable (InvalidOperationException). Anything
                // else here is a real fault and should not be swallowed by a passing test's cleanup.
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // A column declaring the table's character set and no collation -- the form extraction writes for a column in
    // the table's collation -- must get the table's collation, not the character set's default. utf8mb4_unicode_ci is
    // not utf8mb4's default on any supported version, so the CHARACTER SET-only form lands elsewhere on every one.
    // Damaged starts in another collation, as an earlier deploy of this package left it; Fresh is created.
    [Test]
    public void ACharsetOnlyColumn_GetsTheTablesCollation_NotTheCharsetDefault()
    {
        const string tableCollation = "utf8mb4_unicode_ci";
        var db = "TestCsOnly_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"CharsetOnly_{Guid.NewGuid():N}");
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db, "CharsetProbe", $$"""
                {
                  "Name": "`CharsetProbe`",
                  "Engine": "InnoDB",
                  "Collation": "{{tableCollation}}",
                  "Columns": [
                    { "Name": "`Id`", "DataType": "int", "Nullable": false },
                    { "Name": "`Damaged`", "DataType": "varchar(40)", "Nullable": true, "CharacterSet": "utf8mb4" },
                    { "Name": "`Fresh`", "DataType": "varchar(40)", "Nullable": true, "CharacterSet": "utf8mb4" }
                  ]
                }
                """);

            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(serverConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            var config = FactoryContainer.Resolve<Microsoft.Extensions.Configuration.IConfigurationRoot>();

            try
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET latin1 COLLATE latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform);
                cmd.CommandText = $"CREATE TABLE `CharsetProbe` (`Id` int NOT NULL, `Damaged` varchar(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL) DEFAULT CHARSET=utf8mb4 COLLATE={tableCollation};";
                cmd.ExecuteNonQuery();

                config["SchemaPackagePath"] = tempDir;

                for (var deploy = 1; deploy <= 2; deploy++)
                {
                    _environment.ClearReceivedCalls();
                    RunSchemaQuench();
                    _environment.DidNotReceive().Exit(2);
                    _environment.DidNotReceive().Exit(3);

                    var damaged = ScalarOrNull(cmd, ColumnCollationSql(db, "Damaged", "CharsetProbe"));
                    var fresh = ScalarOrNull(cmd, ColumnCollationSql(db, "Fresh", "CharsetProbe"));
                    Assert.Multiple(() =>
                    {
                        Assert.That(fresh, Is.EqualTo(tableCollation),
                            $"deploy {deploy}: a created column declaring only the table's character set must take the table's collation.");
                        Assert.That(damaged, Is.EqualTo(tableCollation),
                            $"deploy {deploy}: an existing column in another collation must be brought to the table's.");
                    });
                }
            }
            finally
            {
                config["SchemaPackagePath"] = string.Empty;
                try
                {
                    conn.ChangeDatabase("information_schema");
                    cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`;";
                    cmd.ExecuteNonQuery();
                }
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
            }
        }
    }

    // MySQL before 8.0.30 and MariaDB before 10.6 name utf8mb3 collations by their old alias (utf8_general_ci);
    // same collation, older spelling, so fold it rather than pin the modern engines' rendering.
    private static string ModernCollationName(string name) =>
        name != null && name.StartsWith("utf8_", StringComparison.OrdinalIgnoreCase) ? "utf8mb3_" + name[5..] : name;

    private static string ColumnCollationSql(string db, string column, string table = "CollationProbe") =>
        $"SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='{db}' "
        + $"AND TABLE_NAME='{table}' AND COLUMN_NAME='{column}'";

    private static string ScalarOrNull(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : value.ToString();
    }

    /// <summary>
    /// The package is written here rather than committed as a fixture: a committed TestProduct would need
    /// generated <c>.json-schemas</c> kept in step with the model (DoD #9), and each table exists only to
    /// carry a few collation states.
    /// </summary>
    private void WritePackage(string dir, string db, string tableName, string tableJson)
    {
        var platform = Platform == Platform.MariaDb ? "MariaDb" : "MySQL";
        Directory.CreateDirectory(Path.Join(dir, "Templates", "Main", "Tables"));

        File.WriteAllText(Path.Join(dir, "Product.json"), $$"""
            {
              "Name": "CollationProbe",
              "ValidationScript": "SELECT 1",
              "TemplateOrder": ["Main"],
              "ScriptTokens": {},
              "ScriptFolders": [],
              "Platform": "{{platform}}"
            }
            """);

        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"), $$"""
            {
              "Name": "Main",
              "DatabaseIdentificationScript": "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{{db}}'",
              "ScriptFolders": []
            }
            """);

        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Tables", $"{tableName}.json"), tableJson);
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
}
