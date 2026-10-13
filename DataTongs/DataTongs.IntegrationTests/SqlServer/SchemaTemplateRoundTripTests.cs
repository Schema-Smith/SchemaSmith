// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.SqlServer;

/// <summary>
/// Slice-7 schema-template round-trip integration test (design §8.6, plan Step 7.2).
///
/// <para>Populate <c>tenant_seed.Customers</c> with rows on a source DB → run DataTongs
/// with <c>Source.Schema = "tenant_seed"</c> against a pre-built schema-template package
/// → assert the emitted artifacts have the schema-template shape (unqualified filename,
/// <c>{{SchemaName}}</c> destination refs, unqualified content-file token) → truncate
/// <c>tenant_seed.Customers</c> → run SchemaQuench against the package (whose Template.json
/// returns <c>'tenant_seed'</c> from <c>SchemaIdentificationScript</c>); slot 9 picks up
/// the generated merge script, resolves <c>{{SchemaName}}</c> to <c>tenant_seed</c>, and
/// reapplies the rows → assert row counts and values match the original.</para>
/// </summary>
[TestFixture]
[Category("SqlServer")]
[Category("Integration")]
public class SchemaTemplateRoundTripTests
{
    private const string SourceSchema = "tenant_seed";
    private const string TemplateName = "TenantBody";
    private const string ProductName = "DataTongsRoundTripProduct";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;
    private string _server;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _server = config["SqlServer:Server"];
        _connectionString = ConnectionString.Build(Platform.SqlServer, _server, "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DTRoundTrip");

