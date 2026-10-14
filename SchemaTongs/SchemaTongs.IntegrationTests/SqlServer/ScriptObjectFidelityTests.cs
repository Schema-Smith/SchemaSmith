// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// Table types, sequences and full-text catalogs are written with every clause the catalog carries, and a module
/// renamed with sp_rename is written under its current name. Each test extracts from the source database, deploys the
/// written script into an empty target database the way the Objects slot does, and asserts the target's catalog.
/// </summary>
[Category("SqlServer")]
[TestFixture]
[Category("Integration")]
public class ScriptObjectFidelityTests
{
    private string _connectionString = "";
    private string _sourceDb = "";
    private string _targetDb = "";
    private bool _fullTextInstalled;
    private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"],
            ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"));
        var unique = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        _sourceDb = $"FidelitySrc_{unique}";
        _targetDb = $"FidelityDst_{unique}";
        CreateDatabases();
        ExtractEverything();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            cmd.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            cmd.ExecuteNonQuery();
        }
    }

    // ---- table types -----------------------------------------------------------------------------------------

    [Test]
    public void TableType_KeepsIdentityComputedDefaultCollationIndexesAndIgnoreDupKey()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("DataTypes", "dbo.TT");
        Deploy(cmd, script);

        cmd.CommandText = @"
SELECT c.name COLLATE DATABASE_DEFAULT + ':' + ISNULL(CONVERT(VARCHAR(10), ic.seed_value) + '/' + CONVERT(VARCHAR(10), ic.increment_value), '') + ':' + ISNULL(cc.definition COLLATE DATABASE_DEFAULT + CASE WHEN cc.is_persisted = 1 THEN '!' ELSE '' END, '') + ':' + ISNULL(dc.definition COLLATE DATABASE_DEFAULT, '') + ':' + ISNULL(c.collation_name COLLATE DATABASE_DEFAULT, '')
  FROM sys.table_types tt
  JOIN sys.columns c ON c.object_id = tt.type_table_object_id
  LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
  LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
  LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
 WHERE tt.name = 'TT' ORDER BY c.column_id";
        var columns = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));
        Assert.That(columns, Is.EqualTo(new[]
        {
            "Id:10/5:::", "Code:::('x'):Latin1_General_BIN", "Amount::::", "Doubled::([Amount]*(2))!::", "Flag:::((0)):"
        }), "identity, computed, DEFAULT or COLLATE was lost: " + script);

        cmd.CommandText = @"
SELECT i.type_desc COLLATE DATABASE_DEFAULT + ':' + CASE WHEN i.is_primary_key = 1 THEN 'PK' WHEN i.is_unique_constraint = 1 THEN 'UQ' ELSE ISNULL(i.name COLLATE DATABASE_DEFAULT, '') END + ':' + CONVERT(VARCHAR(1), i.ignore_dup_key) + ':' +
       STUFF((SELECT ', ' + COL_NAME(ic.object_id, ic.column_id) COLLATE DATABASE_DEFAULT + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '')
  FROM sys.table_types tt JOIN sys.indexes i ON i.object_id = tt.type_table_object_id
 WHERE tt.name = 'TT' ORDER BY i.index_id";
        var indexes = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) indexes.Add(reader.GetString(0));
        // The server numbers constraints before plain indexes on the target, so the order is not asserted.
        Assert.That(indexes, Is.EquivalentTo(new[] { "CLUSTERED:UQ:1:Code, Id", "NONCLUSTERED:IX_Code:0:Code, Amount DESC", "NONCLUSTERED:PK:0:Id" }),
            "the index forms, the named index or IGNORE_DUP_KEY were lost: " + script);
        cmd.CommandText = "SELECT COUNT(*) FROM sys.table_types tt JOIN sys.check_constraints ck ON ck.parent_object_id = tt.type_table_object_id WHERE tt.name = 'TT'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the CHECK was lost");
    }

    // ---- sequences -------------------------------------------------------------------------------------------

    [Test]
    public void Sequence_WideDecimalAliasTypeAndCache_RoundTrip()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        foreach (var name in new[] { "SqBig", "SqNoCache", "SqAlias" })
            Deploy(cmd, Script("Sequences", $"dbo.{name}"));

        cmd.CommandText = @"
