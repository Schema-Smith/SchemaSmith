// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data.Common;
using System.Data;
using System.IO;
using System;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;
using log4net;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A SQL Server column whose name contains a <c>]</c> must deploy, REDEPLOY, and never be dropped.
/// <para>Two reported defects sat behind one shape — a raw <c>'[' + catalog_name + ']'</c> where correct
/// escaping belongs, so a catalog name <c>a]b</c> rendered as <c>[a]b]</c> and was compared against the
/// declared, correctly-escaped <c>[a]]b]</c>. The comparison could never match:</para>
/// <list type="number">
/// <item><c>#ExistingColumns</c> never saw the column, so <c>NewColumn</c> stayed 1 for ever and the SECOND
/// deploy of an unchanged package failed with "Column names in each table must be unique". The first deploy
/// succeeding is what made it hard to notice.</item>
/// <item>"Detect Column Drops" elected the column for DROP even though the product declares it — and the
/// malformed identifier was the ONLY thing preventing the data loss, because the emitted
/// <c>ALTER TABLE … DROP COLUMN [a]b]</c> could not parse. Fixing the rendering alone would have ENABLED the
/// drop, which is why the comparison is what had to be fixed.</item>
/// </list>
/// <para>So this test deploys TWICE and asserts the column survives. A single-deploy test passes over both
/// defects, and a test that only checked exit codes would pass over the second one entirely.</para>
/// </summary>
[Category("SqlServer")]
[TestFixture]
public class BracketedColumnNameRedeployTests
{
    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    private const string TableName = "BracketColumnProbe";
    private const string RawColumnName = "a]b";          // what the catalog stores
    private const string DeclaredColumnName = "[a]]b]";  // its correct delimited form
    private const string RawIndexName = "IX]Probe";      // what the catalog stores
    private const string DeclaredIndexName = "[IX]]Probe]";
    private const string ParentTable = "BracketParentProbe";
    private const string RawParentCol = "c]d";
    private const string DeclaredParentCol = "[c]]d]";
    private const string RawFkName = "FK]Probe";
    private const string DeclaredFkName = "[FK]]Probe]";
    private const string RawCheckName = "CK]Probe";
    private const string DeclaredCheckName = "[CK]]Probe]";
    private const string RawStatName = "ST]Probe";
    private const string DeclaredStatName = "[ST]]Probe]";

