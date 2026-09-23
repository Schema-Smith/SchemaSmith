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

namespace DataTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// PostgreSQL mirror of the slice-7 schema-template round-trip integration test (design §8.6,
/// plan Step 7.2). Mirrors <c>DataTongs.IntegrationTests.SqlServer.SchemaTemplateRoundTripTests</c>
/// with PostgreSQL idioms (double-quoted identifiers, lowercase names, plain SQL exec).
/// </summary>
[TestFixture]
[Category("PostgreSQL")]
[Category("Integration")]
public class SchemaTemplateRoundTripTests
{
    private const string SourceSchema = "tenant_seed";
    private const string TemplateName = "TenantBody";
    private const string ProductName = "DataTongsRoundTripProductPg";

    private string _integrationDb = "";
    private string _connectionString;
    private string _tempProductPath;
    private string _server;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _server = config["PostgreSQL:Server"];
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, _server, "postgres",
            config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("dt_roundtrip");

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
            $"DataTongsRoundTripPg_{Guid.NewGuid():N}");
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
                var dataTongs = new global::DataTongs.DataTongs(Platform.PostgreSQL);
                dataTongs.CastData();

                var tableDataDir = Path.Combine(_tempProductPath, "Templates", TemplateName, "Table Data");
                var contentFile = Path.Combine(tableDataDir, "customers.tabledata");
                Assert.That(File.Exists(contentFile), Is.True,
                    "DataTongs must write an unqualified customers.tabledata file in schema-template mode.");

                // DataDelivery block was added to the existing Tables/customers.json.
                var tableJsonPath = Path.Combine(_tempProductPath, "Templates", TemplateName, "Tables", "customers.json");
                var tableJson = File.ReadAllText(tableJsonPath);
                Assert.That(tableJson, Does.Contain("\"DataDelivery\""),
                    "ConfigureDataDelivery must add a DataDelivery block to the schema-template Table.json.");
                Assert.That(tableJson, Does.Not.Contain("\"Schema\":"),
                    "ConfigureDataDelivery must not add a Schema field to a schema-template Table.json.");
                Assert.That(tableJson, Does.Contain("Table Data/customers.tabledata"),
                    "DataDelivery.ContentFile must reference the unqualified tabledata path.");

                // ----- Phase 2: truncate the source rows -----
                TruncateCustomers();
                Assert.That(CustomerCount(), Is.EqualTo(0),
                    "Sanity: customers must be empty before SchemaQuench restores it.");

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
                    $"SELECT code FROM \"{SourceSchema}\".customers ORDER BY customer_id");
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{_integrationDb}\";";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        cmd.CommandText = $@"
CREATE SCHEMA ""{SourceSchema}"";
CREATE TABLE ""{SourceSchema}"".customers (
    customer_id INT NOT NULL CONSTRAINT pk_customers_slice7 PRIMARY KEY,
    code VARCHAR(20) NOT NULL,
    name VARCHAR(100) NOT NULL
);
INSERT INTO ""{SourceSchema}"".customers (customer_id, code, name) VALUES
    (1, 'C001', 'Customer One'),
    (2, 'C002', 'Customer Two'),
    (3, 'C003', 'Customer Three');";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void TruncateCustomers()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{SourceSchema}\".customers;";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTestDatabase()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Terminate connections first, give the server a moment, then drop. Splitting the
            // statements lets DROP DATABASE see a clean session list — bundling them in one
            // batch can race when the terminate hasn't propagated yet.
            cmd.CommandText = $@"
SELECT pg_terminate_backend(pid)
  FROM pg_stat_activity
  WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();

            cmd.CommandText = $@"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $@"DROP DATABASE IF EXISTS ""{_integrationDb}"";";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch
        {
            // Best-effort cleanup — leftover DB is not a test failure on the PG fixture (the
            // container is ephemeral). Suppress so the cleanup error doesn't mask the passing
            // test's verdict.
        }
    }

    // ----- Package fixture -----------------------------------------------------------------

    private static void BuildMinimalSchemaTemplatePackage(string root)
    {
        var templateDir = Path.Combine(root, "Templates", TemplateName);
        Directory.CreateDirectory(Path.Combine(templateDir, "Tables"));
        Directory.CreateDirectory(Path.Combine(templateDir, "Table Data"));

        File.WriteAllText(Path.Combine(root, "Product.json"),
            $"{{\"Name\":\"{ProductName}\",\"Platform\":\"PostgreSQL\",\"TemplateOrder\":[\"{TemplateName}\"],\"ScriptTokens\":{{\"MainDB\":\"TestMain\"}},\"ScriptFolders\":[]}}");

        File.WriteAllText(Path.Combine(templateDir, "Template.json"),
            "{\n" +
            "  \"DatabaseIdentificationScript\": \"SELECT datname FROM pg_database WHERE datname = '{{MainDB}}'\",\n" +
            $"  \"SchemaIdentificationScript\": \"SELECT '{SourceSchema}' AS SchemaName\",\n" +
            "  \"CreateSchemaIfMissing\": false,\n" +
            "  \"AllowParallel\": false\n" +
            "}");

        File.WriteAllText(Path.Combine(templateDir, "Tables", "customers.json"),
            "{\n" +
            "  \"Name\": \"customers\",\n" +
            "  \"Columns\": [\n" +
            "    { \"Name\": \"customer_id\", \"DataType\": \"INTEGER\" },\n" +
            "    { \"Name\": \"code\", \"DataType\": \"VARCHAR(20)\" },\n" +
            "    { \"Name\": \"name\", \"DataType\": \"VARCHAR(100)\" }\n" +
            "  ],\n" +
            "  \"Indexes\": [\n" +
            "    { \"Name\": \"pk_customers_slice7\", \"PrimaryKey\": true, \"IndexColumns\": \"customer_id\" }\n" +
            "  ]\n" +
            "}");
    }

    // ----- Configs --------------------------------------------------------------------------

    private IConfigurationRoot BuildDataTongsConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "PostgreSQL:ConnectionProperties");
        var tableDataDir = Path.Combine(_tempProductPath, "Templates", TemplateName, "Table Data");

        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = rootConfig["PostgreSQL:Server"],
            ["Source:Port"] = rootConfig["PostgreSQL:Port"],
            ["Source:User"] = rootConfig["PostgreSQL:User"],
            ["Source:Password"] = rootConfig["PostgreSQL:Password"],
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
            ["ShouldCast:ConfigureDataDelivery"] = "true",
            ["Tables:0:Name"] = "customers",
            ["Tables:0:KeyColumns"] = "\"customer_id\""
        };
        foreach (var prop in connProps)
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private IConfigurationRoot BuildSchemaQuenchConfig()
    {
        var rootConfig = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(rootConfig, "PostgreSQL:ConnectionProperties");

        var values = new Dictionary<string, string>
        {
            ["Target:Server"] = rootConfig["PostgreSQL:Server"],
            ["Target:Port"] = rootConfig["PostgreSQL:Port"],
            ["Target:User"] = rootConfig["PostgreSQL:User"],
            ["Target:Password"] = rootConfig["PostgreSQL:Password"],
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
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_integrationDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{SourceSchema}\".customers;";
        var result = cmd.ExecuteScalar();
        conn.Close();
        return Convert.ToInt32(result);
    }

    private List<string> QueryStrings(string sql)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
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
        return $"{prefix}_test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }
}
