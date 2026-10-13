// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
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
/// A system-managed history table must not end up as a table file in the extracted package.
/// <para>SchemaSmith creates a temporal history table from the versioned table's own declaration —
/// <c>IsTemporal</c> plus the <c>HistoryTable*</c> properties. Extracting it separately writes a second
/// table file that the next deploy then tries to create in its own right, so the package no longer
/// round-trips.</para>
/// <para>Raised while checking ledger (gap item J3) for the failure
/// <see href="https://github.com/Schema-Smith/SchemaSmith/issues/402">#402</see> exposed for graph tables.
/// A ledger table's own generated columns are already excluded — they report
/// <c>generated_always_type</c> 7–10 — but an updatable ledger table also spawns
/// <c>MSSQL_LedgerHistoryFor_&lt;object_id&gt;</c>, whose <c>temporal_type</c> is
/// <c>NON_TEMPORAL_TABLE</c>, so nothing keyed on temporal reaches it.</para>
/// <para><b>Asserted on the files SchemaTongs actually writes</b>, by running a real extraction rather
/// than by re-executing a copy of its table-list query. A test holding its own copy of the query passes
/// happily while the real one drifts — which is the whole failure mode this guards.</para>
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class HistoryTableExtractionTests
{
    private const string ProductName = "HistoryTableProduct";
    private const string TemplateName = "Main";

    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath = "";
    private bool _ledgerSupported;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = $"TongsHist_{Guid.NewGuid():N}"[..30];

        Master($"CREATE DATABASE [{_db}]");

        OnDb(cmd =>
        {
            Run(cmd, """
                CREATE TABLE dbo.Versioned (
                    Id INT NOT NULL PRIMARY KEY,
                    V INT NULL,
                    SysStart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                    SysEnd DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                    PERIOD FOR SYSTEM_TIME (SysStart, SysEnd))
                WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.Versioned_Hist))
                """);

            // Graph tables join the fixture so one round trip covers #402 and #403 together --
            // both were "extraction emits something that cannot be deployed".
            Run(cmd, "CREATE TABLE dbo.GraphPerson (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL) AS NODE");
            Run(cmd, "CREATE TABLE dbo.GraphKnows (Since DATE NULL) AS EDGE");

            cmd.CommandText = "SELECT COUNT(*) FROM sys.all_columns "
                              + "WHERE object_id = OBJECT_ID('sys.tables') AND name = 'ledger_type'";
            _ledgerSupported = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            if (_ledgerSupported)
            {
                // LedgerAcct is dropped by the artefact test to produce the retained MSSQL_Dropped*
                // objects. LedgerLive stays, so the LIVE ledger view (LedgerLive_Ledger) exists at
                // extraction time -- otherwise the ledger_view_id filter is never exercised and would
                // ship untested.
                Run(cmd, "CREATE TABLE dbo.LedgerAcct (Id INT NOT NULL PRIMARY KEY, Bal DECIMAL(18,2) NULL) "
                         + "WITH (SYSTEM_VERSIONING = ON, LEDGER = ON)");
                Run(cmd, "CREATE TABLE dbo.LedgerLive (Id INT NOT NULL PRIMARY KEY, Bal DECIMAL(18,2) NULL) "
                         + "WITH (SYSTEM_VERSIONING = ON, LEDGER = ON)");
            }
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            OnDb(cmd =>
            {
                try { Run(cmd, "ALTER TABLE dbo.Versioned SET (SYSTEM_VERSIONING = OFF)"); } catch (DbException) { /* best effort */ }
            });
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
            conn.Open();
            using var dropCmd = conn.CreateCommand();
            dropCmd.CommandTimeout = 300;
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(dropCmd, _db);
        }
        catch (DbException) { /* teardown must not mask an assertion */ }

        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void Master(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, sql);
    }

    private void OnDb(Action<IDbCommand> act)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        conn.ChangeDatabase(_db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    [Test]
    public void ARealExtraction_WritesNoHistoryTableFile()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsHist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(environment);
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);

            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }

        var tablesDir = Path.Join(_tempProductPath, "Templates", TemplateName, "Tables");
        Assert.That(Directory.Exists(tablesDir), Is.True, "extraction must have produced a Tables folder");
        var files = string.Join(";", Directory.GetFiles(tablesDir, "*.json"));

        Assert.Multiple(() =>
        {
            Assert.That(files, Does.Contain("Versioned"),
                "the versioned table itself must still be extracted -- an over-broad exclusion that also "
                + "dropped the real table would otherwise pass every assertion below");
            Assert.That(files, Does.Not.Contain("Versioned_Hist"),
                "the temporal history table is created by the versioned table's own declaration; a file "
                + "for it makes the next deploy try to create it in its own right.\nFiles: " + files);

            if (_ledgerSupported)
                Assert.That(files, Does.Not.Contain("MSSQL_LedgerHistoryFor"),
                    "an updatable ledger table's history is named after an object id on the SOURCE server, "
                    + "so that file could not be deployed anywhere.\nFiles: " + files);
        });
    }

    /// <summary>
    /// The rest of the ledger artefact family, found by probing what a ledger table actually leaves behind.
    /// <para>A live updatable ledger table auto-creates a <b>ledger view</b> (<c>&lt;table&gt;_Ledger</c>),
    /// and dropping a ledger table does not remove it — SQL Server renames it to
    /// <c>MSSQL_DroppedLedgerTable_&lt;name&gt;_&lt;guid&gt;</c> and leaves
    /// <c>MSSQL_DroppedLedgerHistory_*</c> and <c>MSSQL_DroppedLedgerView_*</c> beside it. All of them
    /// report <c>is_ms_shipped = 0</c>, so all of them were being extracted as user objects.</para>
    /// <para>Two of these need different filters, which is why probing mattered:
    /// <c>MSSQL_DroppedLedgerHistory_*</c> has <c>is_dropped_ledger_table = 0</c>, so the obvious flag
    /// misses it; and the live ledger view carries no <c>MSSQL_</c> prefix at all, so only
    /// <c>sys.tables.ledger_view_id</c> identifies it.</para>
    /// </summary>
    [Test]
    public void LedgerArtefacts_AreNotOfferedForExtraction()
    {
        if (!_ledgerSupported)
            Assert.Ignore("Ledger tables need SQL Server 2022 (major 16); nothing here applies below it.");

        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsLedger_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);

        // Drop the ledger table so the retained artefacts exist alongside the live ones.
        OnDb(cmd => Run(cmd, "DROP TABLE dbo.LedgerAcct"));

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());

            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }

        var templateDir = Path.Join(_tempProductPath, "Templates", TemplateName);
        var written = string.Join(";", Directory.Exists(templateDir)
            ? Directory.GetFiles(templateDir, "*.*", SearchOption.AllDirectories).Select(Path.GetFileName)
            : []);

        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Contain("Versioned"),
                "the ordinary tables must still be extracted -- an over-broad filter would pass every "
                + "assertion below while emptying the package."
                    + " Written: " + written);

            foreach (var artefact in new[]
                     {
                         "MSSQL_DroppedLedgerTable", "MSSQL_DroppedLedgerHistory", "MSSQL_DroppedLedgerView",
                     })
                Assert.That(written, Does.Not.Contain(artefact),
                    $"'{artefact}' is retained by the engine when a ledger table is dropped, and its name "
                    + "carries a GUID -- it can be deployed nowhere."
                        + " Written: " + written);

            Assert.That(written, Does.Contain("LedgerLive"),
                "the live ledger TABLE is a real user table and must still be extracted -- only the view "
                + "it generates is engine-owned. Written: " + written);
            Assert.That(written, Does.Not.Contain("_Ledger."),
                "the live ledger view <table>_Ledger is generated by the ledger table's own declaration; "
                + "extracting it as a user view means the next deploy tries to create it in its own "
                + "right."
                    + " Written: " + written);
        });
    }

    private IConfigurationRoot BuildConfig()
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["SqlServer:Server"],
            ["Source:Port"] = root["SqlServer:Port"],
            ["Source:User"] = root["SqlServer:User"],
            ["Source:Password"] = root["SqlServer:Password"],
            ["Source:Database"] = _db,
            ["Target:Server"] = root["SqlServer:Server"],
            ["Target:Port"] = root["SqlServer:Port"],
            ["Target:User"] = root["SqlServer:User"],
            ["Target:Password"] = root["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "true",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Schemas"] = "false",
            ["ShouldCast:IndexedViews"] = "false",
            ["ShouldCast:Sequences"] = "false",
            ["ShouldCast:Synonyms"] = "false",
            ["ShouldCast:XmlSchemaCollections"] = "false",
            ["ShouldCast:FullTextCatalogs"] = "false",
            ["ShouldCast:FullTextStopLists"] = "false",
        };
        // Both halves need them: extraction reads Source:*, the redeploy reads Target:*, and a missing
        // TrustServerCertificate surfaces only as "Error validating configured servers".
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