    [Test]
    public void ABracketedColumnDeploysRedeploysAndIsNeverDropped()
    {
        var db = "TestBracketCol_" + Guid.NewGuid().ToString("N")[..12];
        var tempDir = Path.Join(Path.GetTempPath(), $"BracketCol_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            WritePackage(tempDir, db);

            // Config comes from the container, the way every other SQL Server test in this project builds
            // its connection -- there is no fixture accessor for it here.
            var cfg = FactoryContainer.Resolve<IConfigurationRoot>();
            var connProps = ConnectionString.ReadProperties(cfg, "Target:ConnectionProperties");
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
                ConnectionString.Build(Platform.SqlServer, cfg["Target:Server"], "master",
                    cfg["Target:User"], cfg["Target:Password"], cfg["Target:Port"], connProps));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            var config = cfg;
            var savedPath = config["SchemaPackagePath"];

            try
            {
                cmd.CommandText = $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];";
                cmd.ExecuteNonQuery();
                conn.ChangeDatabase(db);
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

                config["SchemaPackagePath"] = tempDir;

                for (var deploy = 1; deploy <= 2; deploy++)
                {
                    _environment.ClearReceivedCalls();
                    RunSchemaQuench();

                    // Deploy 2 is the one that used to fail: NewColumn never cleared, so the add was
                    // re-attempted against a column already there.
                    _environment.DidNotReceive().Exit(2);
                    _environment.DidNotReceive().Exit(3);

                    Assert.That(ColumnExists(cmd, db), Is.True,
                        $"after deploy {deploy} the column '{RawColumnName}' must exist. If it is missing, "
                        + "Detect Column Drops elected a column the product declares -- the data-loss half, "
                        + "which was latent only while the malformed DROP statement could not parse.");

                    Assert.That(IndexExists(cmd, RawIndexName), Is.True,
                        $"after deploy {deploy} the index '{RawIndexName}' must exist. Index ownership is "
                        + "matched against the BARE catalog name from fn_listextendedproperty, so a raw wrap "
                        + "renders [IX]Probe] and can never equal the declared [IX]]Probe] -- the index then "
                        + "lands in #IndexesRemovedFromProduct and is dropped although the package declares "
                        + "it. Same data-loss shape as the column, a different object.");

                    // The FOREIGN KEY, CHECK CONSTRAINT and STATISTIC each carry a ] too. They are
                    // separate emission and ownership paths from the index, and until now NOTHING
                    // anywhere deployed a delimiter-bearing object of any of these kinds -- which is why
                    // the index defect went unnoticed for so long. Each is asserted by NAME after every
                    // deploy, so a drop-and-recreate or an ownership mismatch shows up here.
                    Assert.That(ObjectExists(cmd, "sys.foreign_keys", RawFkName), Is.True,
                        $"after deploy {deploy} the foreign key '{RawFkName}' must exist");
                    Assert.That(ObjectExists(cmd, "sys.check_constraints", RawCheckName), Is.True,
                        $"after deploy {deploy} the check constraint '{RawCheckName}' must exist");
                    Assert.That(ObjectExists(cmd, "sys.stats", RawStatName), Is.True,
                        $"after deploy {deploy} the statistic '{RawStatName}' must exist");
                    // The FK's TARGET column also carries a ], so the related-column rendering is covered
                    // rather than only the FK name. A parent column that went missing would leave the FK
                    // pointing at nothing, which is a different failure from the FK itself being dropped.
                    Assert.That(ColumnExistsOn(cmd, ParentTable, RawParentCol), Is.True,
                        $"after deploy {deploy} the parent column '{RawParentCol}' must exist");

                    // A ROW goes in after the first deploy, and its value is read back after the second.
                    // sys.columns CANNOT tell a surviving column from a dropped-and-recreated one -- both
                    // leave a column of the right name and type behind -- so presence alone would pass over
                    // exactly the data loss this guards. The value is the only thing that distinguishes
                    // them, and with the comparison fixed the DROP now PARSES, so the channel is live.
                    if (deploy == 1) InsertProbeRow(cmd);
                    else
                        Assert.That(ProbeValue(cmd), Is.EqualTo(ProbeRowValue),
                            "the column's ROWS must survive the redeploy. A recreated column would satisfy "
                            + "sys.columns and return NULL here, which is the data loss wearing a passing "
                            + "test as a disguise.");
                }
            }
            finally
            {
                config["SchemaPackagePath"] = savedPath;
                try
                {
                    conn.ChangeDatabase("master");
                    cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN "
                                      + $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                                      + $"DROP DATABASE [{db}]; END";
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

    private static bool ColumnExistsOn(IDbCommand cmd, string table, string column)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = "
                          + "OBJECT_ID('dbo." + table + "') AND [name] = @n";
        cmd.Parameters.Clear();
        var prm = cmd.CreateParameter();
        prm.ParameterName = "@n";
        prm.Value = column;
        cmd.Parameters.Add(prm);
        var found = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        cmd.Parameters.Clear();
        return found;
    }

    private static bool ObjectExists(IDbCommand cmd, string catalogView, string name)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM " + catalogView + " WHERE [name] = @n";
        cmd.Parameters.Clear();
        var prm = cmd.CreateParameter();
        prm.ParameterName = "@n";
        prm.Value = name;
        cmd.Parameters.Add(prm);
        var found = Convert.ToInt32(cmd.ExecuteScalar()) >= 1;
        cmd.Parameters.Clear();
        return found;
    }

