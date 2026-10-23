// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// SQL Server <c>XML_COMPRESSION</c> — the sibling of <c>DATA_COMPRESSION</c>, and independent of it.
/// <para><b>The version story is asymmetric, and it is the whole reason this was awkward.</b> Verified
/// live rather than read: the clause DEPLOYS from SQL Server 2022, but
/// <c>sys.partitions.xml_compression</c> is missing on 2022 CU25 (the column is only on
/// <c>sys.internal_partitions</c>, which reports NULL for an ordinary table) and present on 2022 CU26 and
/// 2025. So a server without it honours the setting and can never report it — the same shape as MariaDB
/// application-time periods (declarable 10.4.3, readable 11.4).</para>
/// <para><b>Two consequences fall out of that, and both are tested here.</b> First, a procedure naming
/// the column fails to CREATE on a server that lacks it — binding happens before any runtime <c>IF</c> —
/// so the reference is composed in or out at KINDLE time, by asking the server whether the column is there.
/// Second, extraction where it is missing returns nothing for it, so SchemaTongs carries the value forward
/// from the package it is refreshing rather than stripping a property the server is honouring.</para>
/// <para>These tests run against whatever the fixture points at and adapt: the round-trip and drift
/// assertions bite wherever the server reports the column, and the deploy assertions bite from 2022.</para>
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class XmlCompressionTests
{
    private const string TableName = "xml_compression_test";
    private IDbConnection _connection = null!;
    private int _major;
    private bool _reports;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _major = Convert.ToInt32(Scalar("SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))"));
        _reports = Convert.ToInt32(Scalar("SELECT CASE WHEN COL_LENGTH('sys.partitions', 'xml_compression') IS NULL THEN 0 ELSE 1 END")) == 1;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        if (_major < 16)
            Assert.Ignore($"XML_COMPRESSION requires SQL Server 2022; this server is major {_major}. "
                          + "The degrade path is covered by GenuineOldBinary.OldBinaryXmlCompressionDegradeTests.");
        Exec($"DROP TABLE IF EXISTS dbo.{TableName}");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        return cmd.ExecuteScalar();
    }

    private static string Package(bool tableCompressed, bool indexCompressed, string xmlIndexes = null) =>
        "[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\""
        + (tableCompressed ? ", \"XmlCompression\": true" : "")
        + ", \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"doc\", \"DataType\": \"XML\", \"Nullable\": true },"
        + " { \"Name\": \"v\", \"DataType\": \"NVARCHAR(50)\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true, \"IndexColumns\": \"id\" },"
        + " { \"Name\": \"ix_" + TableName + "\", \"IndexColumns\": \"v\""
        + (indexCompressed ? ", \"XmlCompression\": true" : "") + " } ]"
        + (xmlIndexes == null ? "" : ", \"XmlIndexes\": " + xmlIndexes) + " }]";

    private void Deploy(bool tableCompressed, bool indexCompressed = false, string xmlIndexes = null)
    {
        var json = Package(tableCompressed, indexCompressed, xmlIndexes).Replace("'", "''");
        Exec($"EXEC SchemaSmith.TableQuench @ProductName = 'XmlCompProduct', @TableDefinitions = '{json}'");
    }

    /// <summary>
    /// FOR JSON returns the document in 2033-character CHUNKS, one row each -- ExecuteScalar would take
    /// only the first, which is how this first "passed" while comparing against the string "{".
    /// </summary>
    private string ExtractedJson()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{TableName}'";
        cmd.CommandTimeout = 300;
        using var reader = cmd.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (reader.Read()) sb.Append(reader.GetValue(0));
        return sb.ToString();
    }

    /// <summary>Reads the live setting. Only answerable where the server reports the column; null elsewhere.</summary>
    private bool? LiveTableSetting()
    {
        if (!_reports) return null;
        var v = Scalar($"SELECT MAX(CONVERT(TINYINT, p.xml_compression)) FROM sys.partitions p "
                       + $"WHERE p.object_id = OBJECT_ID('dbo.{TableName}') AND p.index_id < 2");
        return v == null || v == DBNull.Value ? null : Convert.ToInt32(v) == 1;
    }

    [Test]
    public void ADeclaredTableXmlCompression_Deploys()
    {
        Deploy(tableCompressed: true);

        Assert.That(Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM sys.tables WHERE name = '{TableName}'")), Is.EqualTo(1),
            "the table has to deploy at all -- an ungated clause is a parser error below 2022");

        if (_reports)
            Assert.That(LiveTableSetting(), Is.True, "and where the server reports it, it must report it on");
    }

    [Test]
    public void ADeclaredIndexXmlCompression_Deploys()
    {
        // The property is per-object: an index does not inherit its table's setting.
        Deploy(tableCompressed: false, indexCompressed: true);

        Assert.That(Convert.ToInt32(Scalar(
            $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.{TableName}') AND name = 'ix_{TableName}'")),
            Is.EqualTo(1));
    }

    [Test]
    public void ATableWithNoXmlColumn_StillAcceptsTheClause()
    {
        // Probed on 2022: unlike TEXTIMAGE_ON, which SQL Server rejects outright on a table with no
        // large-object column (error 1709), XML_COMPRESSION is accepted on a table with no xml column.
        // So no LOB-style guard is needed, and this test is what stops one being added defensively.
        var json = ("[{ \"Schema\": \"dbo\", \"Name\": \"" + TableName + "\", \"XmlCompression\": true,"
                    + " \"Columns\": [ { \"Name\": \"id\", \"DataType\": \"INT\", \"Nullable\": false } ],"
                    + " \"Indexes\": [ { \"Name\": \"pk_" + TableName + "\", \"PrimaryKey\": true, \"Unique\": true, \"IndexColumns\": \"id\" } ] }]")
                   .Replace("'", "''");

        Assert.DoesNotThrow(() => Exec($"EXEC SchemaSmith.TableQuench @ProductName = 'XmlCompProduct', @TableDefinitions = '{json}'"));
    }

    [Test]
    public void RedeployingAnUnchangedDeclaration_IsIdempotent()
    {
        // THE expensive-mistake guard. Where the catalog cannot report the current value, a
        // comparison that read "cannot report" as "currently off" would REBUILD every declared-ON table
        // on every deploy -- a full data rebuild, forever. The drift block is version-guarded so it
        // simply does not run there.
        Deploy(tableCompressed: true);
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Deploy(tableCompressed: true);

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToInt32(Scalar(
                $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ActionType = 'modified' AND ObjectName LIKE '%{TableName}%'")),
                Is.Zero, "a second identical deploy must not rebuild the table");
            // The clustered key declares no XML compression of its own; compared alone, it was rebuilt off, with no audit row.
            if (_reports) Assert.That(LiveTableSetting(), Is.True, "and the table keeps its XML compression");
        });
    }

    [Test]
    public void TurningItOff_Converges_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("Drift detection needs sys.partitions.xml_compression (2025, and 2022 from CU26). "
                          + "Without it the setting is applied at create and deliberately never re-evaluated.");

        Deploy(tableCompressed: true);
        Assert.That(LiveTableSetting(), Is.True, "precondition");

        Deploy(tableCompressed: false);

        Assert.That(LiveTableSetting(), Is.False, "where the server CAN report the setting, drift must converge");
    }

    [Test]
    public void ItRoundTripsThroughExtraction_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("sys.partitions.xml_compression is missing on this server, so extraction structurally "
                          + "cannot see the setting; ImportTableHelperTests.XmlCompression_IsCarriedForward_WhenTheSourceCannotReportIt covers what "
                          + "happens instead.");

        Deploy(tableCompressed: true);

        var json = ExtractedJson();
        Assert.That(json, Does.Contain("XmlCompression"), json);
    }

    [Test]
    public void ATableWithoutIt_ExtractsWithoutTheProperty()
    {
        // No-churn: emitting the property for every table would rewrite every committed SQL Server
        // package for a setting nobody declared.
        Deploy(tableCompressed: false);

        var json = ExtractedJson();
        Assert.That(json, Does.Not.Contain("XmlCompression"), json);
    }

    private static string PrimaryAndSecondary(bool primaryCompressed, bool secondaryCompressed) =>
        "[ { \"Name\": \"pxml_" + TableName + "\", \"IsPrimary\": true, \"Column\": \"doc\""
        + (primaryCompressed ? ", \"XmlCompression\": true" : "") + " },"
        + " { \"Name\": \"sxml_" + TableName + "\", \"IsPrimary\": false, \"Column\": \"doc\", \"PrimaryIndex\": \"pxml_" + TableName + "\", \"SecondaryIndexType\": \"VALUE\""
        + (secondaryCompressed ? ", \"XmlCompression\": true" : "") + " } ]";

    /// <summary>An XML index's rows live in an internal node table; its index of the same name holds the compression.</summary>
    private bool? LiveXmlIndexSetting(string indexName)
    {
        if (!_reports) return null;
        var v = Scalar("SELECT MAX(CONVERT(TINYINT, p.xml_compression)) FROM sys.internal_tables it "
                       + "JOIN sys.indexes xsi ON xsi.object_id = it.object_id AND xsi.name = '" + indexName + "' "
                       + "JOIN sys.partitions p ON p.object_id = xsi.object_id AND p.index_id = xsi.index_id "
                       + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}')");
        return v == null || v == DBNull.Value ? null : Convert.ToInt32(v) == 1;
    }

    private int XmlIndexAudits(string action) => Convert.ToInt32(Scalar(
        $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE ObjectType = 'xmlIndex' AND ActionType = '{action}' AND ObjectName LIKE '%{TableName}%'"));

    [Test]
    public void ADeclaredXmlIndexXmlCompression_Deploys_AndASecondaryKeepsItsOwnSetting()
    {
        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: false));

        Assert.That(Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM sys.xml_indexes WHERE object_id = OBJECT_ID('dbo.{TableName}')")),
            Is.EqualTo(2), "both XML indexes deploy");
        if (_reports)
            Assert.Multiple(() =>
            {
                Assert.That(LiveXmlIndexSetting($"pxml_{TableName}"), Is.True, "the primary is compressed");
                Assert.That(LiveXmlIndexSetting($"sxml_{TableName}"), Is.False, "the secondary does not inherit it");
            });
    }

    [Test]
    public void RedeployingAnUnchangedXmlIndexDeclaration_DoesNotReCreateIt()
    {
        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: true));
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%{TableName}%'");

        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: true));

        Assert.That(XmlIndexAudits("created"), Is.Zero, "an unchanged compressed XML index is left alone");
    }

    [Test]
    public void ChangingAnXmlIndexsCompression_ReCreatesIt_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("Drift detection needs sys.partitions.xml_compression (2025, and 2022 from CU26).");

        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: false));

        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: false, secondaryCompressed: true));

        Assert.Multiple(() =>
        {
            Assert.That(LiveXmlIndexSetting($"pxml_{TableName}"), Is.False, "the primary's compression was turned off");
            Assert.That(LiveXmlIndexSetting($"sxml_{TableName}"), Is.True, "the secondary's was turned on");
        });
    }

    [Test]
    public void AnXmlIndexsCompression_RoundTripsThroughExtraction_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("sys.partitions.xml_compression is missing on this server, so extraction cannot see the setting.");

        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: false));

        var json = ExtractedJson();
        var xmlIndexes = json[json.IndexOf("\"XmlIndexes\"", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(xmlIndexes, Does.Match("pxml_" + TableName + @"[^}]*""XmlCompression"":\s*true"), json);
            Assert.That(xmlIndexes, Does.Not.Match("sxml_" + TableName + @"[^}]*XmlCompression"), "an uncompressed one carries no property: " + json);
        });
    }

    private static void IndexOnly(IDbCommand cmd, string tableDefinitions)
    {
        cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'XmlCompProduct', @TableDefinitions = @t";
        cmd.Parameters.Clear();
        var t = cmd.CreateParameter();
        t.ParameterName = "@t";
        t.Value = tableDefinitions;
        cmd.Parameters.Add(t);
        cmd.CommandTimeout = 300;
        try { cmd.ExecuteNonQuery(); }
        finally { cmd.Parameters.Clear(); }
    }

    private static int ScalarOn(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? -1 : Convert.ToInt32(v);
    }

    private static int LiveXmlIndexSettingOn(IDbCommand cmd, string indexName) => ScalarOn(cmd,
        "SELECT MAX(CONVERT(TINYINT, p.xml_compression)) FROM sys.internal_tables it "
        + "JOIN sys.indexes xsi ON xsi.object_id = it.object_id AND xsi.name = '" + indexName + "' "
        + "JOIN sys.partitions p ON p.object_id = xsi.object_id AND p.index_id = xsi.index_id "
        + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}')");

    // Index-only writes no audit row for an XML index; a re-created one gets a new internal node table.
    private static int NodeTableIdOn(IDbCommand cmd, string indexName) => ScalarOn(cmd,
        "SELECT it.object_id FROM sys.internal_tables it "
        + "JOIN sys.indexes xsi ON xsi.object_id = it.object_id AND xsi.name = '" + indexName + "' "
        + $"WHERE it.parent_object_id = OBJECT_ID('dbo.{TableName}')");

    /// <summary>Index-only deploys apply and converge the setting, on the ingest tier the database was kindled with.</summary>
    private void AssertIndexOnlyConverges(IDbCommand cmd, Func<string, string> encode)
    {
        IndexOnly(cmd, encode(Package(false, false, PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: false))));
        Assert.Multiple(() =>
        {
            Assert.That(LiveXmlIndexSettingOn(cmd, $"pxml_{TableName}"), Is.EqualTo(1), "index-only turns the primary's compression on");
            Assert.That(LiveXmlIndexSettingOn(cmd, $"sxml_{TableName}"), Is.EqualTo(0), "and leaves the secondary's off");
        });

        var primaryBefore = NodeTableIdOn(cmd, $"pxml_{TableName}");
        var secondaryBefore = NodeTableIdOn(cmd, $"sxml_{TableName}");
        Assert.That(primaryBefore, Is.GreaterThan(0), "the primary's node table is found");
        IndexOnly(cmd, encode(Package(false, false, PrimaryAndSecondary(primaryCompressed: true, secondaryCompressed: false))));
        Assert.Multiple(() =>
        {
            Assert.That(NodeTableIdOn(cmd, $"pxml_{TableName}"), Is.EqualTo(primaryBefore), "an unchanged index-only redeploy leaves the primary alone");
            Assert.That(NodeTableIdOn(cmd, $"sxml_{TableName}"), Is.EqualTo(secondaryBefore), "and the secondary");
        });
    }

    [Test]
    public void IndexOnly_AppliesAndConvergesAnXmlIndexsCompression_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("Drift detection needs sys.partitions.xml_compression (2025, and 2022 from CU26).");
        Deploy(tableCompressed: false, xmlIndexes: PrimaryAndSecondary(primaryCompressed: false, secondaryCompressed: false));

        using var cmd = _connection.CreateCommand();
        AssertIndexOnlyConverges(cmd, json => json);
    }

    [Test]
    public void IndexOnly_OnTheXmlIngestTier_AppliesAndConvergesAnXmlIndexsCompression_WhereTheServerReportsIt()
    {
        if (!_reports)
            Assert.Ignore("Drift detection needs sys.partitions.xml_compression (2025, and 2022 from CU26).");
        // The XML tier is its own procedure set, so it needs a database kindled with it.
        var db = $"xmlcomp_xmltier_{Guid.NewGuid().ToString()[..8]}";
        Exec($"CREATE DATABASE [{db}]");
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(FixtureSetup.GetMainDbConnectionString()) { InitialCatalog = db };
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(builder.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, IngestEncoding.Xml);
            string ToXml(string json) => ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = 'XmlCompProduct', @TableDefinitions = @t";
            var t = cmd.CreateParameter();
            t.ParameterName = "@t";
            t.Value = ToXml(Package(false, false, PrimaryAndSecondary(primaryCompressed: false, secondaryCompressed: false)));
            cmd.Parameters.Add(t);
            cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();

            AssertIndexOnlyConverges(cmd, ToXml);
            conn.Close();
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            using var master = _connection.CreateCommand();
            TestDatabaseDrop.Drop(master, db);
        }
    }
}
