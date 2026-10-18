// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// XML_COMPRESSION (SQL Server 2022) on a table, an index and both kinds of XML index, deployed to an older genuine
// binary: everything is created uncompressed, and each suppressed setting is recorded as a downgrade. [Explicit]: run
// once per old instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 //   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' //   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinaryXmlCompressionDegradeTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryXmlCompressionDegradeTests
{
    private const string ProductName = "OldBinXmlCompression";

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[OldBinXmlCompression]",
            "XmlCompression": true,
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[V]", "DataType": "NVARCHAR(50)", "Nullable": true },
                { "Name": "[Doc]", "DataType": "XML", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "[PK_OldBinXmlCompression]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                { "Name": "[IX_OldBinXmlCompression]", "IndexColumns": "[V]", "XmlCompression": true }
            ],
            "XmlIndexes": [
                { "Name": "[PXML_OldBinXmlCompression]", "IsPrimary": true, "Column": "[Doc]", "XmlCompression": true },
                { "Name": "[SXML_OldBinXmlCompression]", "IsPrimary": false, "Column": "[Doc]", "PrimaryIndex": "[PXML_OldBinXmlCompression]",
                  "SecondaryIndexType": "VALUE", "XmlCompression": true }
            ]
        }]
        """;

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

    [Test]
    public void XmlCompressionEverywhere_DeploysUncompressed_AndRecordsEachDowngrade()
    {
        if (_serverMajor >= 16)
            Assert.Ignore($"Detected SQL Server major {_serverMajor}; XML_COMPRESSION exists from 2022.");
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'XML compression (SQL Server 2022)' AND ObjectName = '[dbo].[OldBinXmlCompression]'"),
                    Is.EqualTo(1), "the table's setting is recorded");
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'XML compression (SQL Server 2022)' AND ObjectName LIKE '%IX[_]OldBinXmlCompression%'"),
                    Is.EqualTo(1), "the index's setting is recorded");
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'XML compression (SQL Server 2022)' AND ObjectName LIKE '%XML[_]OldBinXmlCompression%'"),
                    Is.EqualTo(2), "each XML index's setting is recorded");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.xml_indexes WHERE [object_id] = OBJECT_ID('dbo.OldBinXmlCompression')"),
                    Is.EqualTo(2), "both XML indexes are created, without the clause");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.OldBinXmlCompression') AND [name] = 'IX_OldBinXmlCompression'"),
                    Is.EqualTo(1), "and so is the index");
            });
            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.That(Count(cmd, "ActionType IN ('created', 'dropped', 'modified')"), Is.Zero, "and a redeploy leaves it alone");
        });
    }

    private static int Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void WithKindledDatabase(string policy, Action<IDbCommand> body)
    {
        var db = $"OldBinXmlCompression_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
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

    private static void Clear(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
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