    private static bool IndexExists(IDbCommand cmd, string indexName)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = "
                          + "OBJECT_ID('dbo." + TableName + "') AND [name] = @n";
        cmd.Parameters.Clear();
        var prm = cmd.CreateParameter();
        prm.ParameterName = "@n";
        prm.Value = indexName;
        cmd.Parameters.Add(prm);
        var found = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        cmd.Parameters.Clear();
        return found;
    }

    private const string ProbeRowValue = "keep-me";

    private static void InsertProbeRow(IDbCommand cmd)
    {
        // The parent row comes FIRST. The probe column is now the child side of a ]-bearing foreign key, so
        // without it the insert is refused -- which is itself evidence the FK was created correctly, but it
        // is not what this test is measuring.
        cmd.CommandText = $"IF NOT EXISTS (SELECT 1 FROM dbo.[{ParentTable}]) "
                          + $"INSERT INTO dbo.[{ParentTable}] ([{DeclaredParentCol.Trim('[', ']').Replace("]]", "]").Replace("]", "]]")}]) "
                          + "VALUES (@v)";
        cmd.Parameters.Clear();
        var pp = cmd.CreateParameter();
        pp.ParameterName = "@v";
        pp.Value = ProbeRowValue;
        cmd.Parameters.Add(pp);
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();

        cmd.CommandText = $"INSERT INTO dbo.{TableName} ([Id], [{RawColumnName.Replace("]", "]]")}]) "
                          + "VALUES (1, @v)";
        var prm = cmd.CreateParameter();
        prm.ParameterName = "@v";
        prm.Value = ProbeRowValue;
        cmd.Parameters.Add(prm);
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
    }

    private static string ProbeValue(IDbCommand cmd)
    {
        cmd.CommandText = $"SELECT [{RawColumnName.Replace("]", "]]")}] FROM dbo.{TableName} WHERE [Id] = 1";
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    private static bool ColumnExists(IDbCommand cmd, string db)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sys.columns c "
                          + $"WHERE c.[object_id] = OBJECT_ID('dbo.{TableName}') AND c.[name] = @n";
        cmd.Parameters.Clear();
        var p = cmd.CreateParameter();
        p.ParameterName = "@n";
        p.Value = RawColumnName;
        cmd.Parameters.Add(p);
        var found = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        cmd.Parameters.Clear();
        return found;
    }

    private static void WritePackage(string dir, string db)
    {
        var tables = Path.Join(dir, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);

        File.WriteAllText(Path.Join(dir, "Product.json"),
            "{\n  \"Name\": \"BracketColumnProbe\",\n  \"ValidationScript\": \"SELECT CAST(1 AS BIT)\",\n"
            + "  \"TemplateOrder\": [\"Main\"],\n  \"ScriptTokens\": {},\n  \"ScriptFolders\": [],\n"
            + "  \"Platform\": \"SqlServer\"\n}\n");

        File.WriteAllText(Path.Join(dir, "Templates", "Main", "Template.json"),
            "{\n  \"Name\": \"Main\",\n  \"DatabaseIdentificationScript\": "
            + $"\"SELECT [name] FROM sys.databases WHERE [name] = '{db}'\",\n"
            + "  \"ScriptFolders\": []\n}\n");

        // The declared names carry the escape, which is what extraction now writes. TWO defects are
        // covered by one package: a ]-bearing COLUMN and a ]-bearing INDEX. They fail differently --
        // the column through #ExistingColumns and Detect Column Drops, the index through ownership
        // matching against fn_listextendedproperty's BARE name, which puts a declared index into
        // #IndexesRemovedFromProduct (@DropIndexesRemovedFromProduct defaults to 1).
        // The PARENT table, so the foreign key has a ]-bearing target to point at. Its referenced column
        // carries a ] as well, which exercises the RelatedColumns rendering rather than only the FK name.
        File.WriteAllText(Path.Join(tables, $"dbo.{ParentTable}.json"),
            "{\n  \"Schema\": \"[dbo]\",\n"
            + $"  \"Name\": \"[{ParentTable}]\",\n  \"Columns\": [\n"
            + $"    {{ \"Name\": \"{DeclaredParentCol}\", \"DataType\": \"VARCHAR(40)\", \"Nullable\": false }}\n"
            + "  ],\n  \"Indexes\": [\n"
            + $"    {{ \"Name\": \"[PK_{ParentTable}]\", \"PrimaryKey\": true, \"Unique\": true, "
            + $"\"UniqueConstraint\": true, \"IndexColumns\": \"{DeclaredParentCol}\" }}\n  ]\n}}\n");

        // ONE package carrying a ] on every object kind whose emission or ownership path the sweep touched:
        // a COLUMN, an INDEX, a FOREIGN KEY (name and related column), a CHECK CONSTRAINT and a STATISTIC.
        // Before this, nothing anywhere deployed a delimiter-bearing object of the last three kinds, which
        // is precisely why the index defect survived unnoticed -- the guard did not exist to catch it.
        File.WriteAllText(Path.Join(tables, $"dbo.{TableName}.json"),
            "{\n  \"Schema\": \"[dbo]\",\n"
            + $"  \"Name\": \"[{TableName}]\",\n  \"Columns\": [\n"
            + "    { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },\n"
            + $"    {{ \"Name\": \"{DeclaredColumnName}\", \"DataType\": \"VARCHAR(40)\", \"Nullable\": true }}\n"
            + "  ],\n  \"Indexes\": [\n"
            + $"    {{ \"Name\": \"[PK_{TableName}]\", \"PrimaryKey\": true, \"Unique\": true, "
            + "\"UniqueConstraint\": true, \"IndexColumns\": \"[Id]\" },\n"
            + $"    {{ \"Name\": \"{DeclaredIndexName}\", \"IndexColumns\": \"{DeclaredColumnName}\" }}\n"
            + "  ],\n  \"ForeignKeys\": [\n"
            + $"    {{ \"Name\": \"{DeclaredFkName}\", \"Columns\": \"{DeclaredColumnName}\", "
            + $"\"RelatedTable\": \"[{ParentTable}]\", \"RelatedColumns\": \"{DeclaredParentCol}\" }}\n"
            + "  ],\n  \"CheckConstraints\": [\n"
            + $"    {{ \"Name\": \"{DeclaredCheckName}\", \"Expression\": \"[Id] >= 0\" }}\n"
            + "  ],\n  \"Statistics\": [\n"
            + $"    {{ \"Name\": \"{DeclaredStatName}\", \"Columns\": \"{DeclaredColumnName}\" }}\n  ]\n}}\n");
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

    private static void RunSchemaQuench() => Program.Main(["SkipKindlingForge"]);
}
