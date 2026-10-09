// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// WhatIf must report what the real run then does. Deploys v1 of a package, previews v2 with <c>--WhatIf</c>, then
/// deploys v2 for real, and requires the two deployment summaries to list the same changes.
/// <para>Compares the summaries rather than the procedures because the gaps live in the orchestration: a step the
/// WhatIf path never calls (foreign keys were the first found) has a perfectly good WhatIf branch in its procedure,
/// so a procedure-level test passes over it.</para>
/// </summary>
public abstract class WhatIfParityTestsSharedTests
{
    private const string ProductName = "WhatIfParity";
    private static readonly string[] TablesChildFirst = ["WhatIfChild", "WhatIfNew", "WhatIfGone", "WhatIfParent"];

    protected abstract Platform Platform { get; }
    protected abstract IConfigurationRoot FixtureConfig { get; }

    private readonly ILog _errorLog = Substitute.For<ILog>();
    private readonly ILog _progressLog = Substitute.For<ILog>();
    private readonly IEnvironment _environment = Substitute.For<IEnvironment>();

    [Test]
    public void WhatIf_ReportsTheChangesTheRealRunMakes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"WhatIfParity_{Guid.NewGuid():N}");

        lock (FactoryContainer.SharedLockObject)
        {
            SetupSharedMocks();
            var config = FixtureConfig;
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainDbConnectionString(config));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;

            try
            {
                Cleanup(cmd);
                config["SchemaPackagePath"] = tempDir;

                WritePackage(tempDir, version: 1);
                RunSchemaQuench(LogDirFor(tempDir, "v1"), whatIf: false);
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                WritePackage(tempDir, version: 2);
                var whatIfLogs = LogDirFor(tempDir, "whatif");
                RunSchemaQuench(whatIfLogs, whatIf: true);
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);
                Assert.That(TableExists(cmd, "WhatIfNew"), Is.False, "WhatIf must not create the table v2 adds");

                var realLogs = LogDirFor(tempDir, "real");
                RunSchemaQuench(realLogs, whatIf: false);
                _environment.DidNotReceive().Exit(2);
                _environment.DidNotReceive().Exit(3);

                var previewed = Changes(whatIfLogs, expectedMode: "WhatIf");
                var applied = Changes(realLogs, expectedMode: "Quench");

                // Without these the comparison could pass on two empty lists.
                Assert.That(applied, Has.Some.Matches<string>(c => c.StartsWith("foreignKey|") && c.EndsWith("|created")),
                    "the real v2 deploy must create a foreign key, or this test proves nothing about them");
                Assert.That(applied, Has.Some.Matches<string>(c => c.EndsWith("|dropped")),
                    "the real v2 deploy must drop something, or this test proves nothing about drops");

                var onlyPreviewed = Difference(previewed, applied);
                var onlyApplied = Difference(applied, previewed);
                Assert.That(onlyPreviewed.Count + onlyApplied.Count, Is.Zero,
                    "WhatIf and the real run disagree."
                    + $"{Environment.NewLine}  Applied but not previewed: {string.Join(", ", onlyApplied)}"
                    + $"{Environment.NewLine}  Previewed but not applied: {string.Join(", ", onlyPreviewed)}");
            }
            finally
            {
                config["SchemaPackagePath"] = string.Empty;
                try { Cleanup(cmd); }
                catch (DbException) { /* best-effort cleanup */ }
                catch (InvalidOperationException) { /* connection already unusable */ }
                conn.Close();
                // --LogPath points inside tempDir, so log4net must let go of its files before the delete.
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

    /// <summary>
    /// Every change the summary lists, as <c>type|name|action</c>, with a preview action mapped to the action it
    /// previews. Object-script "ran" rows are left out: WhatIf lists those scripts in its own section.
    /// </summary>
    private static List<string> Changes(string logDir, string expectedMode)
    {
        var path = Path.Join(logDir, "SchemaQuench - Summary.json");
        Assert.That(File.Exists(path), Is.True, $"no deployment summary was written to '{logDir}'");
        var summary = JObject.Parse(File.ReadAllText(path));
        Assert.That(summary.SelectToken("run.mode")?.Value<string>(), Is.EqualTo(expectedMode));
        Assert.That(summary.SelectToken("objectChanges.instrumented")?.Value<bool>(), Is.True,
            $"the {expectedMode} summary must carry the change audit");

        return ((JArray)summary.SelectToken("objectChanges.details") ?? [])
            .Select(d => (Type: (string)d["objectType"], Name: (string)d["objectName"], Action: (string)d["action"]))
            .Where(d => d.Action != "ran")
            .Select(d => $"{d.Type}|{d.Name}|{Executed(d.Action)}")
            .ToList();
    }

    private static string Executed(string action) => action switch
    {
        "wouldCreate" => "created",
        "wouldModify" => "modified",
        "wouldDrop" => "dropped",
        _ => action
    };

    // Multiset difference: a change listed twice on one side and once on the other is a disagreement.
    private static List<string> Difference(List<string> left, List<string> right)
    {
        var remaining = right.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var item in left)
        {
            if (remaining.TryGetValue(item, out var n) && n > 0) remaining[item] = n - 1;
            else result.Add(item);
        }
        return result;
    }

    private void WritePackage(string dir, int version)
    {
        var templateDir = Path.Join(dir, "Templates", "Main");
        var tablesDir = Path.Join(templateDir, "Tables");
        if (Directory.Exists(tablesDir)) Directory.Delete(tablesDir, true);
        Directory.CreateDirectory(tablesDir);

        File.WriteAllText(Path.Join(dir, "Product.json"), $$"""
            {
              "Name": "{{ProductName}}",
              "ValidationScript": "{{ValidationScript}}",
              "TemplateOrder": ["Main"],
              "ScriptFolders": [],
              "ScriptTokens": { "MainDB": "TestMain" },
              "Platform": "{{PlatformName}}",
              "DropTablesRemovedFromProduct": true,
              "DropColumnsRemovedFromProduct": true,
              "DropForeignKeysRemovedFromProduct": true,
              "DropCheckConstraintsRemovedFromProduct": true,
              "DropIndexesRemovedFromProduct": true
            }
            """);
        File.WriteAllText(Path.Join(templateDir, "Template.json"), $$"""
            {
              "Name": "Main",
              "DatabaseIdentificationScript": "{{IdentificationScript}}",
              "ScriptFolders": []
            }
            """);

        WriteTable(tablesDir, "WhatIfParent", [Column("Id", IntType, false)], [PrimaryKey("WhatIfParent")]);

        if (version == 1)
        {
            WriteTable(tablesDir, "WhatIfChild",
                [Column("Id", IntType, false), Column("ParentId", IntType, false), Column("Name", StringType(50), true)],
                [PrimaryKey("WhatIfChild"), Index("IX_WhatIfChild_Name", "Name")],
                foreignKeys: [ForeignKey("FK_WhatIfChild_Parent", "ParentId")],
                checks: [Check("CK_WhatIfChild_Ids", $"{ColumnRef("Id")} <> {ColumnRef("ParentId")}")]);
            WriteTable(tablesDir, "WhatIfGone", [Column("Id", IntType, false)], [PrimaryKey("WhatIfGone")]);
            return;
        }

        // v2: a widened column, an added column and index, the old index, FK and CHECK removed, a new table with
        // its own foreign key, and a table removed from the product.
        WriteTable(tablesDir, "WhatIfChild",
            [Column("Id", IntType, false), Column("ParentId", IntType, false), Column("Name", StringType(100), true),
             Column("Extra", IntType, true)],
            [PrimaryKey("WhatIfChild"), Index("IX_WhatIfChild_Extra", "Extra")]);
        WriteTable(tablesDir, "WhatIfNew",
            [Column("Id", IntType, false), Column("ParentId", IntType, false)],
            [PrimaryKey("WhatIfNew")],
            foreignKeys: [ForeignKey("FK_WhatIfNew_Parent", "ParentId")]);
    }

    private void WriteTable(string dir, string table, string[] columns, string[] indexes,
        string[] foreignKeys = null, string[] checks = null)
    {
        var schema = Platform switch
        {
            Platform.SqlServer => "\"Schema\": \"[dbo]\", ",
            Platform.PostgreSQL => "\"Schema\": \"public\", ",
            _ => ""
        };
        var json = $"{{ {schema}\"Name\": \"{Quote(table)}\","
                   + $" \"Columns\": [ {string.Join(", ", columns)} ],"
                   + $" \"Indexes\": [ {string.Join(", ", indexes)} ],"
                   + $" \"ForeignKeys\": [ {string.Join(", ", foreignKeys ?? [])} ],"
                   + $" \"CheckConstraints\": [ {string.Join(", ", checks ?? [])} ] }}";
        File.WriteAllText(Path.Join(dir, $"{table}.json"), json);
    }

    private string Column(string name, string type, bool nullable) =>
        $"{{ \"Name\": \"{Quote(name)}\", \"DataType\": \"{type}\", \"Nullable\": {(nullable ? "true" : "false")} }}";

    private string PrimaryKey(string table) =>
        $"{{ \"Name\": \"{Quote($"PK_{table}")}\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"{Quote("Id")}\" }}";

    private string Index(string name, string column) =>
        $"{{ \"Name\": \"{Quote(name)}\", \"PrimaryKey\": false, \"Unique\": false, \"IndexColumns\": \"{Quote(column)}\" }}";

    private string ForeignKey(string name, string column)
    {
        var relatedSchema = Platform switch
        {
            Platform.SqlServer => "\"RelatedTableSchema\": \"[dbo]\", ",
            Platform.PostgreSQL => "\"RelatedTableSchema\": \"public\", ",
            _ => ""
        };
        return $"{{ \"Name\": \"{Quote(name)}\", \"Columns\": \"{Quote(column)}\", {relatedSchema}"
               + $"\"RelatedTable\": \"{Quote("WhatIfParent")}\", \"RelatedColumns\": \"{Quote("Id")}\" }}";
    }

    private string Check(string name, string expression) =>
        $"{{ \"Name\": \"{Quote(name)}\", \"Expression\": \"{expression}\" }}";

    private string Quote(string name) => Platform switch
    {
        Platform.SqlServer => $"[{name}]",
        Platform.PostgreSQL => name,
        _ => $"`{name}`"
    };

    // A column reference inside an expression, already escaped for the JSON string it lands in.
    private string ColumnRef(string name) => Platform == Platform.PostgreSQL ? $"\\\"{name}\\\"" : Quote(name);

    private string IntType => Platform == Platform.PostgreSQL ? "INT4" : "INT";

    private string StringType(int length) => Platform == Platform.SqlServer ? $"NVARCHAR({length})" : $"VARCHAR({length})";

    private string PlatformName => Platform switch
    {
        Platform.SqlServer => "MSSQL",
        Platform.PostgreSQL => "PostgreSQL",
        Platform.MariaDb => "MariaDb",
        _ => "MySQL"
    };

    private string ValidationScript => Platform switch
    {
        Platform.SqlServer => "SELECT CAST(1 AS BIT)",
        Platform.PostgreSQL => "SELECT CAST(1 AS BIT)",
        _ => "SELECT 1"
    };

    private string IdentificationScript => Platform switch
    {
        Platform.SqlServer => "SELECT [name] FROM master.dbo.sysdatabases WHERE [name] = '{{MainDB}}'",
        Platform.PostgreSQL => "SELECT datname FROM pg_database WHERE datistemplate = false AND datname = '{{MainDB}}'",
        _ => "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{{MainDB}}'"
    };

    private string MainDbConnectionString(IConfigurationRoot config)
    {
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        return ConnectionString.Build(Platform, config["Target:Server"], config["ScriptTokens:MainDB"],
            config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
    }

    private bool TableExists(IDbCommand cmd, string table)
    {
        cmd.CommandText = Platform switch
        {
            Platform.SqlServer => $"SELECT COUNT(*) FROM sys.tables WHERE [name] = '{table}'",
            Platform.PostgreSQL => $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'",
            _ => $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'"
        };
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void Cleanup(IDbCommand cmd)
    {
        foreach (var table in TablesChildFirst)
        {
            cmd.CommandText = Platform switch
            {
                Platform.SqlServer => $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.{table}",
                Platform.PostgreSQL => $"DROP TABLE IF EXISTS public.\"{table}\" CASCADE",
                _ => $"DROP TABLE IF EXISTS `{table}`"
            };
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = Platform switch
        {
            Platform.SqlServer => $"DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{ProductName}'",
            Platform.PostgreSQL => $"DELETE FROM \"SchemaSmith\".\"ProductOwnership\" WHERE \"ProductName\" = '{ProductName}'",
            _ => $"DELETE FROM SchemaSmith_ProductOwnership WHERE ProductName = '{ProductName}'"
        };
        cmd.ExecuteNonQuery();
    }

    private static string LogDirFor(string tempDir, string run)
    {
        var dir = Path.Join(tempDir, $"logs-{run}");
        Directory.CreateDirectory(dir);
        return dir;
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

    // Switches reach CommandLineParser through the IEnvironment isolator, not Main's args.
    private void RunSchemaQuench(string logDir, bool whatIf)
    {
        _environment.ClearReceivedCalls();
        _environment.CommandLine.Returns($"SchemaQuench.exe \"--LogPath:{logDir}\"");
        // On this line WhatIf is the WhatIfONLY setting; the --WhatIf switch arrives with a later release.
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var saved = config[SettingsKeys.WhatIfOnly];
        config[SettingsKeys.WhatIfOnly] = whatIf ? "true" : "false";
        try { Program.Main(["SkipKindlingForge"]); }
        finally { config[SettingsKeys.WhatIfOnly] = saved; }
    }
}
