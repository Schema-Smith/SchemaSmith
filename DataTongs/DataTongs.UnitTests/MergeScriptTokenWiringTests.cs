// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.UnitTests;

/// <summary>
/// Issue #390: DataTongs emits a {{key}} content-file token into every tokenized merge script but
/// never wrote the matching ScriptTokens entry, so the default output could not deploy. These tests
/// cover the wiring end to end at the DataTongs level (filename/token identity, idempotency, and
/// delivery-takes-precedence), complementing the lower-level MergeScriptHelperTests coverage of the
/// contentFileToken parameter itself and MergeScriptTokenConfiguratorImplTests' coverage of the
/// ScriptTokens write mechanics. Follows SchemaTemplateModeTests' mocked-CastData() harness.
/// </summary>
[TestFixture]
public class MergeScriptTokenWiringTests
{
    private ILog _progressLog;
    private IDbConnectionFactory _connectionFactory;
    private IDbConnection _connection;
    private IDbCommand _command;
    private IFile _fileWrapper;
    private IDirectory _directoryWrapper;
    private string _templateRoot;

    private void SetUpMocks()
    {
        _progressLog = Substitute.For<ILog>();
        _connectionFactory = Substitute.For<IDbConnectionFactory>();
        _connection = Substitute.For<IDbConnection>();
        _command = Substitute.For<IDbCommand>();
        _fileWrapper = Substitute.For<IFile>();
        _directoryWrapper = Substitute.For<IDirectory>();

        _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_connection);
        _connection.CreateCommand().Returns(_command);

        _directoryWrapper.Exists(Arg.Any<string>()).Returns(true);
        _fileWrapper.Exists(Arg.Any<string>()).Returns(false);

