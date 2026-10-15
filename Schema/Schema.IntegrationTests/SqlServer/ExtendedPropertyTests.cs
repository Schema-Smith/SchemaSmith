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
/// A component's <c>Extensions.ExtendedProperties</c> bag deploys, name by name, onto the table, column, index, foreign
/// key or check constraint it sits on. A declared value is set, a changed one updated, an empty one (<c>""</c> or
/// <c>null</c>) removes the property, and a name the package does not mention is left alone. SchemaSmith's own stamps are
/// never taken from the bag, and on a table, column or index <c>MS_Description</c> belongs to <c>Description</c>.
/// Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ExtendedPropertyTests
{
    private const string Product = "EpTest";
    private const string Parent = "EpParent";
    private const string Table = "EpOrders";
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
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE 'dbo.Ep%' OR ObjectName LIKE '[[]dbo].[[]Ep%'");
    }

    private void DropOwnObjects() => Exec($@"IF OBJECT_ID('dbo.{Table}', 'U') IS NOT NULL DROP TABLE dbo.{Table};
                                            IF OBJECT_ID('dbo.{Parent}', 'U') IS NOT NULL DROP TABLE dbo.{Parent};
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

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>The bag as a package author writes it; a null value is the JSON null.</summary>
    private static string Bag(params (string Name, string Value)[] entries) =>
        entries.Length == 0
            ? ""
            : ", \"Extensions\": { \"ExtendedProperties\": { "
              + string.Join(", ", entries.Select(x => $"\"{Esc(x.Name)}\": {(x.Value == null ? "null" : $"\"{Esc(x.Value)}\"")}"))
              + " } }";

    private static string Package(string tableBag = "", string idBag = "", string ixBag = "", string fkBag = "", string ccBag = "", string tableExtra = "") =>
        $"[{{ \"Schema\": \"[dbo]\", \"Name\": \"[{Parent}]\", \"Columns\": [ {{ \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false }} ],"
        + $" \"Indexes\": [ {{ \"Name\": \"[PK_{Parent}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true }} ] }},"
        + $" {{ \"Schema\": \"[dbo]\", \"Name\": \"[{Table}]\"{tableExtra}{tableBag}, \"Columns\": ["
        + $" {{ \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false{idBag} }},"
        + " { \"Name\": \"[ParentId]\", \"DataType\": \"INT\", \"Nullable\": true },"
        + " { \"Name\": \"[Qty]\", \"DataType\": \"INT\", \"Nullable\": true } ], \"Indexes\": ["
        + $" {{ \"Name\": \"[PK_{Table}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true }},"
        + $" {{ \"Name\": \"[IX_{Table}_Qty]\", \"IndexColumns\": \"[Qty]\"{ixBag} }} ],"
        + $" \"ForeignKeys\": [ {{ \"Name\": \"[FK_{Table}_Parent]\", \"Columns\": \"[ParentId]\", \"RelatedTableSchema\": \"[dbo]\", \"RelatedTable\": \"[{Parent}]\", \"RelatedColumns\": \"[Id]\"{fkBag} }} ],"
        + $" \"CheckConstraints\": [ {{ \"Name\": \"[CK_{Table}_Qty]\", \"Expression\": \"[Qty] >= 0 OR [ParentId] IS NULL\"{ccBag} }} ] }}]";

    private string Ep(string name, string level2 = ", default, default") =>
        ScalarStr($"SELECT CONVERT(NVARCHAR(MAX), [value]) FROM fn_listextendedproperty(N'{name.Replace("'", "''")}', N'SCHEMA', N'dbo', N'TABLE', N'{Table}'{level2})");

    private string TableEp(string name) => Ep(name);
    private string ColumnEp(string name, string column = "Id") => Ep(name, $", N'COLUMN', N'{column}'");
    private string IndexEp(string name) => Ep(name, $", N'INDEX', N'IX_{Table}_Qty'");
    private string FkEp(string name) => Ep(name, $", N'CONSTRAINT', N'FK_{Table}_Parent'");
    private string CcEp(string name) => Ep(name, $", N'CONSTRAINT', N'CK_{Table}_Qty'");

    private int Audits(string objectType, string action, string nameSuffix = "") =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = '{objectType}' AND ActionType = '{action}' AND ObjectName = 'dbo.{Table}{nameSuffix}'");

    [Test]
    public void DeclaredBags_AreApplied_ToTableColumnIndexForeignKeyAndCheckConstraint()
    {
        Deploy(Package(
            tableBag: Bag(("OwningTeam", "Billing"), ("My Note", "a & b < c 'quoted'")),
            idBag: Bag(("Classification", "Key")),
            ixBag: Bag(("IdxNote", "hot")),
            fkBag: Bag(("FkNote", "to the parent"), ("MS_Description", "the parent link")),
            ccBag: Bag(("CkNote", "non-negative"))));

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("OwningTeam"), Is.EqualTo("Billing"));
            Assert.That(TableEp("My Note"), Is.EqualTo("a & b < c 'quoted'"), "a name with a space and a value with a quote");
            Assert.That(ColumnEp("Classification"), Is.EqualTo("Key"));
            Assert.That(ColumnEp("Classification", "Qty"), Is.Null, "a column that declares nothing gets nothing");
            Assert.That(IndexEp("IdxNote"), Is.EqualTo("hot"));
            Assert.That(FkEp("FkNote"), Is.EqualTo("to the parent"));
            Assert.That(FkEp("MS_Description"), Is.EqualTo("the parent link"), "a foreign key has no Description key, so its MS_Description comes from the bag");
            Assert.That(CcEp("CkNote"), Is.EqualTo("non-negative"));
            Assert.That(Audits("table", "modified"), Is.EqualTo(2), "one audit row per property");
            Assert.That(Audits("column", "modified", ".Id"), Is.EqualTo(1));
            Assert.That(Audits("index", "modified", $".IX_{Table}_Qty"), Is.EqualTo(1));
            Assert.That(Audits("foreignKey", "modified", $".FK_{Table}_Parent"), Is.EqualTo(2));
            Assert.That(Audits("constraint", "modified", $".CK_{Table}_Qty"), Is.EqualTo(1));
        });
    }

    [Test]
    public void AChangedValue_IsUpdated_AnUnchangedOne_IsNotTouched_AndANameThePackageStopsMentioning_IsLeftAlone()
    {
        Deploy(Package(tableBag: Bag(("OwningTeam", "Billing"), ("Tier", "gold")), fkBag: Bag(("FkNote", "v1"))));
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE 'dbo.{Table}%'");

        Deploy(Package(tableBag: Bag(("OwningTeam", "Finance")), fkBag: Bag(("FkNote", "v1"))));

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("OwningTeam"), Is.EqualTo("Finance"));
            Assert.That(TableEp("Tier"), Is.EqualTo("gold"), "omitted means unmanaged, not removed");
            Assert.That(FkEp("FkNote"), Is.EqualTo("v1"));
            Assert.That(Audits("table", "modified"), Is.EqualTo(1), "only the changed property is touched");
            Assert.That(Audits("foreignKey", "modified", $".FK_{Table}_Parent"), Is.Zero);
        });
    }

    [Test]
    public void AnEmptyValue_RemovesTheProperty_AndANullDoesToo()
    {
        Deploy(Package(tableBag: Bag(("OwningTeam", "Billing"), ("Tier", "gold")), ccBag: Bag(("CkNote", "x"))));
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE 'dbo.{Table}%'");

        Deploy(Package(tableBag: Bag(("OwningTeam", ""), ("Tier", null)), ccBag: Bag(("CkNote", ""))));

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("OwningTeam"), Is.Null);
            Assert.That(TableEp("Tier"), Is.Null);
            Assert.That(CcEp("CkNote"), Is.Null);
            Assert.That(Audits("table", "modified"), Is.EqualTo(2));
            Assert.That(Audits("constraint", "modified", $".CK_{Table}_Qty"), Is.EqualTo(1));
        });

        Deploy(Package(tableBag: Bag(("OwningTeam", ""))));
        Assert.That(Audits("table", "modified"), Is.EqualTo(2), "removing what is already absent records nothing");
    }

    [Test]
    public void SchemaSmithsOwnStamps_AreNeverTakenFromTheBag()
    {
        Deploy(Package(tableBag: Bag(("ProductName", "SomeoneElse"), ("PreventDrop", "true"), ("SchemaSmith_SuspendedHistory", "x"), ("Kept", "yes"))));

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("ProductName"), Is.EqualTo(Product), "the ownership stamp is the deploy's, not the bag's");
            Assert.That(TableEp("PreventDrop"), Is.EqualTo("false"), "the deploy writes its own PreventDrop stamp; the bag cannot turn it on");
            Assert.That(TableEp("SchemaSmith_SuspendedHistory"), Is.Null);
            Assert.That(TableEp("Kept"), Is.EqualTo("yes"));
        });
    }

    [Test]
    public void MsDescriptionInTheBag_OnATable_IsTheLegacyDescription_NotASecondProperty()
    {
        Deploy(Package(tableBag: Bag(("MS_Description", "from the bag"), ("Other", "x"))));

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("MS_Description"), Is.EqualTo("from the bag"), "the pre-2.8 spelling still deploys, through Description");
            Assert.That(Audits("table", "modified"), Is.EqualTo(2), "one for the description, one for Other -- not two for MS_Description");
        });
    }

    [Test]
    public void WhatIf_ChangesNothing_AndRecordsWhatItWouldChange()
    {
        Deploy(Package());
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE 'dbo.{Table}%'");

        Deploy(Package(tableBag: Bag(("OwningTeam", "Billing")), fkBag: Bag(("FkNote", "x"))), whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(TableEp("OwningTeam"), Is.Null);
            Assert.That(FkEp("FkNote"), Is.Null);
            Assert.That(Audits("table", "wouldModify"), Is.EqualTo(1));
            Assert.That(Audits("foreignKey", "wouldModify", $".FK_{Table}_Parent"), Is.EqualTo(1));
            Assert.That(Audits("table", "modified") + Audits("foreignKey", "modified", $".FK_{Table}_Parent"), Is.Zero);
        });
    }

    [Test]
    public void IndexOnlyQuench_ConvergesIndexBags_Too()
    {
        Deploy(Package());

        DeployIndexesOnly(Package(ixBag: Bag(("IdxNote", "hot"))));

        Assert.That(IndexEp("IdxNote"), Is.EqualTo("hot"));
    }

    [Test]
    public void ARedeployOfAnUnchangedPackage_RecordsNothing()
    {
        var package = Package(tableBag: Bag(("OwningTeam", "Billing")), idBag: Bag(("Classification", "Key")), ixBag: Bag(("IdxNote", "hot")),
            fkBag: Bag(("FkNote", "x")), ccBag: Bag(("CkNote", "y")));
        Deploy(package);
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE 'dbo.{Table}%'");

        Deploy(package);

        Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ActionType = 'modified' AND ObjectName LIKE 'dbo.{Table}%'"), Is.Zero);
    }

    [Test]
    public void Extraction_RoundTrips_EveryBag()
    {
        Deploy(Package(
            tableBag: Bag(("OwningTeam", "Billing"), ("My Note", "a & b")),
            idBag: Bag(("Classification", "Key")),
            ixBag: Bag(("IdxNote", "hot")),
            fkBag: Bag(("FkNote", "to the parent")),
            ccBag: Bag(("CkNote", "non-negative"))));

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{Table}'";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader.GetString(0);
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

        static JObject Eps(DynamicBase o) => (JObject)((JObject)o.Extensions)?["ExtendedProperties"];
        Assert.Multiple(() =>
        {
            Assert.That((string)Eps(table)?["OwningTeam"], Is.EqualTo("Billing"));
            Assert.That((string)Eps(table)?["My Note"], Is.EqualTo("a & b"));
            Assert.That((string)Eps(table.Columns.Single(c => c.Name == "[Id]"))?["Classification"], Is.EqualTo("Key"));
            Assert.That((string)Eps(table.Indexes.Single(i => i.Name == $"[IX_{Table}_Qty]"))?["IdxNote"], Is.EqualTo("hot"));
            Assert.That((string)Eps(table.ForeignKeys.Single(k => k.Name == $"[FK_{Table}_Parent]"))?["FkNote"], Is.EqualTo("to the parent"));
            Assert.That((string)Eps(table.CheckConstraints.Single(c => c.Name == $"[CK_{Table}_Qty]"))?["CkNote"], Is.EqualTo("non-negative"));
        });
    }
}
