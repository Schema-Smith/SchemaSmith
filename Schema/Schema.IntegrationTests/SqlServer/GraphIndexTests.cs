// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// An index over an edge table's <c>$from_id</c> / <c>$to_id</c> deploys from a package that names them, survives a
/// redeploy untouched, and extracts under those names -- the four hidden columns SQL Server stores it over carry
/// per-table GUIDs no package could carry. The engine's own GRAPH_UNIQUE_INDEX is neither extracted nor dropped as
/// unknown. Assertions read the catalog and the change audit. Uses the suite's kindled database and isolates by name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class GraphIndexTests
{
    private const string Product = "GixTest";
    private const string Node = "GixPerson";
    private const string Edge = "GixKnows";
    private const string Index = "IX_GixKnows_FromTo";
    private IDbConnection _connection;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        DropOwnObjects();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try { DropOwnObjects(); }
        finally { _connection.Close(); _connection.Dispose(); }
    }

    [SetUp]
    public void SetUp()
    {
        DropOwnObjects();
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%Gix%'");
    }

    private void DropOwnObjects() => Exec($@"IF OBJECT_ID('dbo.{Edge}', 'U') IS NOT NULL DROP TABLE dbo.{Edge};
                                            IF OBJECT_ID('dbo.{Node}', 'U') IS NOT NULL DROP TABLE dbo.{Node};
                                            DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{Product}';");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(Str(sql) ?? "0");

    private void Deploy(string json, bool dropUnknownIndexes = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{Product}', @TableDefinitions = N'{json.Replace("'", "''")}', @DropUnknownIndexes = {(dropUnknownIndexes ? 1 : 0)}";
        cmd.ExecuteNonQuery();
    }

    private static string Package(string indexColumns) =>
        "[{ \"Schema\": \"[dbo]\", \"Name\": \"[" + Node + "]\", \"GraphType\": \"Node\", \"Columns\": [ { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false } ],"
        + " \"Indexes\": [ { \"Name\": \"[PK_" + Node + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true } ] },"
        + " { \"Schema\": \"[dbo]\", \"Name\": \"[" + Edge + "]\", \"GraphType\": \"Edge\", \"Columns\": [ { \"Name\": \"[Since]\", \"DataType\": \"DATE\", \"Nullable\": true } ]"
        + (indexColumns == null ? "" : ", \"Indexes\": [ { \"Name\": \"[" + Index + "]\", \"IndexColumns\": \"" + indexColumns + "\" } ]")
        + " }]";

    /// <summary>The index's key columns as the catalog sees them: the graph type of each, in key order.</summary>
    private string KeyGraphTypes() => Str(
        $"SELECT STRING_AGG(CONVERT(NVARCHAR(10), c.graph_type), ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.indexes i JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id WHERE i.[object_id] = OBJECT_ID('dbo.{Edge}') AND i.[name] = '{Index}'");

    private int IndexAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = '{action}' AND ObjectName LIKE '%{Index}%'");

    private int GraphUniqueIndexes() => Count($"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{Edge}') AND [name] LIKE 'GRAPH_UNIQUE_INDEX%'");

    private string Extract(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        return sb.ToString();
    }

    [Test]
    public void AnIndexOverTheFromAndToIds_Deploys_AndARedeployLeavesIt()
    {
        Deploy(Package("$from_id,$to_id"));
        Assert.That(KeyGraphTypes(), Is.EqualTo("4,3,7,6"), "the two pseudo-columns are stored as the from (obj_id, id) and to (obj_id, id) pairs");

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy(Package("$from_id,$to_id"), dropUnknownIndexes: true);
        Assert.Multiple(() =>
        {
            Assert.That(IndexAudits("dropped"), Is.Zero, "the catalog's four columns compare as the package's two names");
            Assert.That(IndexAudits("created"), Is.Zero);
            Assert.That(KeyGraphTypes(), Is.EqualTo("4,3,7,6"));
            Assert.That(GraphUniqueIndexes(), Is.EqualTo(1), "the engine's own index is not an unknown index to drop");
        });
    }

    [Test]
    public void Extraction_WritesTheIndexUnderThePseudoColumnNames_AndNotTheEnginesOwnIndex()
    {
        Deploy(Package("$to_id"));

        var json = Extract(Edge);
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
        Assert.Multiple(() =>
        {
            Assert.That(table.Indexes.Select(i => i.Name), Is.EqualTo(new[] { $"[{Index}]" }), "the user's index, and not GRAPH_UNIQUE_INDEX:\n" + json);
            Assert.That(table.Indexes.Single().IndexColumns, Is.EqualTo("$to_id"));
            Assert.That(json, Does.Not.Contain("to_obj_id").And.Not.Contain("to_id_"), "no GUID-suffixed column name reaches the package");
        });

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy("[" + json + "]"); // the extracted table redeploys as it was written
        Assert.Multiple(() =>
        {
            Assert.That(KeyGraphTypes(), Is.EqualTo("7,6"));
            Assert.That(IndexAudits("dropped") + IndexAudits("created"), Is.Zero, "and changes nothing");
        });
    }

    [Test]
    public void AnIndexOverTheFromId_OnANodeTable_IsRefused()
    {
        var json = Package("$from_id").Replace("\"GraphType\": \"Edge\"", "\"GraphType\": \"Node\"");
        var ex = Assert.Catch<Exception>(() => Deploy(json));
        Assert.That(ex.Message, Does.Contain("$from_id"), "a node table has no $from_id; the engine refuses it by name, and the deploy passes the name through");
    }
}