SELECT sq.name COLLATE DATABASE_DEFAULT + ':' + TYPE_NAME(sq.user_type_id) COLLATE DATABASE_DEFAULT + ':' + CONVERT(VARCHAR(3), sq.precision) + ':' + CONVERT(VARCHAR(1), sq.is_cached) + ':' + ISNULL(CONVERT(VARCHAR(10), sq.cache_size), '-') + ':' + CONVERT(VARCHAR(50), sq.maximum_value)
  FROM sys.sequences sq ORDER BY sq.name";
        var sequences = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) sequences.Add(reader.GetString(0));
        Assert.That(sequences, Is.EqualTo(new[]
        {
            "SqAlias:MyInt:10:1:-:2147483647",
            "SqBig:decimal:38:1:50:99999999999999999999999999999999999999",
            "SqNoCache:int:10:0:-:100"
        }), "the wide type, the alias type or CACHE was lost");
    }

    // ---- full-text catalogs ----------------------------------------------------------------------------------

    [Test]
    public void FullTextCatalog_KeepsAccentSensitivityAndDefault()
    {
        if (!_fullTextInstalled) Assert.Ignore("Full-text search is not installed on this server.");
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("FullTextCatalogs", "FT_Opts");
        Assert.That(script, Does.Not.Contain("sysfulltextcatalogs"), "the deprecated catalog view must not be used: " + script);
        Deploy(cmd, script);
        Deploy(cmd, Script("FullTextCatalogs", "FT_Plain"));

        cmd.CommandText = "SELECT name COLLATE DATABASE_DEFAULT + ':' + CONVERT(VARCHAR(1), is_accent_sensitivity_on) + ':' + CONVERT(VARCHAR(1), is_default) FROM sys.fulltext_catalogs ORDER BY name";
        var catalogs = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) catalogs.Add(reader.GetString(0));
        Assert.That(catalogs, Is.EqualTo(new[] { "FT_Opts:0:1", "FT_Plain:1:0" }), "ACCENT_SENSITIVITY or AS DEFAULT was lost");
    }

    // ---- renamed modules -------------------------------------------------------------------------------------

    [Test]
    public void RenamedView_IsWrittenUnderItsCurrentName_WithAWarning()
    {
        using var conn = Open(_targetDb);
        using var cmd = conn.CreateCommand();
        var script = Script("Views", "dbo.vNew");
        Assert.That(script, Does.Contain("[dbo].[vNew]").And.Not.Contain("vOld"), script);
        Assert.That(_warnings.Any(w => w.Contains("vNew") && w.Contains("sp_rename") && w.Contains("vOld")), Is.True, "the stale definition must be reported: " + string.Join(" | ", _warnings));
        Deploy(cmd, script);
        cmd.CommandText = "SELECT STRING_AGG(name, ',') FROM sys.views WHERE name IN ('vOld', 'vNew')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("vNew"), "the old name must not come back as a second view");
    }

    // ---- fixture ---------------------------------------------------------------------------------------------

    private string Script(string folder, string name)
    {
        Assert.That(_scripts.TryGetValue($"{folder}/{name}", out var script), Is.True, $"{folder}/{name} was not extracted; extracted: {string.Join(", ", _scripts.Keys)}");
        return script;
    }

    private static void Deploy(IDbCommand cmd, string script)
    {
        foreach (var batch in SqlServerBatchSplitter.Split(script))
        {
            cmd.CommandText = batch;
            cmd.ExecuteNonQuery();
        }
    }

    private IDbConnection Open(string database)
    {
        var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(database);
        return conn;
    }

    private void ExtractEverything()
    {
        var file = Substitute.For<IFile>();
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>())).Do(ci =>
        {
            var parts = ci.ArgAt<string>(0).Replace('\\', '/').Split('/');
            if (parts.Length < 2) return;
            var folder = parts[^2];
            if (folder is "DataTypes" or "Sequences" or "FullTextCatalogs" or "Views")
                _scripts[$"{folder}/{System.IO.Path.GetFileNameWithoutExtension(parts[^1])}"] = ci.ArgAt<string>(1);
        });
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString()));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            FactoryContainer.Register(file);
            FactoryContainer.Register(Substitute.For<IDirectory>());
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config["SqlServer:Port"];
            config["Source:User"] = config["SqlServer:User"];
            config["Source:Password"] = config["SqlServer:Password"];
            config["Source:database"] = _sourceDb;
            foreach (var prop in ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            foreach (var kind in new[] { "Tables", "Schemas", "Functions", "Procedures", "TableTriggers", "StopLists", "DDLTriggers", "XMLSchemaCollections", "IndexedViews", "Synonyms" })
                config[$"ShouldCast:{kind}"] = "false";
            foreach (var kind in new[] { "UserDefinedTypes", "Sequences", "Catalogs", "Views" })
                config[$"ShouldCast:{kind}"] = "true";
            FactoryContainer.Register<IConfigurationRoot>(config);
            new SchemaTongs(Platform.SqlServer).CastTemplate();
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    private void CreateDatabases()
    {
        using var conn = Open("master");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')";
        _fullTextInstalled = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
        foreach (var db in new[] { _sourceDb, _targetDb })
        {
            Run(cmd, $"CREATE DATABASE [{db}]");
            conn.ChangeDatabase(db);
            Run(cmd, "CREATE TYPE dbo.MyInt FROM INT NOT NULL");
            conn.ChangeDatabase("master");
        }

        conn.ChangeDatabase(_sourceDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
        Run(cmd, @"
CREATE TYPE dbo.TT AS TABLE (
    Id INT IDENTITY(10, 5) NOT NULL PRIMARY KEY NONCLUSTERED,
    Code VARCHAR(10) COLLATE Latin1_General_BIN NOT NULL DEFAULT ('x'),
    Amount DECIMAL(10, 2) NULL,
    Doubled AS (Amount * 2) PERSISTED,
    Flag BIT NOT NULL DEFAULT (0),
    INDEX IX_Code NONCLUSTERED (Code, Amount DESC),
    UNIQUE CLUSTERED (Code, Id) WITH (IGNORE_DUP_KEY = ON),
    CHECK (Amount >= 0)
)");
        Run(cmd, "CREATE SEQUENCE dbo.SqBig AS DECIMAL(38, 0) START WITH 1 INCREMENT BY 1 CACHE 50");
        Run(cmd, "CREATE SEQUENCE dbo.SqNoCache AS INT START WITH 5 INCREMENT BY 2 MINVALUE 0 MAXVALUE 100 CYCLE NO CACHE");
        Run(cmd, "CREATE SEQUENCE dbo.SqAlias AS dbo.MyInt START WITH 1 INCREMENT BY 1");
        if (_fullTextInstalled)
        {
            Run(cmd, "CREATE FULLTEXT CATALOG FT_Opts WITH ACCENT_SENSITIVITY = OFF AS DEFAULT");
            Run(cmd, "CREATE FULLTEXT CATALOG FT_Plain");
        }
        Run(cmd, "CREATE VIEW dbo.vOld AS SELECT 1 AS x");
        Run(cmd, "EXEC sp_rename 'dbo.vOld', 'vNew'");
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
