// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace DataTongs.IntegrationTests.SqlServer;

/// <summary>
/// Integration tests for DataTongs data extraction against SQL Server.
/// Creates its own test database with test tables.
/// </summary>
[TestFixture]
[Category("SqlServer")]
[Category("Integration")]
public class DataTongsExtractionTests
{
    private string _integrationDb = "";
    private string _connectionString = "";
    private IDbConnection _connection = null!;
    private global::DataTongs.DataTongs _dataTongs = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("DTExtract");

        CreateTestDatabase();
    }

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        _connection.Open();
        _connection.ChangeDatabase(_integrationDb);
        _dataTongs = new global::DataTongs.DataTongs(Platform.SqlServer);
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        DropTestDatabase();
    }

    #region XML Delivery Extraction (B1 slice 3)

    [Test]
    public void XmlExtraction_RoundTripsThroughTheShred_OnCompat100()
    {
        // Extract in the delivery XML encoding, then deploy it through the legacy-tier XML shred on a
        // compatibility-level-100 database — every value (NULLs, bit, decimal, datetime, varbinary,
        // XML-special characters) must survive the round trip, and NULL columns must be omitted.
        var db = "dt_xmlrt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var master = _connection.CreateCommand())
        {
            master.CommandText = $"CREATE DATABASE [{db}]; ALTER DATABASE [{db}] SET COMPATIBILITY_LEVEL = 100;";
            master.ExecuteNonQuery();
        }
        try
        {
            _connection.ChangeDatabase(db);
            using (var c = _connection.CreateCommand())
            {
                c.CommandText = @"
CREATE TABLE [dbo].[Src] (
  [code] VARCHAR(20) NOT NULL PRIMARY KEY, [flag] BIT NULL, [amount] DECIMAL(10,2) NULL,
  [note] NVARCHAR(100) NULL, [ts] DATETIME2 NULL, [bin] VARBINARY(MAX) NULL);
INSERT INTO [dbo].[Src] VALUES
  ('A001', 1, 7.25, N'a & b < c', '2026-08-11T06:00:00', 0xDEADBEEF),
  ('B002', 0, NULL, NULL, NULL, NULL);";
                c.ExecuteNonQuery();
            }

            string xml;
            using (var c = _connection.CreateCommand())
                xml = _dataTongs.GetTableDataXmlSqlServer(c, "dbo", "Src", "[code]", null);

            Assert.That(xml, Does.Contain("<c n=\"code\">A001</c>"));
            Assert.That(xml, Does.Contain("a &amp; b &lt; c"), "XML-special characters must be escaped.");
            Assert.That(xml, Does.Contain("<c n=\"bin\">3q2+7w==</c>"), "Binary must be base64.");
            Assert.That(xml, Does.Contain("<row><c n=\"code\">B002</c><c n=\"flag\">0</c></row>"),
                "A row's NULL columns must be omitted entirely (absent <c> = NULL).");

            using (var c = _connection.CreateCommand())
            {
                c.CommandText = @"CREATE TABLE [dbo].[Dst] (
  [code] VARCHAR(20) NOT NULL PRIMARY KEY, [flag] BIT NULL, [amount] DECIMAL(10,2) NULL,
  [note] NVARCHAR(100) NULL, [ts] DATETIME2 NULL, [bin] VARBINARY(MAX) NULL);";
                c.ExecuteNonQuery();
            }
            using (var c = _connection.CreateCommand())
            {
                var script = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, c, "dbo", "Dst", xml, "[code]",
                    mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false,
                    mergeFilter: null, contentEncoding: "Xml");
                c.CommandText = script;
                c.ExecuteNonQuery();
            }

            using (var c = _connection.CreateCommand())
            {
                c.CommandText = "SELECT [flag],[amount],[note],CONVERT(VARCHAR(33),[ts],126),CONVERT(VARCHAR(MAX),[bin],1) FROM [dbo].[Dst] WHERE [code]='A001'";
                using var r = c.ExecuteReader();
                Assert.That(r.Read(), Is.True);
                Assert.That(r.GetBoolean(0), Is.True);
                Assert.That(r.GetDecimal(1), Is.EqualTo(7.25m));
                Assert.That(r.GetString(2), Is.EqualTo("a & b < c"));
                Assert.That(r.GetString(3), Does.StartWith("2026-08-11T06:00:00"));
                Assert.That(r.GetString(4), Is.EqualTo("0xDEADBEEF"));
            }
            using (var c = _connection.CreateCommand())
            {
                c.CommandText = "SELECT [flag],[amount],[note],[ts],[bin] FROM [dbo].[Dst] WHERE [code]='B002'";
                using var r = c.ExecuteReader();
                Assert.That(r.Read(), Is.True);
                Assert.That(r.GetBoolean(0), Is.False);
                Assert.That(r.IsDBNull(1), Is.True, "A NULL decimal must round-trip as NULL.");
                Assert.That(r.IsDBNull(2), Is.True);
                Assert.That(r.IsDBNull(3), Is.True);
                Assert.That(r.IsDBNull(4), Is.True);
            }
        }
        finally
        {
            _connection.ChangeDatabase(_integrationDb);
            using var master = _connection.CreateCommand();
            master.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            master.ExecuteNonQuery();
        }
    }

    // The XML producer converted with CONVERT's default style -- 6 significant digits of a float, 2 decimals of money --
    // and wrote geometry with STAsText, which drops Z and M. Every value is compared with its source after delivery, a
    // float bit for bit.
    [Test]
    public void XmlExtraction_KeepsFloatMoneyAndGeometryZM()
    {
        var db = "dt_xmlnum_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using (var master = _connection.CreateCommand())
        {
            master.CommandText = $"CREATE DATABASE [{db}]";
            master.ExecuteNonQuery();
        }
        try
        {
            _connection.ChangeDatabase(db);
            const string columns = "[id] INT NOT NULL PRIMARY KEY, [f] FLOAT NULL, [r] REAL NULL, [m] MONEY NULL, [sm] SMALLMONEY NULL, [g] GEOMETRY NULL";
            using (var c = _connection.CreateCommand())
            {
                c.CommandText = $@"
CREATE TABLE [dbo].[Src] ({columns});
CREATE TABLE [dbo].[Dst] ({columns});
INSERT INTO [dbo].[Src] VALUES
  (1, 1.2345678901234567, CAST(3.1415927 AS REAL), 12345.6789, 98.7654, geometry::STGeomFromText('POINT (1 2 3 4)', 0)),
  (2, 1e-300, CAST(-1.17549435E-38 AS REAL), -922337203685477.5808, -214748.3648, geometry::STGeomFromText('LINESTRING (0 0 1, 2 2 2)', 4326));";
                c.ExecuteNonQuery();
            }

            string xml;
            using (var c = _connection.CreateCommand())
                xml = _dataTongs.GetTableDataXmlSqlServer(c, "dbo", "Src", "[id]", null);
            using (var c = _connection.CreateCommand())
            {
                c.CommandText = MergeScriptHelper.BuildMergeScript(Platform.SqlServer, c, "dbo", "Dst", xml, "[id]",
                    mergeUpdate: true, mergeDelete: false, disableTriggers: false, tokenizeScripts: false,
                    mergeFilter: null, contentEncoding: "Xml");
                c.ExecuteNonQuery();
            }

            using (var c = _connection.CreateCommand())
            {
                c.CommandText = @"SELECT COUNT(*) FROM [dbo].[Src] s JOIN [dbo].[Dst] d ON d.[id] = s.[id]
WHERE CAST(d.[f] AS VARBINARY(8)) = CAST(s.[f] AS VARBINARY(8)) AND CAST(d.[r] AS VARBINARY(4)) = CAST(s.[r] AS VARBINARY(4))
  AND d.[m] = s.[m] AND d.[sm] = s.[sm] AND d.[g].AsTextZM() = s.[g].AsTextZM() AND d.[g].STSrid = s.[g].STSrid";
                Assert.That(c.ExecuteScalar(), Is.EqualTo(2), "every value must arrive exactly as it left. XML: " + xml);
            }
        }
        finally
        {
            _connection.ChangeDatabase(_integrationDb);
            using var master = _connection.CreateCommand();
            master.CommandText = $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END";
            master.ExecuteNonQuery();
        }
    }

    #endregion

    #region TableExists Tests

    [Test]
    public void TableExists_ExistingTable_ReturnsTrue()
    {
        using var command = _connection.CreateCommand();

        var exists = _dataTongs.TableExists(command, "dbo", "Country");

        Assert.That(exists, Is.True);
    }

    [Test]
    public void TableExists_NonExistentTable_ReturnsFalse()
    {
        using var command = _connection.CreateCommand();

        var exists = _dataTongs.TableExists(command, "dbo", "non_existent_table_xyz");

        Assert.That(exists, Is.False);
    }

    #endregion

    #region GetSelectColumns Tests

    [Test]
    public void GetSelectColumns_ReturnsAllColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "dbo", "Country");

        Assert.That(columns, Is.Not.Null.And.Not.Empty);
        Assert.That(columns, Does.Contain("[CountryId]"));
        Assert.That(columns, Does.Contain("[CountryName]"));
        Assert.That(columns, Does.Contain("[LastUpdate]"));
    }

    [Test]
    public void GetSelectColumns_ExcludesComputedColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "dbo", "Person");

        // Should include Id, FirstName, LastName but NOT FullName (computed)
        Assert.That(columns, Does.Contain("[Id]"));
        Assert.That(columns, Does.Contain("[FirstName]"));
        Assert.That(columns, Does.Contain("[LastName]"));
        Assert.That(columns, Does.Not.Contain("[FullName]"));
    }

    #endregion

    #region GetTableDataJson Tests

    [Test]
    public void GetTableDataJson_ReturnsValidJson()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", "[CountryId]", null);

        Assert.That(json, Is.Not.Null);
        Assert.That(json, Does.Contain("CountryId"));
        Assert.That(json, Does.Contain("CountryName"));
        Assert.That(json, Does.Contain("LastUpdate"));
    }

    [Test]
    public void GetTableDataJson_WithFilter_AppliesFilter()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", "[CountryId]", "CountryId = 1");

        Assert.That(json, Is.Not.Null);
        // Should only contain one country
        var objectCount = json.Split(new[] { "},{" }, StringSplitOptions.None).Length;
        Assert.That(objectCount, Is.EqualTo(1));
    }

    [Test]
    public void GetTableDataJson_EmptyResult()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Country");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Country", "[CountryId]", "CountryId = -999");

        // SQL Server FOR JSON returns empty string for no rows
        Assert.That(string.IsNullOrEmpty(json), Is.True);
    }

    [Test]
    public void GetTableDataJson_DecimalColumn_PreservesPrecision()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Payment");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Payment", "[PaymentId]", "PaymentId = 1");

        Assert.That(json, Does.Contain("Amount"));
        // Decimal values should be preserved (not converted to scientific notation)
        Assert.That(json, Does.Contain("100.5000"));
    }

    [Test]
    public void GetTableDataJson_NullValues_HandlesCorrectly()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "Rental");

        // Some rentals have null ReturnDate
        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "Rental", "[RentalId]", "ReturnDate IS NULL");

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        // SQL Server FOR JSON omits null columns by default; the row should still have RentalId
        Assert.That(json, Does.Contain("RentalId"));
    }

    #endregion

    #region Empty Table Tests

    [Test]
    public void GetTableDataJson_EmptyTable_ReturnsEmptyOrNull()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "EmptyTable");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "EmptyTable", "[Id]", null);

        // SQL Server FOR JSON returns empty string for tables with no rows
        Assert.That(string.IsNullOrEmpty(json), Is.True);
    }

    #endregion

    #region Legacy Type Tests

    [Test]
    public void GetSelectColumns_LegacyTypes_ReturnsAllColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "dbo", "LegacyTypes");

        Assert.That(columns, Does.Contain("[Id]"));
        Assert.That(columns, Does.Contain("[ImageData]"));
        Assert.That(columns, Does.Contain("[NTextData]"));
        Assert.That(columns, Does.Contain("[TextData]"));
        Assert.That(columns, Does.Contain("[GeoData]"));
        Assert.That(columns, Does.Contain("[HierarchyData]"));
    }

    [Test]
    public void GetTableDataJson_LegacyTypes_ReturnsValidJson()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "LegacyTypes");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "LegacyTypes", "[Id]", "Id = 1");

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("Id"));
        Assert.That(json, Does.Contain("NTextData"));
        Assert.That(json, Does.Contain("TextData"));
        Assert.That(json, Does.Contain("Unicode text content"));
        Assert.That(json, Does.Contain("ASCII text content"));
    }

    [Test]
    public void GetTableDataJson_LegacyTypes_NullValues_HandledCorrectly()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "LegacyTypes");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "LegacyTypes", "[Id]", "Id = 2");

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("Id"));
    }

    [Test]
    public void BuildMergeScript_LegacyTypes_ContainsTypeMappings()
    {
        using var command = _connection.CreateCommand();

        var selectColumns = _dataTongs.GetSelectColumns(command, "dbo", "LegacyTypes");
        var json = _dataTongs.GetTableDataJson(command, selectColumns, "dbo", "LegacyTypes", "[Id]", null);

        var mergeScript = Schema.Utility.MergeScriptHelper.BuildMergeScript(
            Schema.Domain.Platform.SqlServer, command, "dbo", "LegacyTypes", json, "[Id]",
            mergeUpdate: true, mergeDelete: false, disableTriggers: false,
            tokenizeScripts: false, mergeFilter: null);

        Assert.That(mergeScript, Does.Contain("MERGE INTO [dbo].[LegacyTypes]"));
        Assert.That(mergeScript, Does.Contain("NVARCHAR(MAX)").IgnoreCase);
        Assert.That(mergeScript, Does.Contain("VARCHAR(MAX)").IgnoreCase);
        Assert.That(mergeScript, Does.Contain("VARBINARY(MAX)").IgnoreCase);
    }

    #endregion

    #region Helper Methods

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $"CREATE DATABASE [{_integrationDb}];";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);

        cmd.CommandText = @"
