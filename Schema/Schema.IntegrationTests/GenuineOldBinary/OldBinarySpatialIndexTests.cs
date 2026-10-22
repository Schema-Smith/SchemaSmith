// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.GenuineOldBinary;

// Spatial indexes on a genuine old binary, through the XML tier both full and index-only deploys use below 2016.
// The AUTO_GRID tessellations arrive in SQL Server 2012: below it a declared one is created with the GRID scheme of its
// family and recorded as a downgrade, and a redeploy leaves it alone; 'fail' refuses it. From 2012 it is created as
// declared. [Explicit]: run once per old instance, as the sibling fixtures are:
//   SmithySettings_SqlServer__Server=127.0.0.1 SmithySettings_SqlServer__Port=14330 \
//   SmithySettings_SqlServer__User=sa SmithySettings_SqlServer__Password='SchemaSmith!Old2026' \
//   dotnet test Schema/Schema.IntegrationTests --filter FullyQualifiedName~OldBinarySpatialIndexTests
[Explicit("Requires a genuine old SQL Server instance; run manually via the SmithySettings_SqlServer__* env vars.")]
[TestFixture]
public class OldBinarySpatialIndexTests
{
    private const string ProductName = "OldBinSpatial";

    private const string TableJson = """
        [{
            "Schema": "[dbo]",
            "Name": "[OldBinSpatial]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Shape]", "DataType": "GEOMETRY", "Nullable": true },
                { "Name": "[Location]", "DataType": "GEOGRAPHY", "Nullable": true }
            ],
            "Indexes": [ { "Name": "[PK_OldBinSpatial]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
            "SpatialIndexes": [
                { "Name": "[SX_Shape]", "Column": "[Shape]", "Tessellation": "GEOMETRY_AUTO_GRID", "BoundingBox": { "XMin": 0, "YMin": 0, "XMax": 500, "YMax": 200 } },
                { "Name": "[SX_Location]", "Column": "[Location]", "Tessellation": "GEOGRAPHY_GRID", "Grids": { "Level1": "LOW", "Level2": "LOW", "Level3": "HIGH", "Level4": "HIGH" }, "CellsPerObject": 32 }
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
    public void DeclaredSpatialIndexes_DeployAndConverge_WithAutoGridCreatedAsGridBelow2012()
    {
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            Run(cmd, "SchemaSmith.TableQuench", xml);
            var expectedShape = (_serverMajor < 11 ? "SX_Shape:GEOMETRY_GRID:16," : "SX_Shape:GEOMETRY_AUTO_GRID:8,") + "SX_Location:GEOGRAPHY_GRID:32,";
            Assert.Multiple(() =>
            {
                Assert.That(Shape(cmd), Is.EqualTo(expectedShape), $"created on SQL Server major {_serverMajor}");
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType LIKE 'spatial AUTO_GRID%'"), Is.EqualTo(_serverMajor < 11 ? 1 : 0),
                    "the scheme change is recorded as a downgrade, and only where it happens");
            });

            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Run(cmd, "SchemaSmith.IndexOnlyQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Count(cmd, "ActionType IN ('created', 'dropped')"), Is.Zero, "full and index-only redeploys leave the indexes alone");
                Assert.That(Shape(cmd), Is.EqualTo(expectedShape));
            });
        });
    }

    // CREATE SPATIAL INDEX takes DATA_COMPRESSION from 2012 on every edition (these are Express); 2008 R2 refuses it as
    // syntax. Below 2012 a declared compression is left off, with a downgrade, and a redeploy does not re-create the index.
    [Test]
    public void ACompressedSpatialIndex_IsCompressedFrom2012OnExpress_AndDeploysUncompressedBelow_AndConverges()
    {
        var xml = ModelXmlSerializer.ToIngestXml(TableJson.Replace("\"CellsPerObject\": 32", "\"CellsPerObject\": 32, \"CompressionType\": \"PAGE\""), "Tables", "Table");
        WithKindledDatabase("warn", cmd =>
        {
            var lacksCompression = _serverMajor < 11;
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Assert.Multiple(() =>
            {
                Assert.That(Compression(cmd, "SX_Location"), Is.EqualTo(lacksCompression ? "NONE" : "PAGE"), $"major {_serverMajor}");
                Assert.That(Count(cmd, "ActionType = 'downgraded' AND ObjectType = '" + "spatial index compression (SQL Server 2012)" + "' AND ObjectName LIKE '%SX_Location%'"), Is.EqualTo(lacksCompression ? 1 : 0));
            });

            Clear(cmd);
            Run(cmd, "SchemaSmith.TableQuench", xml);
            Run(cmd, "SchemaSmith.IndexOnlyQuench", xml);
            Assert.That(Count(cmd, "ActionType IN ('created', 'dropped')"), Is.Zero, "full and index-only redeploys leave the index alone");
        });
    }

    private static string Compression(IDbCommand cmd, string index)
    {
        cmd.CommandText = "SELECT MAX(p.data_compression_desc) FROM sys.indexes i JOIN sys.internal_tables it ON it.parent_object_id = i.[object_id] AND it.parent_minor_id = i.index_id "
                        + $"JOIN sys.partitions p ON p.[object_id] = it.[object_id] AND p.index_id = 1 WHERE i.[object_id] = OBJECT_ID('dbo.OldBinSpatial') AND i.name = '{index}'";
        return cmd.ExecuteScalar() as string ?? "(no index)";
    }

    [Test]
    public void AnAutoGridSpatialIndex_IsRefusedBelow2012_UnderTheFailPolicy()
    {
        if (_serverMajor >= 11)
            Assert.Ignore($"Detected SQL Server major {_serverMajor}; the AUTO_GRID tessellations exist from 2012.");
        var xml = ModelXmlSerializer.ToIngestXml(TableJson, "Tables", "Table");
        WithKindledDatabase("fail", cmd =>
        {
            var ex = Assert.Catch(() => Run(cmd, "SchemaSmith.TableQuench", xml));
            Assert.That(ex!.Message, Does.Contain("AUTO_GRID spatial tessellations require SQL Server 2012").And.Contain("[SX_Shape]"));
        });
    }

    private void WithKindledDatabase(string policy, Action<IDbCommand> body)
    {
        var db = $"OldBinSpatial_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
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

    private static string Shape(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT i.name COLLATE DATABASE_DEFAULT + ':' + i.tessellation_scheme COLLATE DATABASE_DEFAULT + ':' + CONVERT(VARCHAR(10), tz.cells_per_object) + ',' "
                        + "FROM sys.spatial_indexes i JOIN sys.spatial_index_tessellations tz ON tz.[object_id] = i.[object_id] AND tz.index_id = i.index_id "
                        + "WHERE i.[object_id] = OBJECT_ID('dbo.OldBinSpatial') ORDER BY i.name DESC FOR XML PATH('')";
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
