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
}
