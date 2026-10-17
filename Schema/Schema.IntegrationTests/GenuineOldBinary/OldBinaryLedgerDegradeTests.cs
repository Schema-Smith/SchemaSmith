// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// A ledger table (SQL Server 2022) on an older genuine binary deploys as an ordinary table with a downgrade recorded.
// Its declared generated columns and ledger view exist only on a ledger table, so they go with it: the table is created
// without them rather than failing on GENERATED ALWAYS AS TRANSACTION_ID. [Explicit]: run once per old instance:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 //   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' //   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinaryLedgerDegradeTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinaryLedgerDegradeTests
{
    private const string ProductName = "OldBinLedger";

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[OldBinLedger]",
            "Ledger": "Updatable",
            "LedgerView": { "Name": "[OldBinLedgerView]" },
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[TxStart]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "TransactionIdStart", "Hidden": true }
            ],
            "Indexes": [ { "Name": "[PK_OldBinLedger]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ]
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
    public void ALedgerTableWithDeclaredNames_DeploysAsAnOrdinaryTable_WithoutItsGeneratedColumns()
    {
        if (_serverMajor >= 16)
            Assert.Ignore($"Detected SQL Server major {_serverMajor}; ledger tables exist from 2022.");
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = 'ledger table (SQL Server 2022)'"), Is.EqualTo(1), "recorded as a downgrade");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID('dbo.OldBinLedger')"), Is.EqualTo(1), "only Id: the ledger column went with the ledger");
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
        var db = $"OldBinLedger_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
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
