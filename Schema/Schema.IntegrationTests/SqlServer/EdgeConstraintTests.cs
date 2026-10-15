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
/// An edge table's <c>EdgeConstraints</c> deploy as <c>CONNECTION (...) ON DELETE ...</c> (SQL Server 2019+), survive a
/// redeploy untouched whatever order the clauses are written in, are re-created when a clause or the action changes,
/// are dropped when they leave the package, and extract with every clause. Assertions read <c>sys.edge_constraints</c>
/// and the constraint's object id, which a re-creation changes. Uses the suite's kindled database and isolates by name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class EdgeConstraintTests
{
    private const string Product = "EcTest";
    private const string Person = "EcPerson";
    private const string City = "EcCity";
    private const string Edge = "EcLivesIn";
    private const string Constraint = "EC_EcLivesIn";
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
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE '%Ec%'");
    }

    private void DropOwnObjects() => Exec($@"IF OBJECT_ID('dbo.{Edge}', 'U') IS NOT NULL DROP TABLE dbo.{Edge};
                                            IF OBJECT_ID('dbo.{Person}', 'U') IS NOT NULL DROP TABLE dbo.{Person};
                                            IF OBJECT_ID('dbo.{City}', 'U') IS NOT NULL DROP TABLE dbo.{City};
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

    private int ServerMajor() => Convert.ToInt32(Str("SELECT SchemaSmith.fn_ServerMajorVersion()"));

    private void Deploy(string json, bool whatIf = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{Product}', @TableDefinitions = N'{json.Replace("'", "''")}', @WhatIf = {(whatIf ? 1 : 0)}";
        cmd.ExecuteNonQuery();
    }

    private static string Node(string name) =>
        "{ \"Schema\": \"[dbo]\", \"Name\": \"[" + name + "]\", \"GraphType\": \"Node\", \"Columns\": [ { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false } ],"
        + " \"Indexes\": [ { \"Name\": \"[PK_" + name + "]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true } ] }";

    /// <param name="clauses">The Clauses array body, or null for an edge table with no constraint.</param>
    private static string Package(string clauses, string deleteAction = "CASCADE") =>
        "[" + Node(Person) + ", " + Node(City) + ","
        + " { \"Schema\": \"[dbo]\", \"Name\": \"[" + Edge + "]\", \"GraphType\": \"Edge\", \"Columns\": [ { \"Name\": \"[Since]\", \"DataType\": \"DATE\", \"Nullable\": true } ]"
        + (clauses == null ? "" : ", \"EdgeConstraints\": [ { \"Name\": \"[" + Constraint + "]\", \"Clauses\": [ " + clauses + " ], \"DeleteAction\": \"" + deleteAction + "\" } ]")
        + " }]";

    private const string PersonToCity = "{ \"FromTable\": \"[dbo].[" + Person + "]\", \"ToTable\": \"[dbo].[" + City + "]\" }";
    private const string CityToPerson = "{ \"FromTable\": \"" + City + "\", \"ToTable\": \"" + Person + "\" }";

    /// <summary>The constraint's action, its clauses (from>to, sorted) and its object id; null when it does not exist.</summary>
    private (string Action, string Clauses, int Oid)? Live()
    {
        var s = Str($@"SELECT ec.delete_referential_action_desc + '|' + (SELECT STRING_AGG(OBJECT_NAME(cl.from_object_id) + '>' + OBJECT_NAME(cl.to_object_id), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(cl.from_object_id)) FROM sys.edge_constraint_clauses cl WHERE cl.[object_id] = ec.[object_id]) + '|' + CONVERT(NVARCHAR(20), ec.[object_id])
                        FROM sys.edge_constraints ec WHERE ec.parent_object_id = OBJECT_ID('dbo.{Edge}') AND ec.[name] = '{Constraint}'");
        if (s == null) return null;
        var p = s.Split('|');
        return (p[0], p[1], int.Parse(p[2]));
    }

    private int Audits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'edgeConstraint' AND ActionType = '{action}' AND ObjectName LIKE '%{Constraint}%'");

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
    public void AnEdgeConstraint_Deploys_SurvivesARedeployInAnyClauseOrder_AndIsRecreatedWhenItChanges()
    {
        if (ServerMajor() < 15) Assert.Ignore("edge constraints are SQL Server 2019+");

        Deploy(Package(PersonToCity + ", " + CityToPerson));
        var first = Live();
        Assert.That(first, Is.Not.Null, "the constraint must exist");
        Assert.Multiple(() =>
        {
            Assert.That(first.Value.Action, Is.EqualTo("CASCADE"));
            Assert.That(first.Value.Clauses, Is.EqualTo($"{City}>{Person},{Person}>{City}"), "a bare clause table is resolved in the edge table's schema");
        });

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy(Package(CityToPerson + ", " + PersonToCity));
        Assert.Multiple(() =>
        {
            Assert.That(Live().Value.Oid, Is.EqualTo(first.Value.Oid), "the clauses are a set: written in another order they compare equal");
            Assert.That(Audits("dropped") + Audits("created"), Is.Zero);
        });

        Deploy(Package(PersonToCity + ", " + CityToPerson, deleteAction: "NO ACTION"));
        var changed = Live();
        Assert.Multiple(() =>
        {
            Assert.That(changed.Value.Action, Is.EqualTo("NO_ACTION"));
            Assert.That(changed.Value.Oid, Is.Not.EqualTo(first.Value.Oid), "a changed action is a drop and re-create");
        });

        Deploy(Package(PersonToCity, deleteAction: "NO ACTION"));
        Assert.That(Live().Value.Clauses, Is.EqualTo($"{Person}>{City}"), "a dropped clause re-creates the constraint with the rest");
    }

    [Test]
    public void AnEdgeConstraint_LeavingThePackage_IsDropped_AndWhatIfPreviewsIt()
    {
        if (ServerMajor() < 15) Assert.Ignore("edge constraints are SQL Server 2019+");
        Deploy(Package(PersonToCity));

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy(Package(null), whatIf: true);
        Assert.Multiple(() =>
        {
            Assert.That(Live(), Is.Not.Null, "WhatIf changes nothing");
            Assert.That(Audits("wouldDrop"), Is.EqualTo(1));
        });

        Deploy(Package(null));
        Assert.Multiple(() =>
        {
            Assert.That(Live(), Is.Null);
            Assert.That(Audits("dropped"), Is.EqualTo(1));
        });
    }

    [Test]
    public void Extraction_WritesTheConstraintWithEveryClause()
    {
        if (ServerMajor() < 15) Assert.Ignore("edge constraints are SQL Server 2019+");
        Deploy(Package(PersonToCity + ", " + CityToPerson));

        var json = Extract(Edge);
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
        var constraint = table.EdgeConstraints.Single();
        Assert.Multiple(() =>
        {
            Assert.That(constraint.Name, Is.EqualTo($"[{Constraint}]"));
            Assert.That(constraint.DeleteAction, Is.EqualTo("CASCADE"));
            Assert.That(constraint.Clauses.Select(c => c.FromTable + ">" + c.ToTable).OrderBy(x => x, StringComparer.Ordinal),
                Is.EqualTo(new[] { $"[dbo].[{City}]>[dbo].[{Person}]", $"[dbo].[{Person}]>[dbo].[{City}]" }), "schema-qualified, as the deploy resolves them:\n" + json);
        });

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy("[" + Node(Person) + ", " + Node(City) + ", " + json + "]");
        Assert.That(Audits("dropped") + Audits("created"), Is.Zero, "the extracted constraint redeploys as it was written");
    }
}
