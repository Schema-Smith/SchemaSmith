// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.UnitTests;

/// <summary>
/// Schema-template extraction mode tests for DataTongs (design §8, plan §10.3, slice 7).
/// Two-signal mode detection: target template's <c>Template.json</c> has
/// <c>SchemaIdentificationScript</c> AND <c>Source.Schema</c> non-empty. In that mode,
/// merge scripts emit unqualified filenames and <c>{{SchemaName}}</c> destination refs,
/// content files use unqualified names, qualified <c>Tables[N].Name</c> entries are
/// rejected at startup, and <c>ConfigureDataDelivery</c> does not add a <c>Schema</c>
/// field back to the destination Table JSON.
/// </summary>
[TestFixture]
public class SchemaTemplateModeTests
{
    private const string SourceSchema = "tenant_seed";

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

        // Default: directory layout exists. Specific tests override Exists/ReadAllText as needed.
        _directoryWrapper.Exists(Arg.Any<string>()).Returns(true);
        _fileWrapper.Exists(Arg.Any<string>()).Returns(false);

        _templateRoot = Path.Combine(Path.GetTempPath(), "slice7-template-" + Guid.NewGuid().ToString("N"));
    }

    private void RegisterTemplateJson(string templateRoot, bool isSchemaTemplate, string sourceSchema = "")
    {
        var templateJsonPath = Path.Combine(templateRoot, "Template.json");
        _fileWrapper.Exists(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == templateJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(true);

        var json = isSchemaTemplate
            ? "{\"SchemaIdentificationScript\":\"SELECT name FROM dbo.Tenants WHERE Status='Active'\"}"
            : "{}";

        _fileWrapper.ReadAllText(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
            == templateJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(json);
    }

    /// <summary>
    /// Builds a config that points DataTongs at <paramref name="templateRoot"/> for both
    /// ContentPath and ScriptPath, and (when sourceSchema is non-empty) sets the new
    /// <c>Source:Schema</c> field added in slice 7.
    /// </summary>
    private IConfigurationRoot BuildConfig(string templateRoot, string sourceSchema, string tableName,
        Dictionary<string, string> extras = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["Source:Schema"] = sourceSchema ?? "",
            ["ContentPath"] = templateRoot + "/Table Data",
            ["ScriptPath"] = templateRoot + "/Table Data",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "true",
            ["ShouldCast:TokenizeScripts"] = "true",
            // MergeUpdate=false keeps the BuildMergeScript catalog-probe sequence minimal
            // (skips GetUpdateColumns); these tests assert SHAPE of the output, not the
            // merge body's update logic — that's covered by MergeScriptHelperTests at the
            // right boundary.
            ["ShouldCast:MergeUpdate"] = "false",
            ["Tables:0:Name"] = tableName,
            ["Tables:0:KeyColumns"] = "[Id]"
        };

        if (extras != null)
            foreach (var kv in extras) settings[kv.Key] = kv.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// Queue-based ExecuteScalar that satisfies the catalog probes DataTongs and
    /// MergeScriptHelper run for a single-table, single-row extraction. Sufficient for
    /// asserting OUTPUT shape; not a substitute for integration tests.
    /// </summary>
    private void StubCatalogQueriesForSingleTable(string keyValue = "[Id]")
    {
        var sequence = new Queue<object>(new object[]
        {
            true,                              // TableExists
            keyValue,                          // GetSelectColumns
            "[{\"Id\":1,\"Name\":\"Row1\"}]", // GetTableDataJson
            // MergeScriptHelper.BuildMergeScript catalog probes follow:
            null,                              // GetUnsupportedColumnComments
            "[Id]",                            // GetJsonSelectColumns
            false,                             // NeedsIdentityInsert
            "           [Id] INT",            // GetJsonColumnDefinitions
            "        [Id]"                    // GetInsertColumns
        });
        // The compat-cliff metadata fallback probes compatibility_level before several metadata queries;
        // answer those out-of-band (0 = modern / >= 130, the STRING_AGG path) so they don't consume the
        // data sequence and this stub stays aligned regardless of how many probes fire.
        _command.ExecuteScalar().Returns(_ =>
            _command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true ? "16.0.1000.6"
            : _command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true ? 160
            : _command.CommandText?.Contains("compatibility_level", StringComparison.OrdinalIgnoreCase) == true
                ? 0
                : sequence.Count > 0 ? sequence.Dequeue() : null);
    }

    #region Mode detection

    [Test]
    public void SchemaTemplateMode_BothSignalsSet_EmitsUnqualifiedMergeScriptFilename()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            var config = BuildConfig(_templateRoot, SourceSchema, "Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            // Schema-template mode → unqualified filename: "Populate Customers.sql"
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate Customers.sql")),
                Arg.Any<string>());
            // Negative assertion: schema-qualified filename must NOT be written.
            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith($"Populate {SourceSchema}.Customers.sql")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_BothSignalsSet_EmitsUnqualifiedContentFilename()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            var config = BuildConfig(_templateRoot, SourceSchema, "Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Customers.tabledata")),
                Arg.Any<string>());
            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith($"{SourceSchema}.Customers.tabledata")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_BothSignalsSet_MergeBodyUsesSchemaNameToken()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            var config = BuildConfig(_templateRoot, SourceSchema, "Customers");
            StubCatalogQueriesForSingleTable();

            string capturedBody = null;
            _fileWrapper.When(f => f.WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate Customers.sql")),
                Arg.Any<string>())).Do(ci => capturedBody = ci.ArgAt<string>(1));

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            Assert.That(capturedBody, Is.Not.Null, "Merge script body must be written.");
            // Destination ref uses {{SchemaName}} (passed via destSchemaOverride to MergeScriptHelper).
            Assert.That(capturedBody, Does.Contain("MERGE INTO [{{SchemaName}}].[Customers] AS Target"));
            // Source schema literal must not appear in destination refs.
            Assert.That(capturedBody, Does.Not.Contain($"[{SourceSchema}].[Customers]"),
                "Source schema literal must not appear in destination refs in schema-template mode.");
            // Content-file token must be unqualified (matches the unqualified .tabledata filename).
            Assert.That(capturedBody, Does.Contain("{{Customers.tabledata}}"));
            Assert.That(capturedBody, Does.Not.Contain($"{{{SourceSchema}.Customers.tabledata}}}}"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_SignalOnlySchemaIdScript_ErrorsWithPrescribedMessage()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            // Source.Schema NOT set, target IS a schema template.
            var config = BuildConfig(_templateRoot, "", "Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            var ex = Assert.Throws<InvalidOperationException>(() => dt.CastData());
            Assert.That(ex.Message, Does.Contain("Source.Schema"),
                "Error must name the missing setting.");
            Assert.That(ex.Message, Does.Contain("schema template"),
                "Error must explain the target is a schema template.");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_SignalOnlySourceSchema_RegularModeWithWarning()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            // Target template is regular (no SchemaIdentificationScript).
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: false);
            var config = BuildConfig(_templateRoot, SourceSchema, "Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => dt.CastData());

            // Warning was emitted that Source.Schema is being ignored.
            _progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("Source.Schema") && s.Contains("ignored")));
            // Output is in regular (qualified) form because target is not a schema template —
            // user-provided Tables[0].Name has no schema qualifier so it lands in dbo by default.
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate dbo.Customers.sql")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_NeitherSignal_TodaysBehaviorUnchanged()
    {
        // Regression guard: neither signal set → existing single-DB extraction behavior.
        // (No Template.json at the output path, no Source.Schema in settings.)
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            // Template.json absent — DataTongs walks up and finds nothing.
            var config = BuildConfig(_templateRoot, "", "dbo.Users");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => dt.CastData());

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Populate dbo.Users.sql")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region Qualified-name rejection (§8.3)

    [Test]
    public void SchemaTemplateMode_QualifiedTableName_ErrorsWithPrescribedMessage()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            // User wrote Tables[0].Name = "tenant_seed.Customers" — qualified in schema-template
            // mode is a config error per §8.3.
            var config = BuildConfig(_templateRoot, SourceSchema, $"{SourceSchema}.Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            var ex = Assert.Throws<InvalidOperationException>(() => dt.CastData());
            Assert.That(ex.Message, Does.Contain("unqualified"),
                "Error must call out the unqualified-name requirement.");
            Assert.That(ex.Message, Does.Contain($"\"{SourceSchema}.Customers\""),
                "Error must name the offending Tables[N].Name verbatim.");
            Assert.That(ex.Message, Does.Contain("Source.Schema"),
                "Error must point the user at the Source.Schema setting as the alternative.");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void SchemaTemplateMode_UnqualifiedTableName_Accepted()
    {
        // Regression guard for the qualified-name check.
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);
            var config = BuildConfig(_templateRoot, SourceSchema, "Customers");
            StubCatalogQueriesForSingleTable();

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => dt.CastData());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region ConfigureDataDelivery in schema-template mode (§8.3, §8.4)

    [Test]
    public void SchemaTemplateMode_ConfigureDataDelivery_DoesNotAddSchemaFieldBack()
    {
        // Per §8.3: ConfigureDataDelivery is honored in schema-template mode, but the
        // existing Table.json (with omitted Schema) must NOT have a Schema field added
        // back by the configurator. The schema-template's tables are schemaless by
        // construction.
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterTemplateJson(_templateRoot, isSchemaTemplate: true);

            // Existing Table.json (unqualified filename, no Schema field — produced by
            // SchemaTongs slice 6).
            var tableJsonPath = Path.Combine(_templateRoot, "Tables", "Customers.json");
            var initialTableJson = "{\"Name\":\"Customers\",\"Columns\":[]}";
            _fileWrapper.Exists(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
                == tableJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(true);
            _fileWrapper.ReadAllText(Arg.Is<string>(p => p.Replace('/', Path.DirectorySeparatorChar)
                == tableJsonPath.Replace('/', Path.DirectorySeparatorChar))).Returns(initialTableJson);

            var config = BuildConfig(_templateRoot, SourceSchema, "Customers",
                new Dictionary<string, string> { ["ShouldCast:ConfigureDataDelivery"] = "true" });
            StubCatalogQueriesForSingleTable();

            string capturedTableJson = null;
            _fileWrapper.When(f => f.WriteAllText(
                Arg.Is<string>(s => s.EndsWith("Customers.json")),
                Arg.Any<string>())).Do(ci => capturedTableJson = ci.ArgAt<string>(1));

            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(_connectionFactory);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            Assert.That(capturedTableJson, Is.Not.Null,
                "ConfigureDataDelivery must update the Table.json.");
            var parsed = JObject.Parse(capturedTableJson);
            Assert.That(parsed["Schema"], Is.Null,
                "Schema-template Table.json must not have a Schema field added by the configurator.");
            Assert.That(parsed["DataDelivery"], Is.Not.Null,
                "DataDelivery block must be added.");

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion
}