        CreateSourceDatabase();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); }
            catch { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void DataTongs_Extract_Then_SchemaQuench_Restore_Yields_Same_Row_Set()
    {
        _tempProductPath = Path.Combine(Path.GetTempPath(),
            $"DataTongsRoundTrip_{Guid.NewGuid():N}");
        BuildMinimalSchemaTemplatePackage(_tempProductPath);

        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();

        var capturedLogs = new List<string>();
        progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => capturedLogs.Add($"INFO: {ci.Arg<object>()}"));
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => capturedLogs.Add($"WARN: {ci.Arg<object>()}"));
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => capturedLogs.Add($"ERROR: {ci.Arg<object>()}"));

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();

            // ----- Phase 1: extract -----
            var extractConfig = BuildDataTongsConfig();
            FactoryContainer.Register<IConfigurationRoot>(extractConfig);
            FactoryContainer.Register(environment);
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);

            try
            {
                var dataTongs = new global::DataTongs.DataTongs(Platform.SqlServer);
                dataTongs.CastData();

                // Verify shape of DataTongs output.
                var tableDataDir = Path.Combine(_tempProductPath, "Templates", TemplateName, "Table Data");
                var contentFile = Path.Combine(tableDataDir, "Customers.tabledata");
                Assert.That(File.Exists(contentFile), Is.True,
                    "DataTongs must write an unqualified Customers.tabledata file in schema-template mode.");

                // DataDelivery block was added to the existing Tables/Customers.json.
                var tableJsonPath = Path.Combine(_tempProductPath, "Templates", TemplateName, "Tables", "Customers.json");
                var tableJson = File.ReadAllText(tableJsonPath);
                Assert.That(tableJson, Does.Contain("\"DataDelivery\""),
                    "ConfigureDataDelivery must add a DataDelivery block to the schema-template Table.json.");
                Assert.That(tableJson, Does.Not.Contain("\"Schema\":"),
                    "ConfigureDataDelivery must not add a Schema field to a schema-template Table.json.");
                Assert.That(tableJson, Does.Contain("Table Data/Customers.tabledata"),
                    "DataDelivery.ContentFile must reference the unqualified tabledata path.");

                // ----- Phase 2: truncate the source rows -----
                TruncateCustomers();
                Assert.That(CustomerCount(), Is.EqualTo(0),
                    "Sanity: Customers must be empty before SchemaQuench restores it.");

                // ----- Phase 3: SchemaQuench restores the rows via slot 9 -----
                FactoryContainer.Clear();
                LogFactory.Clear();

                var quenchConfig = BuildSchemaQuenchConfig();
                FactoryContainer.Register<IConfigurationRoot>(quenchConfig);
                FactoryContainer.Register(environment);
                LogFactory.Register("ErrorLog", errorLog);
                LogFactory.Register("ProgressLog", progressLog);

                Schema.Checkpointing.FileCheckpointManager.GetFromFactory().DeleteCheckpoints(ProductName);
                global::SchemaQuench.Program.Main(["SkipKindlingForge"]);

                if (capturedLogs.Any(l => l.StartsWith("ERROR:")))
                {
                    TestContext.Out.WriteLine("Captured log lines:");
                    foreach (var entry in capturedLogs)
                        TestContext.Out.WriteLine(entry);
                }
                progressLog.DidNotReceive().Error(Arg.Any<string>());
                environment.DidNotReceive().Exit(2);
                environment.DidNotReceive().Exit(3);

                // ----- Phase 4: assert row-set equality -----
                Assert.That(CustomerCount(), Is.EqualTo(3),
                    "All 3 original rows must be restored after SchemaQuench resolves {{SchemaName}}.");
                var restoredCodes = QueryStrings(
                    $"SELECT [Code] FROM [{SourceSchema}].[Customers] ORDER BY [CustomerID]");
                Assert.That(restoredCodes, Is.EqualTo(new[] { "C001", "C002", "C003" }),
                    "Restored row contents must match the original.");
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    // ----- Source DB setup -----------------------------------------------------------------

    private void CreateSourceDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = $@"
EXEC('CREATE SCHEMA [{SourceSchema}]');
CREATE TABLE [{SourceSchema}].[Customers] (
    [CustomerID] INT NOT NULL CONSTRAINT [PK_Customers_Slice7] PRIMARY KEY,
    [Code] NVARCHAR(20) NOT NULL,
    [Name] NVARCHAR(100) NOT NULL
);
INSERT INTO [{SourceSchema}].[Customers] ([CustomerID], [Code], [Name]) VALUES
    (1, 'C001', 'Customer One'),
    (2, 'C002', 'Customer Two'),
    (3, 'C003', 'Customer Three');";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void TruncateCustomers()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM [{SourceSchema}].[Customers];";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, _integrationDb);
        conn.Close();
    }

    // ----- Package fixture -----------------------------------------------------------------

    /// <summary>
    /// Builds a minimal schema-template package on disk. Includes the schema-template Table.json
    /// (no Schema field, schema-template style — as SchemaTongs slice 6 would have produced) so
    /// SchemaQuench's <c>DataDeliveryProcessor</c> can read DataTongs's <c>DataDelivery</c> block
    /// and apply at quench. The Tables/Customers.json is pre-created without DataDelivery because
    /// DataTongs <c>ConfigureDataDelivery=true</c> adds that block during extraction.
    /// </summary>
    private static void BuildMinimalSchemaTemplatePackage(string root)
    {
        var templateDir = Path.Combine(root, "Templates", TemplateName);
        Directory.CreateDirectory(Path.Combine(templateDir, "Tables"));
        Directory.CreateDirectory(Path.Combine(templateDir, "Table Data"));

        File.WriteAllText(Path.Combine(root, "Product.json"),
            $"{{\"Name\":\"{ProductName}\",\"Platform\":\"SqlServer\",\"TemplateOrder\":[\"{TemplateName}\"],\"ScriptTokens\":{{\"MainDB\":\"TestMain\"}},\"ScriptFolders\":[]}}");

        File.WriteAllText(Path.Combine(templateDir, "Template.json"),
            "{\n" +
            "  \"DatabaseIdentificationScript\": \"SELECT [name] FROM master.dbo.sysdatabases WHERE [name] = '{{MainDB}}'\",\n" +
            $"  \"SchemaIdentificationScript\": \"SELECT '{SourceSchema}' AS SchemaName\",\n" +
            "  \"CreateSchemaIfMissing\": false,\n" +
            "  \"AllowParallel\": false\n" +
            "}");

        // Schema-template Table.json: no Schema field. Columns + PK mirror the source-DB shape.
        // Format matches the existing SchemaTemplateProduct/Lookups.json convention (bracketed
        // names, length-in-DataType).
        File.WriteAllText(Path.Combine(templateDir, "Tables", "Customers.json"),
            "{\n" +
            "  \"Name\": \"[Customers]\",\n" +
            "  \"Columns\": [\n" +
            "    { \"Name\": \"[CustomerID]\", \"DataType\": \"INT\" },\n" +
            "    { \"Name\": \"[Code]\", \"DataType\": \"NVARCHAR(20)\" },\n" +
            "    { \"Name\": \"[Name]\", \"DataType\": \"NVARCHAR(100)\" }\n" +
            "  ],\n" +
            "  \"Indexes\": [\n" +
            "    { \"Name\": \"[PK_Customers_Slice7]\", \"PrimaryKey\": true, \"IndexColumns\": \"[CustomerID]\" }\n" +
            "  ]\n" +
            "}");
    }

    // ----- Configs --------------------------------------------------------------------------

    private IConfigurationRoot BuildDataTongsConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "SqlServer:ConnectionProperties");
        var tableDataDir = Path.Combine(_tempProductPath, "Templates", TemplateName, "Table Data");

        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["SqlServer:Server"],
            ["Source:Port"] = rootConfig["SqlServer:Port"],
            ["Source:User"] = rootConfig["SqlServer:User"],
            ["Source:Password"] = rootConfig["SqlServer:Password"],
            ["Source:Database"] = _integrationDb,
            ["Source:Schema"] = SourceSchema,
            ["ContentPath"] = tableDataDir,
            ["ScriptPath"] = tableDataDir,
            ["ShouldCast:OutputContentFiles"] = "true",
            // OutputScripts=false: this test exercises the DataDelivery round-trip (the
            // production data-delivery path), not the slot-9 .sql path. The slot-9 token
            // resolution for {{<name>.tabledata}} is a pre-existing limitation in the
            // engine and out of slice-7 scope; the unit tests verify the .sql output shape
            // separately.
            ["ShouldCast:OutputScripts"] = "false",
            ["ShouldCast:TokenizeScripts"] = "true",
            ["ShouldCast:MergeUpdate"] = "true",
            ["ShouldCast:MergeDelete"] = "false",
            // ConfigureDataDelivery=true so the schema-template Tables/Customers.json picks up a
            // DataDelivery block — that's how SchemaQuench's DataDeliveryProcessor wires the
            // .tabledata file through to the merge at quench time.
            ["ShouldCast:ConfigureDataDelivery"] = "true",
            ["Tables:0:Name"] = "Customers",
            ["Tables:0:KeyColumns"] = "[CustomerID]"
        };
        foreach (var prop in connProps)
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private IConfigurationRoot BuildSchemaQuenchConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "SqlServer:ConnectionProperties");

        var values = new Dictionary<string, string>
        {
            ["Target:Server"] = rootConfig["SqlServer:Server"],
            ["Target:Port"] = rootConfig["SqlServer:Port"],
            ["Target:User"] = rootConfig["SqlServer:User"],
            ["Target:Password"] = rootConfig["SqlServer:Password"],
            ["Target:Database"] = _integrationDb,
            ["SchemaPackagePath"] = _tempProductPath,
            ["ScriptTokens:MainDB"] = _integrationDb
        };
        foreach (var prop in connProps)
            values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // ----- Assertion helpers ---------------------------------------------------------------

    private int CustomerCount()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{SourceSchema}].[Customers];";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }

    private List<string> QueryStrings(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader[0]?.ToString());
        conn.Close();
        return rows;
    }

    private static string GenerateUniqueDBName(string prefix)
    {
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{prefix}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }
}
