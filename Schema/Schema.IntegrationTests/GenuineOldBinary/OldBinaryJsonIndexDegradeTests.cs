// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// JSON indexes (SQL Server 2025) on a genuine older binary, through the XML ingest tier those servers use: a declared one is
// skipped with a downgrade and the table deploys, through both the full and the index-only path; under 'fail' the deploy
// is refused. The index is declared on an nvarchar column because these servers have no json type. [Explicit]: run once
// per old instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinaryJsonIndexDegradeTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryJsonIndexDegradeTests
{
    private const string ProductName = "OldBinJsonIndex";

    private static string TableJson(bool withIndex) => """
        [{
            "Schema": "[dbo]",
            "Name": "[OldBinJsonIndex]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Doc]", "DataType": "NVARCHAR(MAX)", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "[PK_OldBinJsonIndex]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
            ]
        """ + (withIndex ? """
            , "JsonIndexes": [ { "Name": "[JX_OldBinJsonIndex]", "Column": "[Doc]", "Paths": [ "$.a", "$.b" ] } ]
        """ : "") + "}]";

    private string _masterConnectionString = "";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _connProps = new();
    private int _serverMajor;
    private readonly List<string> _createdDbs = [];

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _server = config["SqlServer:Server"] ?? "127.0.0.1";
        _user = config["SqlServer:User"];
        _password = config["SqlServer:Password"];
        _port = config["SqlServer:Port"];
        _connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, _server, "master", _user, _password, _port, _connProps);

        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        _serverMajor = TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable;
        conn.Close();
    }

    [TestCase("SchemaSmith.TableQuench")]
    [TestCase("SchemaSmith.IndexOnlyQuench")]
    public void ADeclaredJsonIndex_IsSkippedWithADowngrade_AndTheTableDeploys(string proc)
    {
        WithKindledDatabase("warn", cmd =>
        {
            if (proc != "SchemaSmith.TableQuench") Run(cmd, "SchemaSmith.TableQuench", ModelXmlSerializer.ToIngestXml(TableJson(false), "Tables", "Table"));
            Run(cmd, proc, ModelXmlSerializer.ToIngestXml(TableJson(true), "Tables", "Table"));
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'JSON index (SQL Server 2025)'"), Is.EqualTo(1), "the JSON index is recorded as a downgrade");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.OldBinJsonIndex') AND [name] = 'JX_OldBinJsonIndex'"),
                    Is.Zero, "and not created");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'OldBinJsonIndex'"), Is.EqualTo(1), "the table deploys");
            });
        });
    }

    [Test]
    public void UnderFail_ADeclaredJsonIndex_IsRefusedByName()
    {
        WithKindledDatabase("fail", cmd =>
        {
            var refused = Assert.Catch(() => Run(cmd, "SchemaSmith.TableQuench", ModelXmlSerializer.ToIngestXml(TableJson(true), "Tables", "Table")));
            Assert.That(refused?.Message, Does.Contain("JSON indexes require SQL Server 2025").And.Contain("JX_OldBinJsonIndex"));
        });
    }

    private static int Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void WithKindledDatabase(string policy, Action<IDbCommand> body)
    {
        var db = $"OldBinJsonIndex_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using (var master = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100;";
            create.ExecuteNonQuery();
        }
        _createdDbs.Add(db);
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(
            ConnectionString.Build(Platform.SqlServer, _server, db, _user, _password, _port, _connProps));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml, _serverMajor, policy);
        body(cmd);
    }

    private static void Run(IDbCommand cmd, string proc, string tableXml)
    {
        cmd.CommandText = proc;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Clear();
        foreach (var (name, value) in new[] { ("@ProductName", ProductName), ("@TableDefinitions", tableXml) })
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        try { cmd.ExecuteNonQuery(); }
        finally
        {
            cmd.Parameters.Clear();
            cmd.CommandType = CommandType.Text;
        }
    }

    private static int Count(IDbCommand cmd, string where)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND {where}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in _createdDbs)
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        conn.Close();
    }
}
