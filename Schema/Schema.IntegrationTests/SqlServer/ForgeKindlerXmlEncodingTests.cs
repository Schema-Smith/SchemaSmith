// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

// The KEY FINDING behind the SQL Server 2008 floor: CREATE PROCEDURE ... OPENJSON parse-errors at
// compatibility level 100, so the stock (JSON) kindle cannot install its helpers there. The legacy (XML)
// encoding swaps every OPENJSON/FOR JSON proc for a .nodes()/FOR XML PATH twin so the whole helper set
// CREATEs below the cliff. This fixture proves both halves on a real compat-100 database.
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class ForgeKindlerXmlEncodingTests
{
    private string _masterConnectionString = "";
    private string _server = "", _user = "", _password = "", _port = "";
    private Dictionary<string, string> _connProps = new();
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
    }

    [Test]
    public void FnSplitList_CreatesAndSplitsInOrder_AtCompatibilityLevel100()
    {
        var db = CreateCompat100Database("SplitList100");
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "IF SCHEMA_ID('SchemaSmith') IS NULL EXEC('CREATE SCHEMA SchemaSmith')";
        cmd.ExecuteNonQuery();
        // fn_SplitList must CREATE at compat 100 (STRING_SPLIT itself would not exist here).
        Assert.DoesNotThrow(() => ForgeKindler.KindleOneFile(cmd, "SchemaSmith.fn_SplitList.sql", Platform.SqlServer),
            "fn_SplitList must CREATE at compatibility level 100");

        // Splits in input order (Ordinal), including an embedded-space token and an empty trailing token.
        cmd.CommandText = @"SELECT STUFF((SELECT '|' + [value] FROM SchemaSmith.fn_SplitList('[Id],[Name] DESC,[Amount],', ',')
                                          ORDER BY [Ordinal] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Id]|[Name] DESC|[Amount]|"));

        conn.Close();
    }

    [Test]
    public void KindleTheForge_XmlEncoding_SucceedsAtCompatibilityLevel100()
    {
        var db = CreateCompat100Database("KindleXml100");
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Sanity: the database really is at compat 100 (where OPENJSON would parse-error).
        cmd.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(100), "database must be at compatibility level 100");

        // The whole helper set must kindle without an OPENJSON parse error.
        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml));

        Assert.Multiple(() =>
        {
            // The metadata tables were bootstrapped (BootstrapTableXmlQuench ran, OPENJSON-free).
            Assert.That(ObjectExists(cmd, "SchemaSmith.ChangeAudit", "U"), Is.True, "ChangeAudit table");
            Assert.That(ObjectExists(cmd, "SchemaSmith.CompletedMigrationScripts", "U"), Is.True, "CompletedMigrationScripts table");
            // TableQuench CREATEd with the XML parse inlined — the proc OPENJSON blocked at compat 100.
            Assert.That(ObjectExists(cmd, "SchemaSmith.TableQuench", "P"), Is.True, "TableQuench proc");
            // The XML compare/extraction twins were kindled instead of the JSON versions.
            Assert.That(ObjectExists(cmd, "SchemaSmith.GenerateTableXml", "P"), Is.True, "GenerateTableXml proc");
            Assert.That(ObjectExists(cmd, "SchemaSmith.GenerateIndexedViewXml", "FN"), Is.True, "GenerateIndexedViewXml function");
            // The JSON-only helpers were NOT kindled on the legacy encoding.
            Assert.That(ObjectExists(cmd, "SchemaSmith.fn_FormatJson", "FN"), Is.False, "fn_FormatJson must be skipped");
            Assert.That(ObjectExists(cmd, "SchemaSmith.GenerateTableJSON", "P"), Is.False, "JSON GenerateTableJSON must not be kindled");
        });

        conn.Close();
    }

    [Test]
    public void KindleTheForge_JsonEncoding_FailsAtCompatibilityLevel100()
    {
        // The negative control that makes the XML path necessary: the stock JSON kindle installs procs with
        // OPENJSON, which is a parse error at compat 100, so kindling must fail.
        var db = CreateCompat100Database("KindleJson100");
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();

        var ex = Assert.Throws<Exception>(() =>
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Json));
        Assert.That(ex!.Message + ex.InnerException?.Message, Does.Contain("kindling").IgnoreCase);

        conn.Close();
    }

    [Test]
    public void GenerateTableXml_RoundTripsObjectExtendedProperties_AtCompatibilityLevel100()
    {
        // B2: the legacy-tier extract must preserve object ExtendedProperties. EP names are arbitrary
        // sysname (a name with a space cannot be an XML element name), so the proc emits them attribute-
        // encoded and FromIngestXml rebuilds the dict. Prove the round trip on a real compat-100 database.
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = @"
CREATE TABLE dbo.WidgetEP ([Id] INT NOT NULL CONSTRAINT PK_WidgetEP PRIMARY KEY, [Amount] DECIMAL(10,2) NULL);
CREATE INDEX IX_WidgetEP_Amount ON dbo.WidgetEP ([Amount]);
EXEC sys.sp_addextendedproperty @name=N'OwningTeam', @value=N'Billing', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WidgetEP';
EXEC sys.sp_addextendedproperty @name=N'My Note', @value=N'a & b < c', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WidgetEP';
EXEC sys.sp_addextendedproperty @name=N'Classification', @value=N'Financial', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WidgetEP', @level2type=N'COLUMN', @level2name=N'Amount';
EXEC sys.sp_addextendedproperty @name=N'IdxNote', @value=N'hot', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WidgetEP', @level2type=N'INDEX', @level2name=N'IX_WidgetEP_Amount';";
            cmd.ExecuteNonQuery();

            // FOR XML PATH (no TYPE) returns the document in 2033-char chunks across rows; concatenate them
            // (ExecuteScalar would read only the first chunk and truncate a larger document).
            cmd.CommandText = "EXEC SchemaSmith.GenerateTableXml @p_Schema='dbo', @p_Table='WidgetEP'";
            var sb = new System.Text.StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    sb.Append(reader.GetValue(0)?.ToString());
            var xml = sb.ToString();
            Assert.That(xml, Does.Contain("</Table>"), "GenerateTableXml must return a complete table document");

            var table = PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(xml), Platform.SqlServer);

            static JToken Eps(DynamicBase o) => ((JObject)o.Extensions)?["ExtendedProperties"];
            Assert.Multiple(() =>
            {
                var tableEps = Eps(table);
                Assert.That((string)tableEps["OwningTeam"], Is.EqualTo("Billing"), "table EP");
                Assert.That((string)tableEps["My Note"], Is.EqualTo("a & b < c"), "table EP with a spaced name + special-char value");
                Assert.That((string)Eps(table.Columns.Single(c => c.Name == "[Amount]"))["Classification"], Is.EqualTo("Financial"), "column EP");
                Assert.That((string)Eps(table.Indexes.Single(i => i.Name == "[IX_WidgetEP_Amount]"))["IdxNote"], Is.EqualTo("hot"), "index EP");
            });
        }
        finally
        {
            DropTables(cmd, "dbo.WidgetEP");
        }

        conn.Close();
    }

    [Test]
    public void BootstrapTableXmlQuench_ColumnRename_AtCompatibilityLevel100_DataSurvives()
    {
        // The XML twin is easy to miss: it is a separate, hand-maintained copy of
        // BootstrapTableQuench's logic (OPENJSON-free), and a rename left out of it would be invisible
        // on a modern server (JSON encoding masks it) and silently wrong only below the compat cliff.
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = "CREATE TABLE dbo.XmlColRenameTest ([Id] INT IDENTITY(1,1) PRIMARY KEY, [OldCol] VARCHAR(50) NOT NULL DEFAULT '0')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO dbo.XmlColRenameTest ([OldCol]) VALUES ('distinguishing-value')";
            cmd.ExecuteNonQuery();

            var json = "{\"Schema\": \"[dbo]\", \"Name\": \"[XmlColRenameTest]\","
                + "\"Columns\": ["
                + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false},"
                + "{\"Name\": \"[Value]\", \"DataType\": \"VARCHAR(50)\", \"Nullable\": false, \"Default\": \"'0'\", \"OldName\": \"[OldCol]\"}"
                + "],"
                + "\"Indexes\": [{\"Name\": \"[PK_XmlColRenameTest]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"}]"
                + "}";
            var xml = ModelXmlSerializer.ToIngestXmlObject(json, "Table");
            cmd.CommandText = $"DECLARE @x XML = N'{xml.Replace("'", "''")}'; EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = @x;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.XmlColRenameTest') AND name = 'Value'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "Renamed column must exist under the new name.");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.XmlColRenameTest') AND name = 'OldCol'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "Old column name must be gone after rename.");

            cmd.CommandText = "SELECT [Value] FROM dbo.XmlColRenameTest WHERE [Id] = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("distinguishing-value"),
                "The pre-existing row's value must survive the rename under the XML-ingest twin, not read back as the column's DEFAULT.");
        }
        finally
        {
            DropTables(cmd, "dbo.XmlColRenameTest");
        }

        conn.Close();
    }

    [Test]
    public void BootstrapTableXmlQuench_TableRename_AtCompatibilityLevel100_DataSurvives()
    {
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            cmd.CommandText = "CREATE TABLE dbo.XmlTblRenameTestOld ([Id] INT IDENTITY(1,1) PRIMARY KEY, [Value] VARCHAR(50) NOT NULL DEFAULT '0')";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO dbo.XmlTblRenameTestOld ([Value]) VALUES ('distinguishing-value')";
            cmd.ExecuteNonQuery();

            var json = "{\"Schema\": \"[dbo]\", \"Name\": \"[XmlTblRenameTest]\", \"OldName\": \"[XmlTblRenameTestOld]\","
                + "\"Columns\": ["
                + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false},"
                + "{\"Name\": \"[Value]\", \"DataType\": \"VARCHAR(50)\", \"Nullable\": false, \"Default\": \"'0'\"}"
                + "],"
                + "\"Indexes\": [{\"Name\": \"[PK_XmlTblRenameTest]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"}]"
                + "}";
            var xml = ModelXmlSerializer.ToIngestXmlObject(json, "Table");
            cmd.CommandText = $"DECLARE @x XML = N'{xml.Replace("'", "''")}'; EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = @x;";
            cmd.ExecuteNonQuery();

            Assert.That(ObjectExists(cmd, "dbo.XmlTblRenameTest", "U"), Is.True, "Renamed table must exist under the new name.");
            Assert.That(ObjectExists(cmd, "dbo.XmlTblRenameTestOld", "U"), Is.False, "Old table name must be gone after rename.");

            cmd.CommandText = "SELECT [Value] FROM dbo.XmlTblRenameTest WHERE [Id] = 1";
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("distinguishing-value"),
                "The old table's row must survive the rename under the XML-ingest twin, not be orphaned under an empty new table.");
        }
        finally
        {
            DropTables(cmd, "dbo.XmlTblRenameTest", "dbo.XmlTblRenameTestOld");
        }

        conn.Close();
    }

    // Tests that only need "a compat-100 database the XML kindle has run in" share one, created and kindled on
    // first use (same arguments the per-test code used) and dropped with the rest in OneTimeTearDown. Each such
    // test owns uniquely named objects and drops them itself. The fresh-kindle tests above keep their own.
    private string _sharedCompat100XmlDb;

    private string SharedCompat100XmlDb()
    {
        if (_sharedCompat100XmlDb != null) return _sharedCompat100XmlDb;
        var db = CreateCompat100Database("SharedXml100");
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml);
        conn.Close();
        return _sharedCompat100XmlDb = db;
    }

    private static void DropTables(IDbCommand cmd, params string[] tables)
    {
        foreach (var table in tables)
        {
            cmd.CommandText = $"IF OBJECT_ID('{table}', 'U') IS NOT NULL DROP TABLE {table}";
            try { cmd.ExecuteNonQuery(); }
            catch (System.Data.Common.DbException) { /* cleanup must not mask the test's own failure */ }
        }
    }

    private static bool ObjectExists(IDbCommand cmd, string name, string type)
    {
        cmd.CommandText = $"SELECT CASE WHEN OBJECT_ID('{name}', '{type}') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private string CreateCompat100Database(string prefix)
    {
        var db = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100;";
        cmd.ExecuteNonQuery();
        conn.Close();
        _createdDbs.Add(db);
        return db;
    }

    private string DbConnectionString(string db) =>
        ConnectionString.Build(Platform.SqlServer, _server, db, _user, _password, _port, _connProps);

    [OneTimeTearDown]
    public void TearDown()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var db in _createdDbs)
        {
            TestDatabaseDrop.Drop(cmd, db);
        }
        conn.Close();
    }

    [Test]
    public void SpatialIndexes_OnTheXmlTier_Deploy_Redeploy_ExtractAndConverge_ThroughBothTheFullAndIndexOnlyPaths()
    {
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        const string geometry = "{\"Name\": \"[SX_Shape]\", \"Column\": \"[Shape]\", \"Tessellation\": \"GEOMETRY_GRID\", \"BoundingBox\": {\"XMin\": 0, \"YMin\": 0, \"XMax\": 500, \"YMax\": 200}, "
                              + "\"Grids\": {\"Level1\": \"MEDIUM\", \"Level2\": \"LOW\", \"Level3\": \"HIGH\", \"Level4\": \"MEDIUM\"}, \"CellsPerObject\": 64, \"FillFactor\": 80, \"CompressionType\": \"PAGE\"}";
        const string geography = "{\"Name\": \"[SX_Location]\", \"Column\": \"[Location]\", \"Tessellation\": \"GEOGRAPHY_GRID\"}";
        string Xml(string spatial) => ModelXmlSerializer.ToIngestXml("[{\"Schema\": \"[dbo]\", \"Name\": \"[XmlSpatial]\", \"Columns\": ["
            + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false}, {\"Name\": \"[Shape]\", \"DataType\": \"GEOMETRY\", \"Nullable\": true}, {\"Name\": \"[Location]\", \"DataType\": \"GEOGRAPHY\", \"Nullable\": true}],"
            + "\"Indexes\": [{\"Name\": \"[PK_XmlSpatial]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"}],"
            + "\"SpatialIndexes\": [" + spatial + "]}]", "Tables", "Table");
        string Shape()
        {
            cmd.CommandText = "SELECT i.name COLLATE DATABASE_DEFAULT + ':' + i.tessellation_scheme COLLATE DATABASE_DEFAULT + ':' + ISNULL(CONVERT(VARCHAR(20), tz.bounding_box_xmax), '-') + ':' + ISNULL(tz.level_2_grid_desc COLLATE DATABASE_DEFAULT, '-') + ':' + CONVERT(VARCHAR(10), tz.cells_per_object) + ',' "
                            + "FROM sys.spatial_indexes i JOIN sys.spatial_index_tessellations tz ON tz.[object_id] = i.[object_id] AND tz.index_id = i.index_id "
                            + "WHERE i.[object_id] = OBJECT_ID('dbo.XmlSpatial') ORDER BY i.name FOR XML PATH('')";
            return cmd.ExecuteScalar() as string ?? "";
        }
        string Compression(string index)
        {
            cmd.CommandText = "SELECT MAX(p.data_compression_desc) FROM sys.indexes i JOIN sys.internal_tables it ON it.parent_object_id = i.[object_id] AND it.parent_minor_id = i.index_id "
                            + $"JOIN sys.partitions p ON p.[object_id] = it.[object_id] AND p.index_id = 1 WHERE i.[object_id] = OBJECT_ID('dbo.XmlSpatial') AND i.name = '{index}'";
            return cmd.ExecuteScalar() as string ?? "(no index)";
        }
        int Audits()
        {
            cmd.CommandText = "SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%XmlSpatial%'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        try
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlSpatial', @TableDefinitions = N'{Xml(geometry + "," + geography).Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();
            Assert.That(Shape(), Is.EqualTo("SX_Location:GEOGRAPHY_GRID:-:MEDIUM:16,SX_Shape:GEOMETRY_GRID:500:LOW:64,"), "created as declared");
            Assert.That(Compression("SX_Shape") + "|" + Compression("SX_Location"), Is.EqualTo("PAGE|NONE"), "compressed as declared");

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlSpatial', @TableDefinitions = N'{Xml(geometry + "," + geography).Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();
            Assert.That(Audits(), Is.Zero, "a redeploy touches nothing");

            cmd.CommandText = "EXEC SchemaSmith.GenerateTableXml @p_Schema='dbo', @p_Table='XmlSpatial'";
            var sb = new System.Text.StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    sb.Append(reader.GetValue(0)?.ToString());
            var table = (Schema.Domain.SqlServer.SqlServerTable)PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(sb.ToString()), Platform.SqlServer);
            var shape = table.SpatialIndexes.Single(s => s.Name == "[SX_Shape]");
            Assert.Multiple(() =>
            {
                Assert.That(table.SpatialIndexes.Select(s => s.Name), Is.EquivalentTo(new[] { "[SX_Shape]", "[SX_Location]" }));
                Assert.That($"{shape.Column}|{shape.Tessellation}|{shape.BoundingBox?.XMin}|{shape.BoundingBox?.YMax}|{shape.Grids?.Level3}|{shape.CellsPerObject}|{shape.FillFactor}|{shape.CompressionType}",
                    Is.EqualTo("[Shape]|GEOMETRY_GRID|0|200|HIGH|64|80|PAGE"), "extracted as declared");
                Assert.That(table.SpatialIndexes.Single(s => s.Name == "[SX_Location]").CompressionType, Is.Null, "an uncompressed index is extracted without it");
            });

            cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'XmlSpatial', @TableDefinitions = N'{Xml(geometry.Replace("\"CellsPerObject\": 64", "\"CellsPerObject\": 32").Replace("PAGE", "ROW")).Replace("'", "''")}'";
            cmd.ExecuteNonQuery();
            Assert.That(Shape(), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:32,"), "the index-only path re-creates a changed one and drops one no longer declared");
            Assert.That(Compression("SX_Shape"), Is.EqualTo("ROW"), "with the compression it declares");

            string Changed(string compression) => Xml(geometry.Replace("\"CellsPerObject\": 64", "\"CellsPerObject\": 32").Replace("PAGE", compression)).Replace("'", "''");
            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'XmlSpatial', @TableDefinitions = N'{Changed("ROW")}'";
            cmd.ExecuteNonQuery();
            Assert.That(Audits(), Is.Zero, "an index-only redeploy leaves the compressed index alone");

            cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'XmlSpatial', @TableDefinitions = N'{Changed("PAGE")}'";
            cmd.ExecuteNonQuery();
            Assert.That(Compression("SX_Shape"), Is.EqualTo("PAGE"), "a compression change alone re-creates it on the index-only path");
        }
        finally
        {
            DropTables(cmd, "dbo.XmlSpatial");
        }

        conn.Close();
    }

    // The XML tier parses a table's OnlineApply on its own, on both the full and the index-only paths: a table that asks for
    // Prefer gets its new index built online with the environment left at Off, and an option-only change is rebuilt in place.
    [TestCase(false)]
    [TestCase(true)]
    public void OnlineApply_OnTheXmlTier_ATablesOwnPrefer_BuildsOnline_AndAnOptionOnlyChangeIsRebuiltInPlace(bool indexOnly)
    {
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        var table = indexOnly ? "XmlOnlineIdx" : "XmlOnlineFull";
        string Xml(string indexes) => ModelXmlSerializer.ToIngestXml("[{\"Schema\": \"[dbo]\", \"Name\": \"[" + table + "]\", \"OnlineApply\": \"Prefer\", \"Columns\": ["
            + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false}, {\"Name\": \"[V]\", \"DataType\": \"INT\", \"Nullable\": true}],"
            + "\"Indexes\": [{\"Name\": \"[PK_" + table + "]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"}" + indexes + "]}]", "Tables", "Table");
        void Quench(string indexes, bool path)
        {
            var defs = Xml(indexes).Replace("'", "''");
            cmd.CommandText = path
                ? $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'XmlOnline', @TableDefinitions = N'{defs}', @DropUnknownIndexes = 1"
                : $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlOnline', @TableDefinitions = N'{defs}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();
        }
        string Audits()
        {
            cmd.CommandText = "SELECT STUFF((SELECT ',' + ObjectName + '|' + ActionType + '|' + ISNULL(Mode, '-') FROM SchemaSmith.ChangeAudit "
                            + $"WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%' ORDER BY Id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '')";
            return cmd.ExecuteScalar() as string ?? "";
        }
        try
        {
            cmd.CommandTimeout = 300;
            Quench("", false);
            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            Quench(",{\"Name\": \"[IX_" + table + "]\", \"IndexColumns\": \"[V]\"}", indexOnly);
            Assert.That(Audits(), Is.EqualTo($"[dbo].[{table}].[IX_{table}]|created|online"));

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            Quench(",{\"Name\": \"[IX_" + table + "]\", \"IndexColumns\": \"[V]\", \"PadIndex\": true}", indexOnly);
            cmd.CommandText = $"SELECT is_padded FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND name = 'IX_{table}'";
            var padded = Convert.ToBoolean(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(Audits(), Is.EqualTo($"[dbo].[{table}].[IX_{table}]|modified|online"), "rebuilt in place, online");
                Assert.That(padded, Is.True);
            });
        }
        finally
        {
            DropTables(cmd, $"dbo.{table}");
        }

        conn.Close();
    }

    [Test]
    public void LedgerNames_OnTheXmlTier_AreCreatedAsDeclared_AndExtractedWhereTheyDiffer()
    {
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            var json = "[{\"Schema\": \"[dbo]\", \"Name\": \"[XmlLedger]\", \"Ledger\": \"Updatable\", \"LedgerView\": {\"Name\": \"[XmlLedgerView]\", \"OperationTypeColumn\": \"[Op]\"},"
                     + "\"Columns\": [{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false},"
                     + "{\"Name\": \"[TxStart]\", \"DataType\": \"BIGINT\", \"Nullable\": false, \"GeneratedAlways\": \"TransactionIdStart\", \"Hidden\": true}],"
                     + "\"Indexes\": [{\"Name\": \"[PK_XmlLedger]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"}]}]";
            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlLedger', @TableDefinitions = N'{xml.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "EXEC SchemaSmith.GenerateTableXml @p_Schema='dbo', @p_Table='XmlLedger'";
            var sb = new System.Text.StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    sb.Append(reader.GetValue(0)?.ToString());
            var table = (Schema.Domain.SqlServer.SqlServerTable)PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(sb.ToString()), Platform.SqlServer);
            Assert.Multiple(() =>
            {
                Assert.That(table.Columns.OfType<Schema.Domain.SqlServer.SqlServerColumn>().Where(c => c.GeneratedAlways != null).Select(c => $"{c.Name}:{c.GeneratedAlways}:{c.Hidden}"),
                    Is.EqualTo(new[] { "[TxStart]:TransactionIdStart:True" }), "the declared column, not the engine's three");
                Assert.That($"{table.LedgerView?.Schema}|{table.LedgerView?.Name}|{table.LedgerView?.OperationTypeColumn}|{table.LedgerView?.TransactionIdColumn}",
                    Is.EqualTo("|[XmlLedgerView]|[Op]|"));
            });
        }
        finally
        {
            DropTables(cmd, "dbo.XmlLedger");
        }

        conn.Close();
    }

    [Test]
    public void TableQuench_XmlTier_AppliesDescriptions_AndGenerateTableXml_ReadsThemBack()
    {
        // The XML twin shreds Description, and the legacy MS_Description inside Extensions, through its own
        // .value() list; the JSON path proving it says nothing about this one.
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            var json = "[{\"Schema\": \"[dbo]\", \"Name\": \"[XmlDesc]\", \"Description\": \"the table\","
                + "\"Columns\": ["
                + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false, \"Description\": \"the key\"},"
                + "{\"Name\": \"[V]\", \"DataType\": \"INT\", \"Nullable\": true, \"Extensions\": {\"ExtendedProperties\": {\"MS_Description\": \"from the bag\"}}}"
                + "],"
                + "\"Indexes\": [{\"Name\": \"[PK_XmlDesc]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\", \"Description\": \"the key index\"},"
                + "{\"Name\": \"[IX_XmlDesc_V]\", \"IndexColumns\": \"[V]\", \"Description\": \"the lookup\"}]"
                + "}]";
            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlDesc', @TableDefinitions = N'{xml.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();

            string Ep(string level2)
            {
                cmd.CommandText = "SELECT CONVERT(NVARCHAR(MAX), [value]) FROM fn_listextendedproperty(N'MS_Description', N'SCHEMA', N'dbo', N'TABLE', N'XmlDesc'" + level2 + ")";
                return cmd.ExecuteScalar() as string;
            }
            Assert.Multiple(() =>
            {
                Assert.That(Ep(", default, default"), Is.EqualTo("the table"));
                Assert.That(Ep(", N'COLUMN', N'Id'"), Is.EqualTo("the key"));
                Assert.That(Ep(", N'COLUMN', N'V'"), Is.EqualTo("from the bag"), "the pre-2.8 spelling still deploys on the XML tier");
                Assert.That(Ep(", N'CONSTRAINT', N'PK_XmlDesc'"), Is.EqualTo("the key index"));
                Assert.That(Ep(", N'INDEX', N'IX_XmlDesc_V'"), Is.EqualTo("the lookup"));
            });

            // A property added outside the package comes back in the bag on extraction, beside the description.
            cmd.CommandText = "EXEC sys.sp_addextendedproperty @name=N'OwningTeam', @value=N'Billing', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'XmlDesc', @level2type=N'COLUMN', @level2name=N'V'";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "EXEC SchemaSmith.GenerateTableXml @p_Schema='dbo', @p_Table='XmlDesc'";
            var sb = new System.Text.StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    sb.Append(reader.GetValue(0)?.ToString());
            var table = PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(sb.ToString()), Platform.SqlServer);
            var v = table.Columns.Single(c => c.Name == "[V]");
            Assert.Multiple(() =>
            {
                Assert.That(table.Description, Is.EqualTo("the table"));
                Assert.That(table.Columns.Single(c => c.Name == "[Id]").Description, Is.EqualTo("the key"));
                Assert.That(v.Description, Is.EqualTo("from the bag"), "read back as Description, whichever spelling put it there");
                Assert.That(table.Indexes.Single(i => i.Name == "[PK_XmlDesc]").Description, Is.EqualTo("the key index"));
                Assert.That(table.Indexes.Single(i => i.Name == "[IX_XmlDesc_V]").Description, Is.EqualTo("the lookup"));
                Assert.That(((JObject)table.Extensions)?["ExtendedProperties"]?["MS_Description"], Is.Null, "it has a key of its own now");
                Assert.That((string)((JObject)v.Extensions)?["ExtendedProperties"]?["OwningTeam"], Is.EqualTo("Billing"), "the rest of the bag is untouched");
            });
        }
        finally
        {
            DropTables(cmd, "dbo.XmlDesc");
        }

        conn.Close();
    }

    [Test]
    public void TableQuench_XmlTier_AppliesExtendedPropertyBags_IncludingASpacedName()
    {
        // The ingest XML carries each bag as <ExtendedProperties><p n="Name">Value</p></ExtendedProperties>, because
        // a property's name need not be an element name, and the XML twin shreds that form on every component.
        var db = SharedCompat100XmlDb();
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(DbConnectionString(db));
        conn.Open();
        using var cmd = conn.CreateCommand();
        try
        {
            var json = "[{\"Schema\": \"[dbo]\", \"Name\": \"[XmlEp]\", \"Extensions\": {\"ExtendedProperties\": {\"My Note\": \"a & b < c\", \"OwningTeam\": \"Billing\"}},"
                + "\"Columns\": ["
                + "{\"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false, \"Extensions\": {\"ExtendedProperties\": {\"Classification\": \"Key\"}}},"
                + "{\"Name\": \"[ParentId]\", \"DataType\": \"INT\", \"Nullable\": true},"
                + "{\"Name\": \"[Qty]\", \"DataType\": \"INT\", \"Nullable\": true}"
                + "],"
                + "\"Indexes\": [{\"Name\": \"[PK_XmlEp]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"[Id]\"},"
                + "{\"Name\": \"[IX_XmlEp_Qty]\", \"IndexColumns\": \"[Qty]\", \"Extensions\": {\"ExtendedProperties\": {\"IdxNote\": \"hot\"}}}],"
                + "\"ForeignKeys\": [{\"Name\": \"[FK_XmlEp_Parent]\", \"Columns\": \"[ParentId]\", \"RelatedTableSchema\": \"[dbo]\", \"RelatedTable\": \"[XmlEp]\", \"RelatedColumns\": \"[Id]\", \"Extensions\": {\"ExtendedProperties\": {\"FkNote\": \"self\"}}}],"
                + "\"CheckConstraints\": [{\"Name\": \"[CK_XmlEp_Qty]\", \"Expression\": \"[Qty] >= 0\", \"Extensions\": {\"ExtendedProperties\": {\"CkNote\": \"non-negative\"}}}]"
                + "}]";
            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");
            Assert.That(xml, Does.Contain("<p n=\"My Note\">"), "the ingest encoding must carry the name as an attribute");
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = 'XmlEp', @TableDefinitions = N'{xml.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
            cmd.ExecuteNonQuery();

            string Ep(string name, string level2)
            {
                cmd.CommandText = $"SELECT CONVERT(NVARCHAR(MAX), [value]) FROM fn_listextendedproperty(N'{name}', N'SCHEMA', N'dbo', N'TABLE', N'XmlEp'{level2})";
                return cmd.ExecuteScalar() as string;
            }
            Assert.Multiple(() =>
            {
                Assert.That(Ep("My Note", ", default, default"), Is.EqualTo("a & b < c"));
                Assert.That(Ep("OwningTeam", ", default, default"), Is.EqualTo("Billing"));
                Assert.That(Ep("Classification", ", N'COLUMN', N'Id'"), Is.EqualTo("Key"));
                Assert.That(Ep("IdxNote", ", N'INDEX', N'IX_XmlEp_Qty'"), Is.EqualTo("hot"));
                Assert.That(Ep("FkNote", ", N'CONSTRAINT', N'FK_XmlEp_Parent'"), Is.EqualTo("self"));
                Assert.That(Ep("CkNote", ", N'CONSTRAINT', N'CK_XmlEp_Qty'"), Is.EqualTo("non-negative"));
            });
        }
        finally
        {
            DropTables(cmd, "dbo.XmlEp");
        }

        conn.Close();
    }
}
