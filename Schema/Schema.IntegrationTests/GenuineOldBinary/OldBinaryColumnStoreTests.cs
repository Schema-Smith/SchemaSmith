// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Columnstore declarations on a genuine SQL Server 2016, through the XML tier. A clustered columnstore over a (max)
// column is refused by the engine below 2017 (35343), so it is skipped and recorded as a downgrade; an ordered one
// (ORDER, 2022) is created unordered and recorded; and neither declaration rebuilds on a redeploy, full or index-only.
// [Explicit]: run against the 2016 instance, as the sibling fixtures are:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinaryColumnStoreTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryColumnStoreTests
{
    private const string ProductName = "OldBinColumnStore";

    private const string TableJson = """
        [{
            "Schema": "[dbo]", "Name": "[OldBinCsMax]",
            "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Note]", "DataType": "NVARCHAR(MAX)", "Nullable": true } ],
            "Indexes": [ { "Name": "[CCI_OldBinCsMax]", "Clustered": true, "ColumnStore": true } ]
        },
        {
            "Schema": "[dbo]", "Name": "[OldBinCsOrder]",
            "Columns": [ { "Name": "[A]", "DataType": "INT", "Nullable": false }, { "Name": "[B]", "DataType": "INT", "Nullable": false } ],
            "Indexes": [ { "Name": "[CCI_OldBinCsOrder]", "Clustered": true, "ColumnStore": true, "Order": "[B],[A]" } ]
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
    public void OnSql2016_AMaxColumnCciIsSkipped_AnOrderedOneIsCreatedUnordered_AndARedeployLeavesBoth()
    {
        if (_serverMajor != 13) Assert.Ignore($"Detected SQL Server major {_serverMajor}; this checks the 2016 gates.");
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'columnstore over a (max) or computed column (SQL Server 2017)'"), Is.EqualTo(1));
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'columnstore index without its ORDER'"), Is.EqualTo(1));
                Assert.That(Columnstores(cmd), Is.EqualTo("CCI_OldBinCsOrder,"), "only the table without a (max) column has its columnstore");
            });

            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Run(cmd, "SchemaSmith.IndexOnlyQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType IN ('created', 'dropped')"), Is.Zero, "full and index-only redeploys leave the index alone");
                Assert.That(Columnstores(cmd), Is.EqualTo("CCI_OldBinCsOrder,"));
            });
        });
    }

    private void WithKindledDatabase(string policy, Action<IDbCommand> body)
    {
        var db = $"OldBinColumnStore_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
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

    private static string Columnstores(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT i.name + ',' FROM sys.indexes i WHERE i.[type] = 5 AND OBJECT_SCHEMA_NAME(i.[object_id]) = 'dbo' ORDER BY i.name FOR XML PATH('')";
        return cmd.ExecuteScalar() as string ?? "";
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
