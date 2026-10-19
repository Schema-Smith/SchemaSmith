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
/// JSON indexes (SQL Server 2025): one json column and a set of SQL/JSON paths, compared as the rendered FOR list. These
/// tests drive every deploy path (TableQuench and IndexOnly, on both ingest tiers) through create, an unchanged redeploy, a
/// change, and a redeploy of what extraction returns. A re-created index gets a new internal table, so "left alone" is
/// read from that table's <c>sys.partitions.partition_id</c>. Every package carries a second index over the whole document,
/// declared with no paths, so the default form is covered wherever the path list is.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class JsonIndexTests
{
    private const string TableName = "json_index_test";
    private const string Product = "JsonIndexProduct";
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
        _xmlTierDb = $"jsonidx_xmltier_{Guid.NewGuid().ToString()[..8]}";
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
        if (Scalar(_connection, "SELECT CASE WHEN OBJECT_ID('sys.json_indexes') IS NULL THEN 0 ELSE 1 END") != 1)
            Assert.Ignore("JSON indexes need SQL Server 2025.");
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
        return cmd.ExecuteScalar();
    }

    private static long Scalar(IDbConnection conn, string sql)
    {
        var v = Value(conn, sql);
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    private const string DocIndex = "jx_doc";
    private const string WholeIndex = "jx_whole";

    // The paths are written out of order on purpose: the FOR list is a set, so the order a package lists them in is not a change.
    private const string DeclaredPaths = "\"$.b\", \"$.a.c\"";

    private static string JsonIndexes(string paths = DeclaredPaths, bool arraySearch = true, int? fillFactor = 80, bool withDocIndex = true) =>
        "[ " + (withDocIndex
                   ? "{ \"Name\": \"" + DocIndex + "\", \"Column\": \"doc\", \"Paths\": [ " + paths + " ]"
                     + (arraySearch ? ", \"OptimizeForArraySearch\": true" : "")
                     + (fillFactor == null ? "" : ", \"FillFactor\": " + fillFactor) + " }, "
                   : "")
        + "{ \"Name\": \"" + WholeIndex + "\", \"Column\": \"whole\" } ]";

    private static string Package(string jsonIndexes, string keyColumns = "id") =>
        "[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\","
        + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"INT\", \"Nullable\": false }, { \"Name\": \"k\", \"DataType\": \"INT\", \"Nullable\": false }, { \"Name\": \"doc\", \"DataType\": \"JSON\", \"Nullable\": true },"
        + " { \"Name\": \"whole\", \"DataType\": \"JSON\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"" + keyColumns + "\" } ]"
        + (jsonIndexes == null ? "" : ", \"JsonIndexes\": " + jsonIndexes) + " }]";

    private static string Encode(bool xmlTier, string json) => xmlTier ? ModelXmlSerializer.ToIngestXml(json, "Tables", "Table") : json;

    private void Run(bool xmlTier, string proc, string json, bool dropUnknown = false)
    {
        using var cmd = Connection(xmlTier).CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.{proc} @ProductName = '{Product}', @TableDefinitions = @t" + (dropUnknown ? ", @DropUnknownIndexes = 1" : "");
        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = Encode(xmlTier, json);
        cmd.Parameters.Add(t);
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    // IndexOnly applies indexes to a table that exists, so that path deploys the bare table first.
    private void Deploy(bool xmlTier, bool indexOnly, string jsonIndexes, bool dropUnknown = false)
    {
        if (indexOnly && Scalar(Connection(xmlTier), $"SELECT COUNT(*) FROM sys.tables WHERE name = '{TableName}'") == 0)
            Run(xmlTier, "TableQuench", Package(null));
        Run(xmlTier, indexOnly ? "IndexOnlyQuench" : "TableQuench", Package(jsonIndexes), dropUnknown);
    }

    private long RowsetOf(bool xmlTier, string index) => Scalar(Connection(xmlTier),
        "SELECT MAX(p.partition_id) FROM sys.internal_tables it JOIN sys.partitions p ON p.[object_id] = it.[object_id] "
        + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}') AND it.parent_minor_id = INDEXPROPERTY(OBJECT_ID('dbo.{TableName}'), '{index}', 'IndexID')");

    private string PathsOf(bool xmlTier, string index) => Value(Connection(xmlTier),
        "SELECT STRING_AGG(p.[path], ' | ') WITHIN GROUP (ORDER BY p.[path] COLLATE Latin1_General_BIN2) FROM sys.json_index_paths p "
        + $"JOIN sys.json_indexes j ON j.[object_id] = p.[object_id] AND j.index_id = p.index_id WHERE j.[object_id] = OBJECT_ID('dbo.{TableName}') AND j.[name] = '{index}'") as string;

    private long Option(bool xmlTier, string index, string column) => Scalar(Connection(xmlTier),
        $"SELECT CONVERT(INT, {column}) FROM sys.json_indexes WHERE [object_id] = OBJECT_ID('dbo.{TableName}') AND [name] = '{index}'");

    private static readonly object[] Paths4 =
    [
        new object[] { false, false }, new object[] { false, true }, new object[] { true, false }, new object[] { true, true },
    ];

    [TestCaseSource(nameof(Paths4))]
    public void ADeclaredJsonIndex_IsCreated_AndAnUnchangedRedeployLeavesItAlone(bool xmlTier, bool indexOnly)
    {
        Deploy(xmlTier, indexOnly, JsonIndexes());

        Assert.Multiple(() =>
        {
            Assert.That(PathsOf(xmlTier, DocIndex), Is.EqualTo("$.a.c | $.b"), "the declared paths are indexed");
            Assert.That(Option(xmlTier, DocIndex, "optimize_for_array_search"), Is.EqualTo(1), "with array search");
            Assert.That(Option(xmlTier, DocIndex, "fill_factor"), Is.EqualTo(80), "and the fill factor");
            Assert.That(PathsOf(xmlTier, WholeIndex), Is.EqualTo("$"), "an index declared without paths covers the whole document");
            Assert.That(Option(xmlTier, WholeIndex, "optimize_for_array_search"), Is.Zero);
        });

        var doc = RowsetOf(xmlTier, DocIndex);
        var whole = RowsetOf(xmlTier, WholeIndex);
        Assert.That(doc, Is.GreaterThan(0), "the index's rowset is found");
        Deploy(xmlTier, indexOnly, JsonIndexes());
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.EqualTo(doc), "an unchanged redeploy leaves the JSON index alone");
            Assert.That(RowsetOf(xmlTier, WholeIndex), Is.EqualTo(whole), "and the whole-document one");
        });
    }

    [TestCaseSource(nameof(Paths4))]
    public void AChangedDeclaration_ReCreatesTheJsonIndex_AndAnOmittedFillFactorDoesNot(bool xmlTier, bool indexOnly)
    {
        Deploy(xmlTier, indexOnly, JsonIndexes());
        var whole = RowsetOf(xmlTier, WholeIndex);

        // JSON keys are case-sensitive, so a path that differs only in case is a different path.
        var doc = RowsetOf(xmlTier, DocIndex);
        Deploy(xmlTier, indexOnly, JsonIndexes("\"$.b\", \"$.A.c\""));
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.Not.EqualTo(doc), "a path changed in case alone re-creates the index");
            Assert.That(PathsOf(xmlTier, DocIndex), Is.EqualTo("$.A.c | $.b"), "with the new path");
            Assert.That(RowsetOf(xmlTier, WholeIndex), Is.EqualTo(whole), "and leaves the other index alone");
        });

        doc = RowsetOf(xmlTier, DocIndex);
        Deploy(xmlTier, indexOnly, JsonIndexes("\"$.b\", \"$.A.c\"", arraySearch: false));
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.Not.EqualTo(doc), "turning array search off re-creates it");
            Assert.That(Option(xmlTier, DocIndex, "optimize_for_array_search"), Is.Zero);
        });

        doc = RowsetOf(xmlTier, DocIndex);
        Deploy(xmlTier, indexOnly, JsonIndexes("\"$.b\", \"$.A.c\"", arraySearch: false, fillFactor: 90));
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.Not.EqualTo(doc), "a changed fill factor re-creates it");
            Assert.That(Option(xmlTier, DocIndex, "fill_factor"), Is.EqualTo(90));
        });

        doc = RowsetOf(xmlTier, DocIndex);
        Deploy(xmlTier, indexOnly, JsonIndexes("\"$.b\", \"$.A.c\"", arraySearch: false, fillFactor: null));
        Assert.That(RowsetOf(xmlTier, DocIndex), Is.EqualTo(doc), "a fill factor the package leaves out is not compared");
    }

    [TestCaseSource(nameof(Paths4))]
    public void AnUndeclaredJsonIndex_IsKept_UnlessUnknownIndexesAreDropped(bool xmlTier, bool indexOnly)
    {
        Deploy(xmlTier, indexOnly, JsonIndexes(withDocIndex: false));
        Exec(Connection(xmlTier), $"CREATE JSON INDEX jx_stray ON dbo.{TableName} (doc) FOR ('$.x')");

        Deploy(xmlTier, indexOnly, JsonIndexes(withDocIndex: false));
        Assert.That(PathsOf(xmlTier, "jx_stray"), Is.EqualTo("$.x"), "an undeclared JSON index is left alone by default");

        Deploy(xmlTier, indexOnly, JsonIndexes(withDocIndex: false), dropUnknown: true);
        Assert.Multiple(() =>
        {
            Assert.That(PathsOf(xmlTier, "jx_stray"), Is.Null, "and dropped as unknown when unknown indexes are dropped");
            Assert.That(PathsOf(xmlTier, WholeIndex), Is.EqualTo("$"), "while the declared one stays");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ARemovedJsonIndex_IsDropped(bool xmlTier)
    {
        Deploy(xmlTier, indexOnly: false, JsonIndexes());
        Deploy(xmlTier, indexOnly: false, JsonIndexes(withDocIndex: false));
        Assert.Multiple(() =>
        {
            Assert.That(PathsOf(xmlTier, DocIndex), Is.Null, "a JSON index the package no longer declares is dropped");
            Assert.That(PathsOf(xmlTier, WholeIndex), Is.EqualTo("$"));
        });
    }

    // A JSON index cannot exist without the clustered primary key, so a change to that key takes the JSON indexes with it.
    [TestCase(false)]
    [TestCase(true)]
    public void AClusteredKeyChange_DropsTheJsonIndexes_AndPutsThemBack(bool xmlTier)
    {
        Deploy(xmlTier, indexOnly: false, JsonIndexes());
        var doc = RowsetOf(xmlTier, DocIndex);

        Run(xmlTier, "TableQuench", Package(JsonIndexes(), keyColumns: "id, k"));

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(Connection(xmlTier), $"SELECT COUNT(*) FROM sys.index_columns WHERE [object_id] = OBJECT_ID('dbo.{TableName}') AND index_id = 1"),
                Is.EqualTo(2), "the key is re-created over both columns");
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.Not.EqualTo(doc).And.GreaterThan(0), "the JSON index went with it and is back");
            Assert.That(PathsOf(xmlTier, DocIndex), Is.EqualTo("$.a.c | $.b"));
            Assert.That(PathsOf(xmlTier, WholeIndex), Is.EqualTo("$"));
        });
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
        Deploy(xmlTier, indexOnly: false, JsonIndexes("\"$.b\", \"$.a.c\", \"$.\\\"quoted key\\\"\""));
        var doc = RowsetOf(xmlTier, DocIndex);
        var whole = RowsetOf(xmlTier, WholeIndex);

        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(Extract(xmlTier), Platform.SqlServer);
        var docIndex = table.JsonIndexes.Find(x => x.Name == $"[{DocIndex}]");
        var wholeIndex = table.JsonIndexes.Find(x => x.Name == $"[{WholeIndex}]");
        Assert.Multiple(() =>
        {
            Assert.That(docIndex?.Column, Is.EqualTo("[doc]"), "the index is extracted with its column");
            Assert.That(docIndex!.Paths, Is.EqualTo(new[] { "$.\"quoted key\"", "$.a.c", "$.b" }), "and its paths, sorted");
            Assert.That(docIndex.OptimizeForArraySearch, Is.True);
            Assert.That(docIndex.FillFactor, Is.EqualTo(80));
            Assert.That(wholeIndex?.Paths, Is.Empty, "a whole-document index is extracted without paths");
            Assert.That(wholeIndex!.OptimizeForArraySearch, Is.Null, "and without the option it does not have");
            Assert.That(table.Indexes.Exists(i => i.Name.Contains("jx_")), Is.False, "and never as an ordinary index");
        });

        Run(xmlTier, "TableQuench", "[" + JsonHelper.Serialize(table) + "]");
        Assert.Multiple(() =>
        {
            Assert.That(RowsetOf(xmlTier, DocIndex), Is.EqualTo(doc), "the extracted JSON index compares equal to the live one");
            Assert.That(RowsetOf(xmlTier, WholeIndex), Is.EqualTo(whole), "and the extracted whole-document one");
        });
    }
}

