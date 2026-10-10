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
/// Extracting a partitioned SQL Server table must say so (#partitioning, K1).
/// <para><b>What this replaces is a silent loss, not a missing field.</b> The table-level
/// <c>[FileGroup]</c> read joins <c>sys.filegroups</c> on the index's <c>data_space_id</c>, and when that
/// data space is a partition SCHEME the join simply finds no row — so a partitioned table extracted
/// cleanly, reported success, and produced a package describing an ordinary unpartitioned table on the
/// default filegroup. Redeploying that package to a fresh target builds the wrong physical layout with no
/// error anywhere.</para>
/// <para>The scheme is emitted as a NAME, never a definition: SchemaSmith places tables on partitioning and
/// does not author it, the same contract <c>FileGroup</c> has. See <c>SqlServerTable.PartitionScheme</c>.
/// </para>
/// <para>Table and index are read independently on purpose. An index is not required to be aligned with
/// its table — a nonclustered index on a partitioned table may sit on one filegroup, and an index on an
/// ordinary heap may itself be partitioned — so neither is inferred from the other.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class PartitioningExtractionTests
{
    private IDbConnection _connection;
    private string _db;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"SchemaPartExtract_{Guid.NewGuid():N}"[..40];
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer)
            .GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();

        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{_db}]");
        _connection.ChangeDatabase(_db);

        using (var cmd = _connection.CreateCommand())
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        Exec("CREATE PARTITION FUNCTION pfExtract (INT) AS RANGE RIGHT FOR VALUES (100, 200)");
        Exec("CREATE PARTITION SCHEME psExtract AS PARTITION pfExtract ALL TO ([PRIMARY])");

        // Partitioned table, clustered index aligned to the same scheme.
        Exec("CREATE TABLE dbo.PartTable (Id INT NOT NULL, Val NVARCHAR(50) NULL) ON psExtract(Id)");
        Exec("CREATE CLUSTERED INDEX ixPartTable ON dbo.PartTable(Id) ON psExtract(Id)");

        // Ordinary table, ordinary index -- the control that proves nothing is emitted when nothing is
        // partitioned. Without it a bug that emitted the scheme unconditionally would look like a pass.
        Exec("CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, Val NVARCHAR(50) NULL)");

        // Partitioned index on an UNPARTITIONED heap: the index carries placement the table does not.
        Exec("CREATE TABLE dbo.PartIndexOnly (Id INT NOT NULL, Val NVARCHAR(50) NULL)");
        Exec("CREATE NONCLUSTERED INDEX ixPartIndexOnly ON dbo.PartIndexOnly(Id) ON psExtract(Id)");
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

    /// <summary>FOR JSON splits across rows at 2033 characters, so the whole reader has to be drained.</summary>
    private string Extract(string table, bool xml = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTable{(xml ? "Xml" : "Json")} @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        return sb.ToString();
    }

    [Test]
    public void APartitionedTable_EmitsItsSchemeAndColumn()
    {
        var json = Extract("PartTable");

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("PartitionScheme"),
                "A partitioned table that extracts without saying so produces a package describing an "
                + "ordinary table -- deploying it elsewhere silently builds the wrong physical layout.\n" + json);
            Assert.That(json, Does.Contain("psExtract"), json);
            Assert.That(json, Does.Contain("PartitionColumn"),
                "The scheme alone is not a placement: SQL Server needs the column the function is applied "
                + "to, and a package carrying one without the other cannot be deployed.\n" + json);
        });
    }

    [Test]
    public void AnAlignedIndex_EmitsItsOwnSchemeAndColumn()
    {
        var json = Extract("PartTable");

        Assert.That(json, Does.Contain("ixPartTable"), json);
        // The scheme name has to appear at least twice: once for the table, once for its aligned index.
        var first = json.IndexOf("psExtract", StringComparison.Ordinal);
        Assert.That(json.IndexOf("psExtract", first + 1, StringComparison.Ordinal), Is.GreaterThan(-1),
            "The index's placement is read independently of the table's, so an aligned index must carry the "
            + "scheme in its own right rather than relying on the table's.\n" + json);
    }

    [Test]
    public void APartitionedIndexOnAnUnpartitionedTable_EmitsTheIndexPlacementOnly()
    {
        var json = Extract("PartIndexOnly");

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("psExtract"),
                "The index is partitioned even though the table is not; inferring index placement from the "
                + "table would lose it.\n" + json);
            // The table itself is a heap on the default filegroup, so its own placement must stay silent.
            var tableSection = json[..json.IndexOf("\"Indexes\"", StringComparison.Ordinal)];
            Assert.That(tableSection, Does.Not.Contain("PartitionScheme"),
                "The table is NOT partitioned and must not claim to be.\n" + tableSection);
        });
    }

    [Test]
    public void AnOrdinaryTable_EmitsNoPartitioningAtAll()
    {
        var json = Extract("PlainTable");

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("PartitionScheme"),
                "Every existing package must keep extracting exactly as it did -- an unpartitioned table "
                + "gaining a partitioning property would churn every committed .json in the wild.\n" + json);
            Assert.That(json, Does.Not.Contain("PartitionColumn"), json);
        });
    }

    [Test]
    public void TheXmlExtractionPathAgrees()
    {
        // The XML path is the compat-100 twin of the JSON one: a SEPARATE procedure reading the same
        // catalog, which is exactly how the two drift when only one is changed. It is kindled only under
        // IngestEncoding.Xml, so this builds its own database rather than re-kindling the fixture's and
        // leaving the sibling tests dependent on execution order.
        var xmlDb = $"SchemaPartExtractXml_{Guid.NewGuid():N}"[..40];
        _connection.ChangeDatabase("master");
        Exec($"CREATE DATABASE [{xmlDb}]");
        try
        {
            _connection.ChangeDatabase(xmlDb);
            using (var cmd = _connection.CreateCommand())
                ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml);

            Exec("CREATE PARTITION FUNCTION pfExtractXml (INT) AS RANGE RIGHT FOR VALUES (100, 200)");
            Exec("CREATE PARTITION SCHEME psExtractXml AS PARTITION pfExtractXml ALL TO ([PRIMARY])");
            Exec("CREATE TABLE dbo.PartTable (Id INT NOT NULL, Val NVARCHAR(50) NULL) ON psExtractXml(Id)");
            Exec("CREATE CLUSTERED INDEX ixPartTable ON dbo.PartTable(Id) ON psExtractXml(Id)");

            var xml = Extract("PartTable", xml: true);

            Assert.Multiple(() =>
            {
                Assert.That(xml, Does.Contain("PartitionScheme"), xml);
                Assert.That(xml, Does.Contain("psExtractXml"), xml);
                Assert.That(xml, Does.Contain("PartitionColumn"), xml);
            });
        }
        finally
        {
            _connection.ChangeDatabase("master");
            using var cmd = _connection.CreateCommand();
            cmd.CommandTimeout = 300;
            TestDatabaseDrop.Drop(cmd, xmlDb);
            _connection.ChangeDatabase(_db);
        }
    }
}
