// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// Selective XML indexes (SQL Server 2012 SP1): a primary that promotes declared paths, and secondaries on one of its
/// paths. Each is compared as its rendered clause, so these tests drive every deploy path (TableQuench and IndexOnly, on
/// both ingest tiers) through create, an unchanged redeploy, a change, and a redeploy of what extraction returns. A
/// re-created index gets a new rowset, so "left alone" is read from <c>sys.partitions.partition_id</c>: a secondary
/// selective index lives on its primary's internal table, so the node table's id cannot see it being re-created.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class SelectiveXmlIndexTests
{
    private const string TableName = "selective_xml_test";
    private const string Product = "SelectiveXmlProduct";
    private IDbConnection _connection = null!;
    private IDbConnection _xmlTier;
    private string _xmlTierDb;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
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
        _connection?.Dispose();
    }

    // The XML tier is its own procedure set, so it has a database kindled with it, built once on first use.
    private IDbConnection Connection(bool xmlTier)
    {
        if (!xmlTier) return _connection;
        if (_xmlTier != null) return _xmlTier;
        _xmlTierDb = $"selxml_xmltier_{Guid.NewGuid().ToString()[..8]}";
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

    private static long Scalar(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    // Every path kind: inferred, a typed xs:string with MAXLENGTH and SINGLETON, node(), and two SQL types written the way a
    // person would (lower case, a space) rather than the way the catalog reports them; a prefixed and a DEFAULT namespace;
    // and a secondary selective index on the SQL path, declared without IsPrimary and named to sort before its primary, so
    // only a create order that puts primaries first deploys it.
    private const string Paths =
        "{ \"Name\": \"pInfer\", \"Path\": \"/o:order/o:id\" },"
        + " { \"Name\": \"pName\", \"Path\": \"/o:order/o:name\", \"Type\": \"xs:string\", \"MaxLength\": 20, \"SingletonCardinality\": true },"
        + " { \"Name\": \"pLines\", \"Path\": \"/o:order/o:lines\", \"Type\": \"node()\" },"
        + " { \"Name\": \"pTotal\", \"Path\": \"/o:order/o:total\", \"Type\": \"decimal(10, 2)\", \"SingletonCardinality\": true }";

    private const string NotePath = "{ \"Name\": \"pNote\", \"Path\": \"/o:order/note\", \"Type\": \"nvarchar(40)\" }";

    private static string Selective(string paths, bool secondaryCompressed = false) =>
        "[ { \"Name\": \"sxi_" + TableName + "\", \"Column\": \"doc\", \"Selective\": {"
        + " \"Namespaces\": [ { \"Prefix\": \"o\", \"Uri\": \"http://ex/o\" }, { \"Uri\": \"http://ex/d\" } ],"
        + " \"Paths\": [ " + paths + " ] } },"
        + " { \"Name\": \"aux_" + TableName + "\", \"Column\": \"doc\", \"PrimaryIndex\": \"sxi_" + TableName + "\", \"ForPath\": \"pTotal\""
        + (secondaryCompressed ? ", \"XmlCompression\": true" : "") + " } ]";

    private static string Package(string xmlIndexes) =>
        "[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\","
        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"INT\", \"Nullable\": false }, { \"Name\": \"doc\", \"DataType\": \"XML\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"id\" } ]"
        + (xmlIndexes == null ? "" : ", \"XmlIndexes\": " + xmlIndexes) + " }]";

    private static string Encode(bool xmlTier, string json) => xmlTier ? ModelXmlSerializer.ToIngestXml(json, "Tables", "Table") : json;

    private void Run(bool xmlTier, string proc, string json)
    {
        using var cmd = Connection(xmlTier).CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.{proc} @ProductName = '{Product}', @TableDefinitions = @t";
        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = Encode(xmlTier, json);
        cmd.Parameters.Add(t);
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    // IndexOnly applies indexes to a table that exists, so that path deploys the bare table first.
    private void Deploy(bool xmlTier, bool indexOnly, string xmlIndexes)
    {
        if (indexOnly && Scalar(Connection(xmlTier), $"SELECT COUNT(*) FROM sys.tables WHERE name = '{TableName}'") == 0)
            Run(xmlTier, "TableQuench", Package(null));
        Run(xmlTier, indexOnly ? "IndexOnlyQuench" : "TableQuench", Package(xmlIndexes));
    }

    private long RowsetOf(bool xmlTier, string index) => Scalar(Connection(xmlTier),
        "SELECT MAX(p.partition_id) FROM sys.internal_tables it "
        + "JOIN sys.indexes xsi ON xsi.[object_id] = it.[object_id] AND xsi.[name] = '" + index + "' "
        + "JOIN sys.partitions p ON p.[object_id] = xsi.[object_id] AND p.index_id = xsi.index_id "
        + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}')");

    private long XmlIndexType(bool xmlTier, string index) => Scalar(Connection(xmlTier),
        $"SELECT xml_index_type FROM sys.xml_indexes WHERE [object_id] = OBJECT_ID('dbo.{TableName}') AND [name] = '{index}'");

    private static readonly object[] Paths4 =
    [
        new object[] { false, false }, new object[] { false, true }, new object[] { true, false }, new object[] { true, true },
    ];

    [TestCaseSource(nameof(Paths4))]
    public void ADeclaredSelectiveIndex_IsCreated_AndAnUnchangedRedeployLeavesItAlone(bool xmlTier, bool indexOnly)
    {
        Deploy(xmlTier, indexOnly, Selective(Paths + ", " + NotePath));

        var conn = Connection(xmlTier);
        Assert.Multiple(() =>
        {
            Assert.That(XmlIndexType(xmlTier, $"sxi_{TableName}"), Is.EqualTo(2), "a selective XML index, not a primary one");
            Assert.That(XmlIndexType(xmlTier, $"aux_{TableName}"), Is.EqualTo(3), "and a secondary selective one");
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM sys.selective_xml_index_paths WHERE [object_id] = OBJECT_ID('dbo.{TableName}')"),
                Is.EqualTo(5), "promoting every declared path");
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM sys.selective_xml_index_paths WHERE [object_id] = OBJECT_ID('dbo.{TableName}') "
                                     + "AND [name] = 'pName' AND xquery_type_description = 'xs:string' AND xquery_max_length = 20 AND is_singleton = 1"),
                Is.EqualTo(1), "with its type, length and cardinality");
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM sys.selective_xml_index_namespaces WHERE [object_id] = OBJECT_ID('dbo.{TableName}')"),
                Is.EqualTo(2), "and both namespaces");
        });

        var primary = RowsetOf(xmlTier, $"sxi_{TableName}");
        var secondary = RowsetOf(xmlTier, $"aux_{TableName}");
        Assert.That(primary, Is.GreaterThan(0), "the primary's rowset is found");
        Deploy(xmlTier, indexOnly, Selective(Paths + ", " + NotePath));
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, $"sxi_{TableName}"), Is.EqualTo(primary), "an unchanged redeploy leaves the selective index alone");
            Assert.That(RowsetOf(xmlTier, $"aux_{TableName}"), Is.EqualTo(secondary), "and its secondary");
        });
    }

    [TestCaseSource(nameof(Paths4))]
    public void AChangedPath_ReCreatesTheSelectiveIndex(bool xmlTier, bool indexOnly)
    {
        Deploy(xmlTier, indexOnly, Selective(Paths + ", " + NotePath));
        var primary = RowsetOf(xmlTier, $"sxi_{TableName}");

        Deploy(xmlTier, indexOnly, Selective(Paths + ", " + NotePath.Replace("nvarchar(40)", "nvarchar(60)")));

        var conn = Connection(xmlTier);
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, $"sxi_{TableName}"), Is.Not.EqualTo(primary), "a path's changed type re-creates the index");
            Assert.That(Scalar(conn, $"SELECT max_length FROM sys.selective_xml_index_paths WHERE [object_id] = OBJECT_ID('dbo.{TableName}') AND [name] = 'pNote'"),
                Is.EqualTo(120), "with the new type");
            Assert.That(XmlIndexType(xmlTier, $"aux_{TableName}"), Is.EqualTo(3), "and the secondary dropped with it is created again");
        });

        primary = RowsetOf(xmlTier, $"sxi_{TableName}");
        Deploy(xmlTier, indexOnly, Selective(Paths));
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, $"sxi_{TableName}"), Is.Not.EqualTo(primary), "a removed path re-creates it too");
            Assert.That(Scalar(conn, $"SELECT COUNT(*) FROM sys.selective_xml_index_paths WHERE [object_id] = OBJECT_ID('dbo.{TableName}')"),
                Is.EqualTo(4), "without the path");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AChangedSecondaryCompression_ReCreatesOnlyTheSecondary(bool xmlTier)
    {
        if (Scalar(_connection, "SELECT CASE WHEN COL_LENGTH('sys.partitions', 'xml_compression') IS NULL THEN 0 ELSE 1 END") != 1)
            Assert.Ignore("Reading XML compression back needs sys.partitions.xml_compression (2025, and 2022 from CU26).");
        Deploy(xmlTier, indexOnly: false, Selective(Paths));
        var primary = RowsetOf(xmlTier, $"sxi_{TableName}");

        Deploy(xmlTier, indexOnly: false, Selective(Paths, secondaryCompressed: true));

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(Connection(xmlTier),
                "SELECT MAX(CONVERT(TINYINT, p.xml_compression)) FROM sys.internal_tables it "
                + $"JOIN sys.indexes xsi ON xsi.[object_id] = it.[object_id] AND xsi.[name] = 'aux_{TableName}' "
                + "JOIN sys.partitions p ON p.[object_id] = xsi.[object_id] AND p.index_id = xsi.index_id "
                + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}')"), Is.EqualTo(1), "the secondary is compressed");
            Assert.That(RowsetOf(xmlTier, $"sxi_{TableName}"), Is.EqualTo(primary), "and the primary is left alone");
        });
        var secondary = RowsetOf(xmlTier, $"aux_{TableName}");
        Deploy(xmlTier, indexOnly: false, Selective(Paths, secondaryCompressed: true));
        Assert.That(RowsetOf(xmlTier, $"aux_{TableName}"), Is.EqualTo(secondary), "and the setting converges");
    }

    private string Extract(bool xmlTier)
    {
        using var cmd = Connection(xmlTier).CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.{(xmlTier ? "GenerateTableXml" : "GenerateTableJSON")} @p_Schema = 'dbo', @p_Table = '{TableName}'";
        cmd.CommandTimeout = 300;
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read()) sb.Append(reader.GetValue(0));
        return xmlTier ? ModelXmlSerializer.FromIngestXml(sb.ToString()) : sb.ToString();
    }

    // What SchemaTongs writes is the extraction read into the model and serialized again; deploying that back must be a no-op,
    // which is what proves the extracted form compares equal to the live index.
    [TestCase(false)]
    [TestCase(true)]
    public void WhatExtractionReturns_DeploysBackWithoutReCreatingAnything(bool xmlTier)
    {
        Deploy(xmlTier, indexOnly: false, Selective(Paths + ", " + NotePath));
        var primary = RowsetOf(xmlTier, $"sxi_{TableName}");
        var secondary = RowsetOf(xmlTier, $"aux_{TableName}");

        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(Extract(xmlTier), Platform.SqlServer);
        var selective = table.XmlIndexes.Find(x => x.Name == $"[sxi_{TableName}]");
        var forPath = table.XmlIndexes.Find(x => x.Name == $"[aux_{TableName}]");
        Assert.Multiple(() =>
        {
            Assert.That(selective?.Selective?.Paths, Has.Count.EqualTo(5), "the paths are extracted");
            Assert.That(selective!.Selective!.Paths.Find(p => p.Name == "pTotal")?.Type, Is.EqualTo("DECIMAL(10,2)"), "a SQL type as the catalog holds it");
            Assert.That(selective.Selective.Paths.Find(p => p.Name == "pName")?.MaxLength, Is.EqualTo(20));
            Assert.That(selective.Selective.Paths.Find(p => p.Name == "pLines")?.Type, Is.EqualTo("node()"));
            Assert.That(selective.Selective.Paths.Find(p => p.Name == "pInfer")?.Type, Is.Null, "an inferred type stays inferred");
            Assert.That(selective.Selective.Namespaces, Has.Count.EqualTo(2), "and the namespaces");
            Assert.That(forPath?.ForPath, Is.EqualTo("pTotal"), "a secondary carries its path");
            Assert.That(forPath!.PrimaryIndex, Is.EqualTo($"[sxi_{TableName}]"), "and its selective index");
        });

        Run(xmlTier, "TableQuench", "[" + JsonHelper.Serialize(table) + "]");
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, $"sxi_{TableName}"), Is.EqualTo(primary), "the extracted selective index compares equal to the live one");
            Assert.That(RowsetOf(xmlTier, $"aux_{TableName}"), Is.EqualTo(secondary), "and the extracted secondary");
        });
    }
}
