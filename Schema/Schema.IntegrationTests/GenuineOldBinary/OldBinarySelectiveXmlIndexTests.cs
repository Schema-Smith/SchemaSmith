// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Selective XML indexes (SQL Server 2012 SP1) on a genuine older binary, through the XML ingest tier those servers use.
// From 2012 SP1 they deploy, an unchanged redeploy and a redeploy of what extraction returns leave them alone; below it
// the selective index and its secondary are skipped and each is recorded as a downgrade. [Explicit]: run once per old
// instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinarySelectiveXmlIndexTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinarySelectiveXmlIndexTests
{
    private const string ProductName = "OldBinSelectiveXml";

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[OldBinSelectiveXml]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Doc]", "DataType": "XML", "Nullable": true }
            ],
            "Indexes": [
                { "Name": "[PK_OldBinSelectiveXml]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" }
            ],
            "XmlIndexes": [
                { "Name": "[SXI_OldBinSelectiveXml]", "Column": "[Doc]", "Selective": {
                    "Namespaces": [ { "Prefix": "o", "Uri": "http://ex/o" } ],
                    "Paths": [
                        { "Name": "pId", "Path": "/o:order/o:id" },
                        { "Name": "pName", "Path": "/o:order/o:name", "Type": "xs:string", "MaxLength": 20, "SingletonCardinality": true },
                        { "Name": "pTotal", "Path": "/o:order/o:total", "Type": "decimal(10, 2)", "SingletonCardinality": true }
                    ] } },
                { "Name": "[SXI2_OldBinSelectiveXml]", "Column": "[Doc]", "PrimaryIndex": "[SXI_OldBinSelectiveXml]", "ForPath": "pTotal" }
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
    public void ASelectiveXmlIndex_DeploysAndRoundTrips_OrIsSkippedWithADowngrade()
    {
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            var supported = Scalar(cmd, "SELECT CASE WHEN COL_LENGTH('sys.xml_indexes', 'xml_index_type') IS NULL THEN 0 ELSE 1 END") == 1;
            // SQL Server 2012 ships the feature off in each database: first the skip, then the deploy once it is turned on.
            if (supported && _serverMajor == 11)
            {
                Run(cmd, "SchemaSmith.TableQuench", xml);
                Assert.Multiple(() =>
                {
                    Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'selective XML index (SQL Server 2012 SP1)'"),
                        Is.EqualTo(2), "on 2012, a database with the feature off skips both and records each");
                    Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.xml_indexes WHERE [object_id] = OBJECT_ID('dbo.OldBinSelectiveXml')"), Is.Zero);
                });
                cmd.CommandText = "DECLARE @db SYSNAME = DB_NAME(); EXEC sys.sp_db_selective_xml_index @db, 'true'";
                cmd.ExecuteNonQuery();
                Clear(cmd);
            }
            Run(cmd, "SchemaSmith.TableQuench", xml);
            if (!supported)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'selective XML index (SQL Server 2012 SP1)'"),
                        Is.EqualTo(2), "the selective index and its secondary are each recorded");
                    Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.xml_indexes WHERE [object_id] = OBJECT_ID('dbo.OldBinSelectiveXml')"),
                        Is.Zero, "and neither is created");
                    Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.tables WHERE [name] = 'OldBinSelectiveXml'"), Is.EqualTo(1), "the table deploys");
                });
                return;
            }

            Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.selective_xml_index_paths WHERE [object_id] = OBJECT_ID('dbo.OldBinSelectiveXml')"),
                Is.EqualTo(3), "the selective index is created with its paths");
            Assert.That(Count(cmd, "ActionType = 'created' AND ObjectType = 'xmlIndex'"), Is.EqualTo(2), "and its secondary");
            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.That(Count(cmd, "ActionType IN ('created', 'dropped', 'modified')"), Is.Zero, "an unchanged redeploy leaves them alone");

            cmd.CommandText = "EXEC SchemaSmith.GenerateTableXml @p_Schema = 'dbo', @p_Table = 'OldBinSelectiveXml'";
            var extracted = new StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) extracted.Append(reader.GetValue(0));
            var table = PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(extracted.ToString()), Platform.SqlServer);
            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", ModelXmlSerializer.ToIngestXml("[" + JsonHelper.Serialize(table) + "]", "Tables", "Table"));
            Assert.That(Count(cmd, "ActionType IN ('created', 'dropped', 'modified')"), Is.Zero, "and so does a redeploy of what extraction returns");
        });
    }

    private static int Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void WithKindledDatabase(string policy, Action<IDbCommand> body)
    {
        var db = $"OldBinSelectiveXml_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
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
