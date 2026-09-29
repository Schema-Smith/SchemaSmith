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
/// An ADOPTED table — one created outside SchemaSmith whose live collation differs from the package's — must
/// have its table DEFAULT corrected without its columns or its foreign keys being rewritten.
/// <para>The pass used to emit <c>ALTER TABLE … CONVERT TO CHARACTER SET</c>, which rewrites every character
/// column on the table and re-encodes its data. That destroyed declared per-column collations; and because
/// the engine refuses the conversion while a foreign key references any of those columns ("Cannot change
/// column … used in a foreign key constraint") it also needed a whole FK teardown-and-restore apparatus
/// wrapped around it. <c>DEFAULT CHARACTER SET</c> changes only the table's default for future columns,
/// which is what the model already means: SchemaSmith manages a column's collation exactly where the package
/// declares one, so converting the others changes something nobody asked about (Paul, 2026-09-26).</para>
/// <para>THE FOREIGN KEY IS THE POINT OF THIS TEST, not scenery. It sits on a character column — the shape
/// that made the conversion illegal — so its survival is the evidence that the teardown is no longer needed,
/// and that an adopted table's foreign keys are no longer dropped and rebuilt for what is only a change to
/// the table's default.</para>
/// <para>The trade-off this pins, deliberately: an adopted table's existing columns KEEP their old
/// collation, so a table SchemaSmith created and a table it adopted can differ. That is the accepted cost of
/// not re-encoding data behind the author's back.</para>
/// </summary>
public abstract class AdoptedTableCollationDeployTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string BaseConnectionString { get; }
    protected abstract Microsoft.Extensions.Configuration.IConfigurationRoot FixtureConfig { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    private const string DeclaredTableCollation = "utf8mb3_general_ci";
    private const string DeclaredColumnCollation = "utf8mb3_bin";
    private const string AdoptedCollation = "latin1_swedish_ci";

    [Test]
    public void AnAdoptedTable_GetsItsDefaultCorrected_WithoutRewritingColumnsOrDroppingForeignKeys()
    {
        var db = "TestAdopt_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"AdoptedCollation_{Guid.NewGuid():N}");
        var serverConnectionString = BaseConnectionString + "Database=information_schema;";

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db);

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

                // Created OUTSIDE SchemaSmith, at latin1, with a foreign key on a character column.
                cmd.CommandText = "CREATE TABLE `AdoptedParent` (`Code` varchar(20) NOT NULL PRIMARY KEY) "
                                  + "ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "CREATE TABLE `AdoptedProbe` ("
                                  + "`Id` int NOT NULL PRIMARY KEY, "
                                  + "`Inherited` varchar(40) NULL, "
                                  + "`BinOverride` varchar(40) NULL, "
                                  + "`ParentCode` varchar(20) NULL, "
                                  + "KEY `IX_AdoptedProbe_ParentCode` (`ParentCode`), "
                                  + "CONSTRAINT `FK_AdoptedProbe_Parent` FOREIGN KEY (`ParentCode`) "
                                  + "REFERENCES `AdoptedParent` (`Code`)"
                                  + ") ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;";
                cmd.ExecuteNonQuery();
                ForgeKindler.KindleTheForge(cmd, Platform);

                config["SchemaPackagePath"] = tempDir;

                for (var deploy = 1; deploy <= 2; deploy++)
                {
                    _environment.ClearReceivedCalls();
                    RunSchemaQuench();
                    _environment.DidNotReceive().Exit(2);
                    _environment.DidNotReceive().Exit(3);

                    Assert.Multiple(() =>
                    {
                        Assert.That(TableCollation(cmd, db), Is.EqualTo(DeclaredTableCollation),
                            $"deploy {deploy}: the adopted table's DEFAULT must be corrected to the declared collation");
                        Assert.That(ColumnCollation(cmd, db, "BinOverride"), Is.EqualTo(DeclaredColumnCollation),
                            $"deploy {deploy}: a column the package declares a collation for must get it");
                        Assert.That(ColumnCollation(cmd, db, "Inherited"), Is.EqualTo(AdoptedCollation),
                            $"deploy {deploy}: a column the package declares NO collation for must be left alone. "
                            + "CONVERT TO CHARACTER SET rewrote it, and re-encoded its data, for a change that "
                            + "was only ever about the table's default.");
                        Assert.That(ColumnCollation(cmd, db, "ParentCode"), Is.EqualTo(AdoptedCollation),
                            $"deploy {deploy}: likewise the foreign key's own column, whose collation has to keep "
                            + "matching the referenced column or the constraint cannot exist at all");
                        Assert.That(ForeignKeyExists(cmd, db), Is.True,
                            $"deploy {deploy}: the foreign key must SURVIVE. It was dropped and rebuilt only "
                            + "because CONVERT TO CHARACTER SET could not run while it existed; a table-default "
                            + "change does not touch the column, so there is nothing to tear down.");
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
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (IOException) { /* a held log handle must not fail a passing test */ }
            }
        }
    }

    private static string TableCollation(System.Data.IDbCommand cmd, string db) =>
        ModernCollationName(ScalarOrNull(cmd, "SELECT TABLE_COLLATION FROM information_schema.TABLES "
                          + $"WHERE TABLE_SCHEMA='{db}' AND TABLE_NAME='AdoptedProbe'"));

    private static string ColumnCollation(System.Data.IDbCommand cmd, string db, string column) =>
        ModernCollationName(ScalarOrNull(cmd, "SELECT COLLATION_NAME FROM information_schema.COLUMNS "
                          + $"WHERE TABLE_SCHEMA='{db}' AND TABLE_NAME='AdoptedProbe' AND COLUMN_NAME='{column}'"));

    // MySQL before 8.0.30 and MariaDB before 10.6 name utf8mb3 collations by their old alias (utf8_general_ci);
    // same collation, older spelling, so fold it rather than pin the modern engines' rendering.
    private static string ModernCollationName(string name) =>
        name != null && name.StartsWith("utf8_", StringComparison.OrdinalIgnoreCase) ? "utf8mb3_" + name[5..] : name;

    private static bool ForeignKeyExists(System.Data.IDbCommand cmd, string db)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS "
                          + $"WHERE CONSTRAINT_SCHEMA='{db}' AND TABLE_NAME='AdoptedProbe' "
                          + "AND CONSTRAINT_NAME='FK_AdoptedProbe_Parent' AND CONSTRAINT_TYPE='FOREIGN KEY'";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static string ScalarOrNull(System.Data.IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : value.ToString();
    }

    private void WritePackage(string dir, string db)
    {
        var platform = Platform == Platform.MariaDb ? "MariaDb" : "MySQL";
        var tables = Path.Join(dir, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);

        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{\n  \"Name\": \"AdoptedCollation\",\n  \"ValidationScript\": \"SELECT 1\",\n"
            + "  \"TemplateOrder\": [\"Main\"],\n  \"ScriptTokens\": {},\n  \"ScriptFolders\": [],\n"
            + $"  \"Platform\": \"{platform}\"\n}}\n");

        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"),
            "{\n  \"Name\": \"Main\",\n  \"DatabaseIdentificationScript\": "
            + $"\"SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{db}'\",\n"
            + "  \"ScriptFolders\": []\n}\n");

        // The parent declares no collation, so nothing about it is managed and its Code column keeps the
        // latin1 collation the foreign key's own column has to match.
        File.WriteAllText(Path.Join(tables, "AdoptedParent.json"),
            "{\n  \"Name\": \"`AdoptedParent`\",\n  \"Engine\": \"InnoDB\",\n  \"Columns\": [\n"
            + "    { \"Name\": \"`Code`\", \"DataType\": \"varchar(20)\", \"Nullable\": false }\n  ],\n"
            + "  \"Indexes\": [\n    { \"Name\": \"PRIMARY\", \"PrimaryKey\": true, \"Unique\": true, "
            + "\"UniqueConstraint\": true, \"IndexColumns\": \"`Code`\" }\n  ]\n}\n");

        File.WriteAllText(Path.Join(tables, "AdoptedProbe.json"),
            "{\n  \"Name\": \"`AdoptedProbe`\",\n  \"Engine\": \"InnoDB\",\n"
            + $"  \"Collation\": \"{DeclaredTableCollation}\",\n  \"Columns\": [\n"
            + "    { \"Name\": \"`Id`\", \"DataType\": \"int\", \"Nullable\": false },\n"
            + "    { \"Name\": \"`Inherited`\", \"DataType\": \"varchar(40)\", \"Nullable\": true },\n"
            + "    { \"Name\": \"`BinOverride`\", \"DataType\": \"varchar(40)\", \"Nullable\": true, "
            + $"\"CharacterSet\": \"utf8mb3\", \"Collation\": \"{DeclaredColumnCollation}\" }},\n"
            + "    { \"Name\": \"`ParentCode`\", \"DataType\": \"varchar(20)\", \"Nullable\": true }\n  ],\n"
            + "  \"Indexes\": [\n    { \"Name\": \"PRIMARY\", \"PrimaryKey\": true, \"Unique\": true, "
            + "\"UniqueConstraint\": true, \"IndexColumns\": \"`Id`\" },\n"
            + "    { \"Name\": \"IX_AdoptedProbe_ParentCode\", \"IndexColumns\": \"`ParentCode`\" }\n  ],\n"
            + "  \"ForeignKeys\": [\n    { \"Name\": \"FK_AdoptedProbe_Parent\", \"Columns\": \"`ParentCode`\", "
            + "\"RelatedTable\": \"`AdoptedParent`\", \"RelatedColumns\": \"`Code`\" }\n  ]\n}\n");
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
