// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// G5: an XML, spatial or JSON index carries a Description (its MS_Description) and an Extensions bag as any index does,
/// on both ingest tiers and the index-only path. An XML index's bag was extracted but never applied, and the other two
/// had neither. JSON indexes join in where the server has them (SQL Server 2025).
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class TypedIndexDescriptionTests
{
    private const string TableName = "typed_desc_test";
    private const string Product = "TypedDescProduct";
    private IDbConnection _connection = null!;
    private IDbConnection _xmlTier;
    private string _xmlTierDb;
    private bool _json;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _json = Convert.ToInt32(Value(_connection, "SELECT CASE WHEN OBJECT_ID('sys.json_indexes') IS NULL THEN 0 ELSE 1 END")) == 1;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _xmlTier?.Dispose();
        if (_xmlTierDb != null)
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            using var master = _connection.CreateCommand();
            TestDatabaseDrop.Drop(master, _xmlTierDb);
        }
        Exec(_connection, $"DROP TABLE IF EXISTS dbo.{TableName}");
        _connection?.Dispose();
    }

    // The XML tier is its own procedure set, so it has a database kindled with it, built once on first use.
    private IDbConnection Connection(bool xmlTier)
    {
        if (!xmlTier) return _connection;
        if (_xmlTier != null) return _xmlTier;
        _xmlTierDb = $"typeddesc_xmltier_{Guid.NewGuid().ToString()[..8]}";
        Exec(_connection, $"CREATE DATABASE [{_xmlTierDb}]");
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = _xmlTierDb };
        _xmlTier = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(builder.ConnectionString);
        _xmlTier.Open();
        using var cmd = _xmlTier.CreateCommand();
        cmd.CommandTimeout = 0;
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml);
        return _xmlTier;
    }

    [SetUp]
    public void SetUp()
    {
        Exec(_connection, $"DROP TABLE IF EXISTS dbo.{TableName}");
        if (_xmlTier != null) Exec(_xmlTier, $"DROP TABLE IF EXISTS dbo.{TableName}");
    }

    private static void Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private static object Value(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var v = cmd.ExecuteScalar();
        return v == DBNull.Value ? null : v;
    }

    private string[] Indexes => _json ? ["pxml_doc", "sx_shape", "jx_data"] : ["pxml_doc", "sx_shape"];

    private static string Extra(string description, (string Name, string Value)[] bag)
    {
        var s = description == null ? "" : $", \"Description\": \"{description}\"";
        if (bag.Length > 0)
            s += ", \"Extensions\": { \"ExtendedProperties\": { " + string.Join(", ", bag.Select(b => $"\"{b.Name}\": \"{b.Value}\"")) + " } }";
        return s;
    }

    private string Package(string description, params (string Name, string Value)[] bag)
    {
        var e = Extra(description, bag);
        return "[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\","
            + " \"Columns\": [ { \"Name\": \"Id\", \"DataType\": \"INT\", \"Nullable\": false }, { \"Name\": \"Doc\", \"DataType\": \"XML\", \"Nullable\": true },"
            + " { \"Name\": \"Shape\", \"DataType\": \"GEOMETRY\", \"Nullable\": true }"
            + (_json ? ", { \"Name\": \"Data\", \"DataType\": \"JSON\", \"Nullable\": true }" : "") + " ],"
            + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"Id\" } ],"
            + " \"XmlIndexes\": [ { \"Name\": \"pxml_doc\", \"IsPrimary\": true, \"Column\": \"Doc\"" + e + " } ],"
            + " \"SpatialIndexes\": [ { \"Name\": \"sx_shape\", \"Column\": \"Shape\", \"Tessellation\": \"GEOMETRY_AUTO_GRID\","
            + " \"BoundingBox\": { \"XMin\": 0, \"YMin\": 0, \"XMax\": 100, \"YMax\": 100 }" + e + " } ]"
            + (_json ? ", \"JsonIndexes\": [ { \"Name\": \"jx_data\", \"Column\": \"Data\"" + e + " } ]" : "")
            + " }]";
    }

    private void Run(bool xmlTier, string proc, string json)
    {
        using var cmd = Connection(xmlTier).CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.{proc} @ProductName = '{Product}', @TableDefinitions = @t";
        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = xmlTier ? ModelXmlSerializer.ToIngestXml(json, "Tables", "Table") : json;
        cmd.Parameters.Add(t);
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void Deploy(bool xmlTier, bool indexOnly, string json) => Run(xmlTier, indexOnly ? "IndexOnlyQuench" : "TableQuench", json);

    private string Properties(bool xmlTier, string property) =>
        string.Join("|", Indexes.Select(i => $"{i}={Value(Connection(xmlTier),
            $"SELECT CONVERT(NVARCHAR(MAX), [value]) FROM fn_listextendedproperty(N'{property}', N'SCHEMA', N'dbo', N'TABLE', N'{TableName}', N'INDEX', N'{i}')")}"));

    private string Each(string value) => string.Join("|", Indexes.Select(i => $"{i}={value}"));

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ATypedIndexsDescriptionAndBag_AreSetChangedLeftAloneWhenOmitted_AndRemoved(bool xmlTier, bool indexOnly)
    {
        if (indexOnly) Deploy(xmlTier, false, Package(null));

        Deploy(xmlTier, indexOnly, Package("The doc's index", ("Owner", "Docs")));
        Assert.Multiple(() =>
        {
            Assert.That(Properties(xmlTier, "MS_Description"), Is.EqualTo(Each("The doc's index")), "set");
            Assert.That(Properties(xmlTier, "Owner"), Is.EqualTo(Each("Docs")), "the bag is applied");
        });

        Deploy(xmlTier, indexOnly, Package("Changed", ("Owner", "Maps")));
        Assert.That(Properties(xmlTier, "MS_Description") + "/" + Properties(xmlTier, "Owner"), Is.EqualTo(Each("Changed") + "/" + Each("Maps")), "changed in place");

        Deploy(xmlTier, indexOnly, Package(null));
        Assert.That(Properties(xmlTier, "MS_Description"), Is.EqualTo(Each("Changed")), "an omitted one is left alone");

        Deploy(xmlTier, indexOnly, Package(""));
        Assert.That(Properties(xmlTier, "MS_Description"), Is.EqualTo(Each("")), "\"\" removes it");
    }

    // A package extracted before XML indexes had a Description kept their MS_Description in the bag.
    [TestCase(false)]
    [TestCase(true)]
    public void AnMsDescriptionInTheBag_FromAnOlderPackage_StillDeploys(bool xmlTier)
    {
        Deploy(xmlTier, false, Package(null, ("MS_Description", "From the bag")));
        Assert.That(Properties(xmlTier, "MS_Description"), Is.EqualTo(Each("From the bag")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Extraction_WritesDescription_AndKeepsItOutOfTheBag(bool xmlTier)
    {
        Deploy(xmlTier, false, Package("Extract me", ("Owner", "Docs")));

        using var cmd = Connection(xmlTier).CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.{(xmlTier ? "GenerateTableXml" : "GenerateTableJSON")} @p_Schema = 'dbo', @p_Table = '{TableName}'";
        var sb = new StringBuilder();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) sb.Append(reader.GetString(0));
        var json = xmlTier ? ModelXmlSerializer.FromIngestXml(sb.ToString()) : sb.ToString();
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

        static JObject Eps(DynamicBase o) => (JObject)((JObject)o.Extensions)?["ExtendedProperties"];
        var found = new List<string>();
        found.AddRange(table.XmlIndexes.Select(i => $"{i.Description}/{Eps(i)?["Owner"]}/{Eps(i)?["MS_Description"]}"));
        found.AddRange(table.SpatialIndexes.Select(i => $"{i.Description}/{Eps(i)?["Owner"]}/{Eps(i)?["MS_Description"]}"));
        found.AddRange(table.JsonIndexes.Select(i => $"{i.Description}/{Eps(i)?["Owner"]}/{Eps(i)?["MS_Description"]}"));
        Assert.That(found, Is.EqualTo(Indexes.Select(_ => "Extract me/Docs/").ToList()), json);
    }
}