/// <summary>
/// Below SQL Server 2025 there are no JSON indexes (nor json columns), so a declared one is skipped with a downgrade and
/// the table deploys without it. Runs only where <c>sys.json_indexes</c> is missing.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class JsonIndexDegradeTests
{
    private const string TableName = "json_index_degrade_test";
    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _connection?.Dispose();

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt64(v);
    }

    [TestCase("TableQuench")]
    [TestCase("IndexOnlyQuench")]
    public void ADeclaredJsonIndex_IsSkippedWithADowngrade_AndTheTableDeploys(string proc)
    {
        if (Scalar("SELECT CASE WHEN OBJECT_ID('sys.json_indexes') IS NULL THEN 0 ELSE 1 END") == 1)
            Assert.Ignore("This server has JSON indexes; JsonIndexTests covers it.");
        Scalar($"DROP TABLE IF EXISTS dbo.{TableName}; DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID; SELECT 0");

        string Package(bool withIndex) =>
            "[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\","
            + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"INT\", \"Nullable\": false }, { \"Name\": \"doc\", \"DataType\": \"NVARCHAR(MAX)\", \"Nullable\": true } ],"
            + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"id\" } ]"
            + (withIndex ? ", \"JsonIndexes\": [ { \"Name\": \"jx_degrade\", \"Column\": \"doc\", \"Paths\": [ \"$.a\" ] } ]" : "") + " }]";
        void Run(string p, string json)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"EXEC SchemaSmith.{p} @ProductName = 'JsonIndexDegradeProduct', @TableDefinitions = @t";
            var t = cmd.CreateParameter();
            t.ParameterName = "@t";
            t.Value = json;
            cmd.Parameters.Add(t);
            cmd.CommandTimeout = 300;
            cmd.ExecuteNonQuery();
        }

        if (proc == "IndexOnlyQuench") Run("TableQuench", Package(withIndex: false));
        Run(proc, Package(withIndex: true));

        Assert.Multiple(() =>
        {
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.tables WHERE name = '{TableName}'"), Is.EqualTo(1), "the table deploys");
            Assert.That(Scalar($"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{TableName}') AND [name] = 'jx_degrade'"), Is.Zero, "without the index");
            Assert.That(Scalar("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'downgraded' "
                               + $"AND ObjectType = 'JSON index (SQL Server 2025)' AND ObjectName LIKE '%jx_degrade%'"), Is.EqualTo(1), "and records the downgrade");
        });
        Scalar($"DROP TABLE IF EXISTS dbo.{TableName}; SELECT 0");
    }
}