        _templateRoot = Path.Join(Path.GetTempPath(), "token-wiring-" + Guid.NewGuid().ToString("N"));
    }

    private void RegisterTemplateJson(string templateRoot, string scriptTokensJson = null)
    {
        var templateJsonPath = Path.Join(templateRoot, "Template.json");
        _fileWrapper.Exists(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == templateJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(true);

        var json = scriptTokensJson == null
            ? "{\"Name\":\"Main\"}"
            : $"{{\"Name\":\"Main\",\"ScriptTokens\":{scriptTokensJson}}}";

        _fileWrapper.ReadAllText(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == templateJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(json);
    }

    private void RegisterTableJsonFound(string templateRoot, string tableFileName)
    {
        var tableJsonPath = Path.Join(templateRoot, "Tables", tableFileName);
        _fileWrapper.Exists(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == tableJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(true);
        _fileWrapper.ReadAllText(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == tableJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns("{\"Columns\":[]}");
    }

    private IConfigurationRoot BuildConfig(string templateRoot, string tableName,
        Dictionary<string, string> extras = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ContentPath"] = templateRoot + "/Table Data",
            ["ScriptPath"] = templateRoot + "/Table Data",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "true",
            ["ShouldCast:TokenizeScripts"] = "true",
            // MergeUpdate=false keeps the BuildMergeScript catalog-probe sequence minimal — these
            // tests assert wiring/identity, not the merge body itself (that's MergeScriptHelperTests).
            ["ShouldCast:MergeUpdate"] = "false",
            ["Tables:0:Name"] = tableName,
            ["Tables:0:KeyColumns"] = "[Id]"
        };

        // extras overrides/extends the defaults above -- EXCEPT a null value, which means "remove
        // this key entirely" rather than "set it to null". Several base settings above (notably
        // ShouldCast:OutputScripts) are seeded unconditionally, so a test that wants to prove
        // "defaulted" behaviour (config[key] == null) cannot achieve that by simply omitting the
        // key from extras -- the base value would still be present. Passing null makes the
        // omission real and gives DataTongs.CastData's config[key] == null check (the same check
        // it uses to distinguish defaulted from explicit) something genuine to see.
        if (extras != null)
        {
            foreach (var kv in extras)
            {
                if (kv.Value == null)
                    settings.Remove(kv.Key);
                else
                    settings[kv.Key] = kv.Value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// Same queue shape as SchemaTemplateModeTests.StubCatalogQueriesForSingleTable — sufficient
    /// for asserting output/wiring shape for a single-table, single-row extraction.
    /// </summary>
    private void StubCatalogQueriesForSingleTable()
    {
        var sequence = new Queue<object>(new object[]
        {
            true,                              // TableExists
            "[Id]",                            // GetSelectColumns
            "[{\"Id\":1,\"Name\":\"Row1\"}]",   // GetTableDataJson
            null,                              // GetUnsupportedColumnComments
            "[Id]",                            // GetJsonSelectColumns
            false,                             // NeedsIdentityInsert
            "           [Id] INT",             // GetJsonColumnDefinitions
            "        [Id]"                     // GetInsertColumns
        });
        _command.ExecuteScalar().Returns(_ =>
            _command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true ? "16.0.1000.6"
            : _command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true ? 160
            : _command.CommandText?.Contains("compatibility_level", StringComparison.OrdinalIgnoreCase) == true
                ? 0
                : sequence.Count > 0 ? sequence.Dequeue() : null);
    }

    private void Register(IConfigurationRoot config)
    {
        FactoryContainer.Register<IConfigurationRoot>(config);
        FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
        FactoryContainer.Register(_fileWrapper);
        FactoryContainer.Register(_directoryWrapper);
        LogFactory.Register("ProgressLog", _progressLog);
    }

    [Test]
    public void TokenizeScripts_Default_WiresScriptTokensEntry_WithTemplateRelativePath()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            var config = BuildConfig(_templateRoot, "TestTable");
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Non-schema-template mode with a non-empty schema ("dbo") qualifies the key, matching
            // the filename DataTongs writes.
            _fileWrapper.Received(1).WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Template.json")),
                Arg.Is<string>(s =>
                    s.ContainsIgnoringCase("\"dbo.TestTable.tabledata\"") &&
                    s.ContainsIgnoringCase("<*File*>Table Data/dbo.TestTable.tabledata")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void TokenizeScripts_AlreadyWired_SecondRunWritesNothingToTemplateJson()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot,
                "{\"dbo.TestTable.tabledata\":\"<*File*>Table Data/dbo.TestTable.tabledata\"}");
            var config = BuildConfig(_templateRoot, "TestTable");
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Template.json")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void NameNeedingFileNameEncoder_FilenameAndTokenAreTheSameEncodedString()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            // ':' is illegal in a filename and FileNameEncoder encodes it to %3A. Before #390's fix
            // the token was derived unencoded, disagreeing with the encoded filename.
            var config = BuildConfig(_templateRoot, "Sales:Report");
            StubCatalogQueriesForSingleTable();
            Register(config);

            string capturedBody = null;
            _fileWrapper.When(f => f.WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate dbo.Sales%3AReport.sql")),
                Arg.Any<string>())).Do(ci => capturedBody = ci.ArgAt<string>(1));

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("dbo.Sales%3AReport.tabledata")),
                Arg.Any<string>());
            Assert.That(capturedBody, Is.Not.Null, "Merge script for the encoded filename must be written.");
            Assert.That(capturedBody, Does.Contain("{{dbo.Sales%3AReport.tabledata}}"));
            Assert.That(capturedBody, Does.Not.Contain("{{dbo.Sales:Report.tabledata}}"),
                "The unencoded (mismatched) form must not appear.");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void EmptySchemaOutsideSchemaTemplateMode_NoLeadingDotMismatch()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            // A leading-dot table name ("." + name) parses to an empty schema on SQL Server even
            // outside schema-template mode -- the other reachable mismatch case from the design doc.
            var config = BuildConfig(_templateRoot, ".Widget");
            StubCatalogQueriesForSingleTable();
            Register(config);

            string capturedBody = null;
            _fileWrapper.When(f => f.WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate Widget.sql")),
                Arg.Any<string>())).Do(ci => capturedBody = ci.ArgAt<string>(1));
            string capturedContentFilePath = null;
            _fileWrapper.When(f => f.WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Widget.tabledata")),
                Arg.Any<string>())).Do(ci => capturedContentFilePath = ci.ArgAt<string>(0));

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            Assert.That(capturedContentFilePath, Is.Not.Null, "Content file must be written.");
            Assert.That(Path.GetFileName(capturedContentFilePath), Is.EqualTo("Widget.tabledata"),
                "Filename must be unqualified, not '.Widget.tabledata'.");
            Assert.That(capturedBody, Is.Not.Null);
            Assert.That(capturedBody, Does.Contain("{{Widget.tabledata}}"));
            Assert.That(capturedBody, Does.Not.Contain("{{.Widget.tabledata}}"),
                "The leading-dot (mismatched) form must not appear.");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void DeliveryConfigured_SuppressesMergeScriptForThatTable()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            RegisterTableJsonFound(_templateRoot, "dbo.TestTable.json");
            var config = BuildConfig(_templateRoot, "TestTable",
                new Dictionary<string, string> { ["ShouldCast:ConfigureDataDelivery"] = "true" });
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Delivery was configured for this table -> no merge script.
            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate dbo.TestTable.sql")),
                Arg.Any<string>());
            // The content file and the Table.json delivery config are still written.
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("dbo.TestTable.tabledata")),
                Arg.Any<string>());
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("dbo.TestTable.json")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void DeliveryDeclined_TableJsonNotFound_StillEmitsMergeScript()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            // Table.json is never found for this table -- FindTableJsonFile declines by returning
            // null (Tables dir itself reports missing, short-circuiting before the glob fallback).
            _directoryWrapper.Exists(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
                == Path.Join(_templateRoot, "Tables"))).Returns(false);
            var config = BuildConfig(_templateRoot, "TestTable",
                new Dictionary<string, string> { ["ShouldCast:ConfigureDataDelivery"] = "true" });
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Per-table, not global: this table's delivery was declined, so its merge script must
            // still be emitted -- a global suppression switch would wrongly drop it.
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate dbo.TestTable.sql")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ConfigureDataDelivery_OutputScriptsDefaulted_LogsInfoOnce()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            RegisterTableJsonFound(_templateRoot, "dbo.TestTable.json");
            var config = BuildConfig(_templateRoot, "TestTable", new Dictionary<string, string>
            {
                ["ShouldCast:ConfigureDataDelivery"] = "true",
                // BuildConfig's base seeds ShouldCast:OutputScripts = "true" unconditionally, so
                // merely omitting it from this dictionary would NOT make it absent -- null here
                // tells BuildConfig to remove the key, so config[key] == null genuinely holds and
                // this test actually exercises the "defaulted" branch, not "explicit true".
                ["ShouldCast:OutputScripts"] = null
            });
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Positive: the defaulted branch fires, once. Negative: the explicit-contradiction
            // branch must NOT also fire -- otherwise a future change collapsing the two branches
            // into one message would still pass this test.
            _progressLog.Received(1).Info(Arg.Is<string>(s =>
                s.ContainsIgnoringCase("suppressed") && s.ContainsIgnoringCase("defaulted")));
            _progressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.ContainsIgnoringCase("suppressed")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ConfigureDataDelivery_OutputScriptsExplicitTrue_WarnsOnce()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot);
            RegisterTableJsonFound(_templateRoot, "dbo.TestTable.json");
            var config = BuildConfig(_templateRoot, "TestTable", new Dictionary<string, string>
            {
                ["ShouldCast:ConfigureDataDelivery"] = "true",
                ["ShouldCast:OutputScripts"] = "true" // explicit, contradicts ConfigureDataDelivery
            });
            StubCatalogQueriesForSingleTable();
            Register(config);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Positive: the explicit-contradiction branch fires, once. Negative: the defaulted
            // branch must NOT also fire -- the mirror of the assertion above, so the two tests
            // together prove the branches are mutually exclusive rather than both passing by luck.
            _progressLog.Received(1).Warn(Arg.Is<string>(s =>
                s.ContainsIgnoringCase("suppressed") && s.ContainsIgnoringCase("Delivery takes precedence")));
            _progressLog.DidNotReceive().Info(Arg.Is<string>(s => s.ContainsIgnoringCase("suppressed")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }
}
