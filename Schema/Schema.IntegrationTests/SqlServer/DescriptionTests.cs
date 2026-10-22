// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// <c>Description</c> on a table, column and index is the <c>MS_Description</c> extended property. A declared value is
/// set, a changed one updated, <c>""</c> removes it, and an omitted one leaves whatever the database has: a package that
/// never mentions descriptions never touches the ones a DBA wrote. The pre-2.8 spelling, an <c>MS_Description</c> inside
/// <c>Extensions.ExtendedProperties</c>, still deploys, and extraction writes <c>Description</c> and keeps it out of the bag.
/// Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class DescriptionTests
{
    private const string Product = "DescTest";
    private const string Table = "DescOrders";
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
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE 'dbo.{Table}%' OR ObjectName LIKE '[[]dbo].[[]{Table}]%'");
    }

    private void DropOwnObjects() => Exec($@"IF OBJECT_ID('dbo.{Table}', 'U') IS NOT NULL DROP TABLE dbo.{Table};
                                            DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = '{Product}';");

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private string ScalarStr(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(ScalarStr(sql) ?? "0");

    private void Deploy(string json, bool whatIf = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{Product}', @TableDefinitions = N'{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @WhatIf = {(whatIf ? 1 : 0)}";
        cmd.ExecuteNonQuery();
    }

    private void DeployIndexesOnly(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{Product}', @TableDefinitions = N'{json.Replace("'", "''")}', @DropIndexesRemovedFromProduct = 0";
        cmd.ExecuteNonQuery();
    }

    private static string Desc(string description) => description == null ? "" : $", \"Description\": \"{description.Replace("\"", "\\\"")}\"";

    private static string Package(string table = null, string id = null, string pk = null, string ix = null, string extra = "") =>
        $"[{{ \"Schema\": \"[dbo]\", \"Name\": \"[{Table}]\"{Desc(table)}{extra}, \"Columns\": ["
        + $" {{ \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false{Desc(id)} }},"
        + " { \"Name\": \"[V]\", \"DataType\": \"INT\", \"Nullable\": true } ], \"Indexes\": ["
        + $" {{ \"Name\": \"[PK_{Table}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true{Desc(pk)} }},"
        + $" {{ \"Name\": \"[IX_{Table}_V]\", \"IndexColumns\": \"[V]\"{Desc(ix)} }} ] }}]";

    private string MsDescription(string level2 = ", default, default") =>
        ScalarStr($"SELECT CONVERT(NVARCHAR(MAX), [value]) FROM fn_listextendedproperty(N'MS_Description', N'SCHEMA', N'dbo', N'TABLE', N'{Table}'{level2})");

    private string TableDescription() => MsDescription();
    private string ColumnDescription(string column = "Id") => MsDescription($", N'COLUMN', N'{column}'");
    private string PkDescription() => MsDescription($", N'CONSTRAINT', N'PK_{Table}'");
    private string IndexDescription() => MsDescription($", N'INDEX', N'IX_{Table}_V'");

    private int Audits(string objectType, string action, string nameSuffix = "") =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = '{objectType}' AND ActionType = '{action}' AND ObjectName = 'dbo.{Table}{nameSuffix}'");

    [Test]
    public void DeclaredDescriptions_AreApplied_ToTableColumnKeyConstraintAndIndex()
    {
        Deploy(Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.EqualTo("all the orders"));
            Assert.That(ColumnDescription(), Is.EqualTo("the key"));
            Assert.That(PkDescription(), Is.EqualTo("the key index"), "a primary key's property sits on the constraint");
            Assert.That(IndexDescription(), Is.EqualTo("the lookup"));
            Assert.That(ColumnDescription("V"), Is.Null, "a column that declares nothing gets nothing");
            Assert.That(Audits("table", "modified"), Is.EqualTo(1));
            Assert.That(Audits("column", "modified", ".Id"), Is.EqualTo(1));
            Assert.That(Audits("index", "modified", $".PK_{Table}"), Is.EqualTo(1));
            Assert.That(Audits("index", "modified", $".IX_{Table}_V"), Is.EqualTo(1));
        });
    }

    [Test]
    public void AChangedDescription_IsUpdatedInPlace_AndAnUnchangedOne_IsNotTouched()
    {
        Deploy(Package(table: "before", id: "stays the same"));
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

        Deploy(Package(table: "after, with Bob's apostrophe", id: "stays the same"));

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.EqualTo("after, with Bob's apostrophe"));
            Assert.That(Audits("table", "modified"), Is.EqualTo(1));
            Assert.That(Audits("column", "modified", ".Id"), Is.Zero, "a description that already matches is not rewritten");
        });
    }

    [Test]
    public void ACaseOnlyChange_IsAChange()
    {
        Deploy(Package(table: "orders"));
        Deploy(Package(table: "Orders"));

        Assert.That(TableDescription(), Is.EqualTo("Orders"), "the comparison is binary, not the database collation's");
    }

    [Test]
    public void AnOmittedDescription_LeavesWhatTheDatabaseHas()
    {
        Deploy(Package());
        Exec($"EXEC sp_addextendedproperty N'MS_Description', N'written by a DBA', N'SCHEMA', N'dbo', N'TABLE', N'{Table}';"
             + $"EXEC sp_addextendedproperty N'MS_Description', N'also by a DBA', N'SCHEMA', N'dbo', N'TABLE', N'{Table}', N'COLUMN', N'Id';");
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

        Deploy(Package());

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.EqualTo("written by a DBA"), "a package that says nothing about descriptions manages none");
            Assert.That(ColumnDescription(), Is.EqualTo("also by a DBA"));
            Assert.That(Count("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'modified'"), Is.Zero);
        });
    }

    [Test]
    public void AnEmptyDescription_RemovesIt()
    {
        Deploy(Package(table: "doomed", ix: "also doomed"));

        Deploy(Package(table: "", ix: ""));

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.Null);
            Assert.That(IndexDescription(), Is.Null);
        });

        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
        Deploy(Package(table: "", ix: ""));
        Assert.That(Count("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'modified'"), Is.Zero,
            "removing what is already absent is not a change");
    }

    [Test]
    public void TheLegacyMsDescriptionInsideExtendedProperties_StillDeploys_AndDescriptionWinsWhenBothAreThere()
    {
        var bag = ", \"Extensions\": { \"ExtendedProperties\": { \"MS_Description\": \"from the bag\" } }";
        var json = Package(extra: bag)
            .Replace("\"DataType\": \"INT\", \"Nullable\": false }", "\"DataType\": \"INT\", \"Nullable\": false, \"Description\": \"from the key\"" + bag + " }");

        Deploy(json);

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.EqualTo("from the bag"), "the pre-2.8 spelling is honoured");
            Assert.That(ColumnDescription(), Is.EqualTo("from the key"), "and Description wins when both are present");
        });
    }

    [Test]
    public void ARedeployOfAnUnchangedPackage_RecordsNothing()
    {
        Deploy(Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

        Deploy(Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));

        Assert.That(Count("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'modified'"), Is.Zero);
    }

    [Test]
    public void WhatIf_ChangesNothing_AndRecordsWhatItWouldChange()
    {
        Deploy(Package(table: "before"));
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");

        Deploy(Package(table: "after", id: "new"), whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(TableDescription(), Is.EqualTo("before"));
            Assert.That(ColumnDescription(), Is.Null);
            Assert.That(Audits("table", "wouldModify"), Is.EqualTo(1));
            Assert.That(Audits("column", "wouldModify", ".Id"), Is.EqualTo(1));
            Assert.That(Count("SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'modified'"), Is.Zero);
        });
    }

    [Test]
    public void IndexOnlyQuench_ConvergesIndexDescriptions_Too()
    {
        Deploy(Package(ix: "before"));

        DeployIndexesOnly(Package(ix: "after"));

        Assert.That(IndexDescription(), Is.EqualTo("after"));
    }

    [Test]
    public void Extraction_WritesDescription_AndKeepsItOutOfTheExtendedPropertiesBag()
    {
        Deploy(Package(table: "all the orders", id: "the key", pk: "the key index", ix: "the lookup"));
        Exec($"EXEC sp_addextendedproperty N'OwningTeam', N'Billing', N'SCHEMA', N'dbo', N'TABLE', N'{Table}';");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{Table}'";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader.GetString(0);
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(table.Description, Is.EqualTo("all the orders"));
            Assert.That(table.Columns.Single(c => c.Name == "[Id]").Description, Is.EqualTo("the key"));
            Assert.That(table.Columns.Single(c => c.Name == "[V]").Description, Is.Null);
            Assert.That(table.Indexes.Single(i => i.Name == $"[PK_{Table}]").Description, Is.EqualTo("the key index"), "read from the constraint");
            Assert.That(table.Indexes.Single(i => i.Name == $"[IX_{Table}_V]").Description, Is.EqualTo("the lookup"));
            Assert.That((string)((JObject)table.Extensions)?["ExtendedProperties"]?["OwningTeam"], Is.EqualTo("Billing"), "the rest of the bag is untouched");
            Assert.That(json, Does.Not.Contain("MS_Description"), "it has a key of its own now");
        });
    }
}
