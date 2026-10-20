// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

using NUnit.Framework;

namespace DataTongs.IntegrationTests.PostgreSQL;

/// <summary>
/// Integration tests for DataTongs data extraction against PostgreSQL.
/// Creates its own test database with test tables for extraction testing.
/// </summary>
[Category("PostgreSQL")]
[TestFixture]
[Category("Integration")]
public class DataTongsExtractionTests
{
    private string _integrationDb = "";
    private string _connectionString = "";
    private Dictionary<string, string> _connProps = null!;
    private IDbConnection _connection = null!;
    private global::DataTongs.DataTongs _dataTongs = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], _connProps);
        _integrationDb = GenerateUniqueDBName("DTExtract");

        CreateTestDatabase();
        CreateTestTables();
    }

    [SetUp]
    public void SetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connection = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(
            ConnectionString.Build(Platform.PostgreSQL,
                config["PostgreSQL:Server"],
                _integrationDb,
                config["PostgreSQL:User"],
                config["PostgreSQL:Password"],
                config["PostgreSQL:Port"],
                _connProps));
        _connection.Open();
        _dataTongs = new global::DataTongs.DataTongs(Platform.PostgreSQL);
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

    #region TableExists Tests

    [Test]
    public void TableExists_ExistingTable_ReturnsTrue()
    {
        using var command = _connection.CreateCommand();

        var exists = _dataTongs.TableExists(command, "public", "TestActor");

        Assert.That(exists, Is.True);
    }

    [Test]
    public void TableExists_NonExistentTable_ReturnsFalse()
    {
        using var command = _connection.CreateCommand();

        var exists = _dataTongs.TableExists(command, "public", "non_existent_table_xyz");

        Assert.That(exists, Is.False);
    }

    #endregion

    #region XML Delivery Extraction (B4b)

    [Test]
    public void XmlDeliveryExtraction_ConvertsExtractedJsonToTheDeliveryXmlShape()
    {
        // No native XML producer on PostgreSQL: DataTongs extracts the same JSON the Json path would
        // have, then MergeScriptHelper.JsonPayloadToXml converts it — this proves that conversion holds
        // for real PostgreSQL-shaped JSON (boolean literals, encode()'d bytea, NULLs), not just the
        // hand-written JSON literals the unit tests use.
        var tableName = $"TestXmlDelivery_{Guid.NewGuid():N}".Substring(0, 30);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $@"
CREATE TABLE public.""{tableName}"" (
    ""code"" VARCHAR(20) NOT NULL PRIMARY KEY, ""flag"" BOOLEAN, ""amount"" DECIMAL(10,2),
    ""note"" VARCHAR(100), ""ts"" TIMESTAMP, ""bin"" BYTEA);
INSERT INTO public.""{tableName}"" VALUES
    ('A001', TRUE, 7.25, 'a & b < c', '2026-08-11 06:00:00', decode('DEADBEEF', 'hex')),
    ('B002', FALSE, NULL, NULL, NULL, NULL);";
            command.ExecuteNonQuery();
        }

        try
        {
            string xml;
            using (var command = _connection.CreateCommand())
            {
                var selectColumns = _dataTongs.GetSelectColumns(command, "public", tableName);
                var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", tableName, "\"code\"", null);
                xml = MergeScriptHelper.JsonPayloadToXml(json);
            }

            Assert.That(xml, Does.Contain("<c n=\"code\">A001</c>"));
            Assert.That(xml, Does.Contain("<c n=\"flag\">1</c>"), "A PostgreSQL boolean must normalize to 1, not 'true'.");
            Assert.That(xml, Does.Contain("a &amp; b &lt; c"), "XML-special characters must be escaped.");
            Assert.That(xml, Does.Contain("<c n=\"bin\">3q2+7w==</c>"), "Binary must be base64, matching the SQL Server reference producer's encoding.");
            Assert.That(xml, Does.Contain("<row><c n=\"code\">B002</c><c n=\"flag\">0</c></row>"),
                "B002's NULL columns (amount/note/ts/bin) must be omitted entirely, and its FALSE flag must be '0'.");
        }
        finally
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS public.\"{tableName}\"";
            command.ExecuteNonQuery();
        }
    }

    #endregion

    #region GetSelectColumns Tests

    [Test]
    public void GetSelectColumns_ReturnsAllColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "public", "TestActor");

        Assert.That(columns, Is.Not.Null.And.Not.Empty);
        Assert.That(columns, Does.Contain("\"ActorId\""));
        Assert.That(columns, Does.Contain("\"FirstName\""));
        Assert.That(columns, Does.Contain("\"LastName\""));
        Assert.That(columns, Does.Contain("\"LastUpdate\""));
    }

    [Test]
    public void GetSelectColumns_ExcludesGeneratedColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "public", "TestGenerated");

        // Should include Id, FirstName, LastName but not FullName (generated)
        Assert.That(columns, Does.Contain("\"Id\""));
        Assert.That(columns, Does.Contain("\"FirstName\""));
        Assert.That(columns, Does.Contain("\"LastName\""));
        Assert.That(columns, Does.Not.Contain("\"FullName\""));
    }

    #endregion

    #region GetTableDataJson Tests

    [Test]
    public void GetTableDataJson_ReturnsValidJson()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestActor");
        var keyColumns = MergeScriptHelper.GetKeyColumns(Platform.PostgreSQL, command, "public", "TestActor");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestActor", keyColumns, null);

        Assert.That(json, Is.Not.Null);
        Assert.That(json, Does.Contain("ActorId"));
        Assert.That(json, Does.Contain("FirstName"));
        Assert.That(json, Does.Contain("LastName"));
    }

    // PG-008. A child table is refused at extraction, so its rows are not the parent's to deliver.
    [Test]
    public void GetTableDataJson_AnInheritanceParent_ReadsOnlyItsOwnRows()
    {
        var parent = $"InhParent_{Guid.NewGuid():N}"[..30];
        using var command = _connection.CreateCommand();
        command.CommandText = $@"
CREATE TABLE public.""{parent}"" (""code"" VARCHAR(20) NOT NULL PRIMARY KEY);
CREATE TABLE public.""{parent}_c"" (""extra"" INT) INHERITS (public.""{parent}"");
INSERT INTO public.""{parent}"" VALUES ('PARENT');
INSERT INTO public.""{parent}_c"" VALUES ('CHILD', 1);";
        command.ExecuteNonQuery();
        try
        {
            var selectColumns = _dataTongs.GetSelectColumns(command, "public", parent);
            var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", parent, "\"code\"", null);
            Assert.That(json, Does.Contain("PARENT").And.Not.Contain("CHILD"), json);
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS public.\"{parent}\" CASCADE";
            command.ExecuteNonQuery();
        }
    }

    [Test]
    public void GetTableDataJson_WithFilter_AppliesFilter()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestActor");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestActor", "\"ActorId\"", "\"ActorId\" = 1");

        Assert.That(json, Is.Not.Null);
        // Should only contain one actor
        var objectCount = json.Split(new[] { "},{" }, StringSplitOptions.None).Length;
        Assert.That(objectCount, Is.EqualTo(1));
    }

    [Test]
    public void GetTableDataJson_EmptyResult()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestActor");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestActor", "\"ActorId\"", "\"ActorId\" = -999");

        // JSON_AGG returns null for empty results
        Assert.That(string.IsNullOrEmpty(json) || json == "null" || json == "[]", Is.True);
    }

    [Test]
    public void GetTableDataJson_DecimalColumn_PreservesPrecision()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestPayment");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestPayment", "\"PaymentId\"", "\"PaymentId\" = 1");

        Assert.That(json, Does.Contain("Amount"));
        // Decimal values should be preserved (not converted to scientific notation)
    }

    [Test]
    public void GetTableDataJson_NullValues_HandlesCorrectly()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestNullable");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestNullable", "\"Id\"", "\"OptionalText\" IS NULL");

        Assert.That(json, Is.Not.Null);
        // JSON should handle null values properly
    }

    #endregion

    #region Legacy Type Tests

    [Test]
    public void GetSelectColumns_LegacyTypes_ReturnsAllColumns()
    {
        using var command = _connection.CreateCommand();

        var columns = _dataTongs.GetSelectColumns(command, "public", "TestLegacyTypes");

        Assert.That(columns, Does.Contain("\"Id\""));
        Assert.That(columns, Does.Contain("\"ByteaData\""));
        Assert.That(columns, Does.Contain("\"TextData\""));
        Assert.That(columns, Does.Contain("\"XmlData\""));
        Assert.That(columns, Does.Contain("\"JsonData\""));
        Assert.That(columns, Does.Contain("\"JsonbData\""));
        Assert.That(columns, Does.Contain("\"UuidData\""));
    }

    [Test]
    public void GetTableDataJson_LegacyTypes_ReturnsValidJson()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestLegacyTypes");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestLegacyTypes", "\"Id\"", "\"Id\" = 1");

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("Id"));
        Assert.That(json, Does.Contain("TextData"));
        Assert.That(json, Does.Contain("Large text content"));
        Assert.That(json, Does.Contain("UuidData"));
    }

    [Test]
    public void GetTableDataJson_LegacyTypes_NullValues_HandledCorrectly()
    {
        using var command = _connection.CreateCommand();
        var selectColumns = _dataTongs.GetSelectColumns(command, "public", "TestLegacyTypes");

        var json = _dataTongs.GetTableDataJson(command, selectColumns, "public", "TestLegacyTypes", "\"Id\"", "\"Id\" = 2");

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        Assert.That(json, Does.Contain("Id"));
    }

    #endregion

    #region Database Setup

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace("-", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void CreateTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"CREATE DATABASE ""{_integrationDb}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private void CreateTestTables()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var dbConnString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _integrationDb, config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], _connProps);
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(dbConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // TestActor table
        cmd.CommandText = @"
CREATE TABLE public.""TestActor"" (
    ""ActorId"" INT NOT NULL PRIMARY KEY,
    ""FirstName"" VARCHAR(50) NOT NULL,
    ""LastName"" VARCHAR(50) NOT NULL,
    ""LastUpdate"" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO public.""TestActor"" (""ActorId"", ""FirstName"", ""LastName"") VALUES
    (1, 'PENELOPE', 'GUINESS'),
    (2, 'NICK', 'WAHLBERG'),
    (3, 'ED', 'CHASE');
";
        cmd.ExecuteNonQuery();

        // TestGenerated table (with GENERATED ALWAYS AS stored column)
        cmd.CommandText = @"
CREATE TABLE public.""TestGenerated"" (
    ""Id"" INT NOT NULL PRIMARY KEY,
    ""FirstName"" VARCHAR(50) NOT NULL,
    ""LastName"" VARCHAR(50) NOT NULL,
    ""FullName"" VARCHAR(101) GENERATED ALWAYS AS (""FirstName"" || ' ' || ""LastName"") STORED
);

INSERT INTO public.""TestGenerated"" (""Id"", ""FirstName"", ""LastName"") VALUES
    (1, 'John', 'Doe'),
    (2, 'Jane', 'Smith');
";
        cmd.ExecuteNonQuery();

        // TestPayment table (decimal precision)
        cmd.CommandText = @"
CREATE TABLE public.""TestPayment"" (
    ""PaymentId"" INT NOT NULL PRIMARY KEY,
    ""Amount"" DECIMAL(10,2) NOT NULL,
    ""PaymentDate"" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO public.""TestPayment"" (""PaymentId"", ""Amount"") VALUES
    (1, 123.45),
    (2, 678.90);
";
        cmd.ExecuteNonQuery();

        // TestNullable table (null handling)
        cmd.CommandText = @"
CREATE TABLE public.""TestNullable"" (
    ""Id"" INT NOT NULL PRIMARY KEY,
    ""OptionalText"" VARCHAR(100),
    ""OptionalNumber"" INT,
    ""OptionalDate"" DATE
);

INSERT INTO public.""TestNullable"" (""Id"", ""OptionalText"", ""OptionalNumber"", ""OptionalDate"") VALUES
    (1, 'has value', 42, '2024-01-01'),
    (2, NULL, NULL, NULL);
";
        cmd.ExecuteNonQuery();

        // TestLegacyTypes table
        cmd.CommandText = @"
CREATE TABLE public.""TestLegacyTypes"" (
    ""Id"" INT NOT NULL PRIMARY KEY,
    ""ByteaData"" BYTEA,
    ""TextData"" TEXT,
    ""XmlData"" XML,
    ""JsonData"" JSON,
    ""JsonbData"" JSONB,
    ""UuidData"" UUID
);

INSERT INTO public.""TestLegacyTypes"" (""Id"", ""ByteaData"", ""TextData"", ""XmlData"", ""JsonData"", ""JsonbData"", ""UuidData"") VALUES
    (1, E'\\xDEADBEEF', 'Large text content here', '<root><item>test</item></root>',
     '{""key"": ""value""}', '{""key"": ""value""}', 'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'),
    (2, NULL, NULL, NULL, NULL, NULL, NULL);
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private void DropTestDatabase()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_integrationDb}' AND pid <> pg_backend_pid();
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{_integrationDb}"";";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    #endregion
}
