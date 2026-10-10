// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// The XML-ingest twin of <see cref="GraphTableExtractionTests"/>.
/// <para>The legacy tier is not only for genuinely old servers — it is selected below the OPENJSON compat
/// cliff, so it can run against a modern instance held at a low compatibility level, where graph tables
/// very much can exist. Fixing only the JSON generator would leave that path emitting GUID-named
/// pseudo-columns exactly as before.</para>
/// <para><b>Its own database on purpose.</b> Kindling the XML encoding replaces the helper procedures, so
/// sharing a database with the JSON fixture would make each one's result depend on which ran last.</para>
/// <para>The filter cannot be written the same way twice: <c>sys.columns.graph_type</c> is 2017+, and a
/// static reference to it is a CREATE-time "invalid column" error on an older binary. It is staged
/// through a version-guarded dynamic insert — the pattern <c>#TempStats</c> already uses for the 2012-only
/// <c>sys.stats.is_temporary</c> — and is simply empty below 2017, where graph tables cannot exist.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class GraphTableXmlExtractionTests
{
    private IDbConnection _connection;
    private string _db;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaGraphXml_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        _connection.ChangeDatabase(_db);

        using var cmd = _connection.CreateCommand();
        var serverMajor = TargetVersionDetector.Detect(cmd, Platform.SqlServer).ServerComparable;
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true,
            IngestEncoding.Xml, serverMajor, "warn");

        Exec("CREATE TABLE dbo.XmlGraphPerson (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL) AS NODE");
        Exec("CREATE TABLE dbo.XmlGraphKnows (Since DATE NULL) AS EDGE");
        Exec("CREATE TABLE dbo.XmlPlain (Id INT NOT NULL PRIMARY KEY, node_id_like NVARCHAR(10) NULL)");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            using var cmd = _connection.CreateCommand();
            cmd.CommandTimeout = 300;
            TestDatabaseDrop.Drop(cmd, _db);
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string Extract(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableXml @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetValue(0));
        return sb.ToString();
    }

    [Test]
    public void TheXmlTier_AlsoOmitsGraphPseudoColumnsAndTheirIndex()
    {
        var node = Extract("XmlGraphPerson");
        var edge = Extract("XmlGraphKnows");

        Assert.Multiple(() =>
        {
            Assert.That(node, Does.Contain("[Id]"), "real columns survive on the node table");
            Assert.That(edge, Does.Contain("[Since]"), "and on the edge table");

            foreach (var pseudo in new[] { "node_id", "graph_id", "GRAPH_UNIQUE_INDEX" })
                Assert.That(node, Does.Not.Contain(pseudo), $"node table still emits '{pseudo}'\n{node}");

            foreach (var pseudo in new[] { "edge_id", "from_id", "to_id", "from_obj_id", "to_obj_id", "graph_id" })
                Assert.That(edge, Does.Not.Contain(pseudo), $"edge table still emits '{pseudo}'\n{edge}");
        });
    }

    [Test]
    public void TheXmlTier_LeavesOrdinaryTablesAlone()
    {
        // The staged exclusion is a join on column_id, so an over-broad stage would silently empty
        // unrelated tables. This is the half that would catch that.
        var plain = Extract("XmlPlain");

        Assert.Multiple(() =>
        {
            Assert.That(plain, Does.Contain("[Id]"));
            Assert.That(plain, Does.Contain("[node_id_like]"),
                "a user column merely NAMED like a graph pseudo-column must survive -- the exclusion keys "
                + "off sys.columns.graph_type, not off the name");
        });
    }
}