CREATE TABLE [dbo].[Country] (
    [CountryId] INT NOT NULL PRIMARY KEY,
    [CountryName] NVARCHAR(100) NOT NULL,
    [LastUpdate] DATETIME2 NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[Country] ([CountryId], [CountryName]) VALUES
    (1, 'United States'),
    (2, 'Canada'),
    (3, 'Mexico');

CREATE TABLE [dbo].[Person] (
    [Id] INT NOT NULL PRIMARY KEY,
    [FirstName] NVARCHAR(50) NOT NULL,
    [LastName] NVARCHAR(50) NOT NULL,
    [FullName] AS ([FirstName] + ' ' + [LastName])
);

INSERT INTO [dbo].[Person] ([Id], [FirstName], [LastName]) VALUES
    (1, 'John', 'Doe'),
    (2, 'Jane', 'Smith');

CREATE TABLE [dbo].[Payment] (
    [PaymentId] INT NOT NULL PRIMARY KEY,
    [CustomerId] INT NOT NULL,
    [Amount] DECIMAL(10,4) NOT NULL,
    [PaymentDate] DATE NOT NULL DEFAULT GETDATE()
);

INSERT INTO [dbo].[Payment] ([PaymentId], [CustomerId], [Amount], [PaymentDate]) VALUES
    (1, 1, 100.5000, '2024-01-15'),
    (2, 2, 200.7500, '2024-02-20'),
    (3, 1, 50.0000, '2024-03-10');

CREATE TABLE [dbo].[Rental] (
    [RentalId] INT NOT NULL PRIMARY KEY,
    [RentalDate] DATETIME2 NOT NULL DEFAULT GETDATE(),
    [ReturnDate] DATETIME2 NULL,
    [CustomerId] INT NOT NULL
);

INSERT INTO [dbo].[Rental] ([RentalId], [RentalDate], [ReturnDate], [CustomerId]) VALUES
    (1, '2024-01-10', '2024-01-17', 1),
    (2, '2024-02-15', NULL, 2),
    (3, '2024-03-01', NULL, 1);

CREATE TABLE [dbo].[EmptyTable] (
    [Id] INT NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(100) NOT NULL
);

CREATE TABLE [dbo].[LegacyTypes] (
    [Id] INT NOT NULL PRIMARY KEY,
    [ImageData] IMAGE NULL,
    [NTextData] NTEXT NULL,
    [TextData] TEXT NULL,
    [GeoData] GEOGRAPHY NULL,
    [HierarchyData] HIERARCHYID NULL
);

INSERT INTO [dbo].[LegacyTypes] ([Id], [ImageData], [NTextData], [TextData], [GeoData], [HierarchyData]) VALUES
    (1, CONVERT(IMAGE, 0xDEADBEEF), N'Unicode text content', 'ASCII text content',
     geography::Point(47.6062, -122.3321, 4326), '/1/2/3/'),
    (2, NULL, NULL, NULL, NULL, NULL);
";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
IF DB_ID('{_integrationDb}') IS NOT NULL
    ALTER DATABASE [{_integrationDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE IF EXISTS [{_integrationDb}];
";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    #endregion
}
