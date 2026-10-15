// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// One rule for the level of a CHECK: a column's <c>CheckExpression</c> is an unnamed check the engine names itself, and
/// an entry in <c>CheckConstraints</c> is a named check whatever columns it reads -- so a named check on one column keeps
/// its name through deploy, redeploy and extraction, and a column can carry both. Uses the suite's kindled database and
/// isolates by object name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class CheckLevelTests
{
    private const string Product = "ClTest";
    private const string Table = "ClOrders";
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
        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}%'");
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

    private string Str(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var r = cmd.ExecuteScalar();
        return r == null || r == DBNull.Value ? null : r.ToString();
    }

    private int Count(string sql) => Convert.ToInt32(Str(sql) ?? "0");

    private void Deploy(string json)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{Product}', @TableDefinitions = N'{json.Replace("'", "''")}', @DropTablesRemovedFromProduct = 0";
        cmd.ExecuteNonQuery();
    }

    /// <param name="qtyNamed">Whether the named second check on Qty is declared.</param>
    private static string Package(bool qtyNamed = true) =>
        $"[{{ \"Schema\": \"[dbo]\", \"Name\": \"[{Table}]\", \"Columns\": ["
        + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[Qty]\", \"DataType\": \"INT\", \"Nullable\": true, \"CheckExpression\": \"[Qty] >= 0\" },"
        + " { \"Name\": \"[Price]\", \"DataType\": \"INT\", \"Nullable\": true } ],"
        + $" \"Indexes\": [ {{ \"Name\": \"[PK_{Table}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true }} ],"
        + $" \"CheckConstraints\": [ {{ \"Name\": \"[CK_{Table}_Price]\", \"Expression\": \"[Price] > 0\" }}"
        + (qtyNamed ? $", {{ \"Name\": \"[CK_{Table}_QtyNamed]\", \"Expression\": \"[Qty] < 1000\" }}" : "")
        + " ] }]";

    /// <summary>Every check on the table as <c>column:name:system</c>, ordered by column then name.</summary>
    private string Checks() => Str(
        $"SELECT STRING_AGG(ISNULL(COL_NAME(parent_object_id, NULLIF(parent_column_id, 0)), '-') + ':' + IIF(is_system_named = 1, 'system', [name]), ',') WITHIN GROUP (ORDER BY ISNULL(COL_NAME(parent_object_id, NULLIF(parent_column_id, 0)), '-') + ':' + IIF(is_system_named = 1, 'system', [name])) " +
        $"FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.{Table}')");

    private int ConstraintAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'constraint' AND ActionType = '{action}' AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}%'");

    [Test]
    public void ANamedCheckOnOneColumn_KeepsItsName_BesideTheColumnsOwnCheck_AndRedeploysAsANoOp()
    {
        Deploy(Package());
        Assert.That(Checks(), Is.EqualTo($"Price:CK_{Table}_Price,Qty:CK_{Table}_QtyNamed,Qty:system"),
            "the column's own check is system-named, the two named ones keep their names, and all three sit on their columns");

        Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}%'");
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(ConstraintAudits("dropped"), Is.Zero, "nothing is re-created on an unchanged package");
            Assert.That(ConstraintAudits("created"), Is.Zero);
            Assert.That(Checks(), Is.EqualTo($"Price:CK_{Table}_Price,Qty:CK_{Table}_QtyNamed,Qty:system"));
        });
    }

    [Test]
    public void Extraction_ReturnsTheSameShape_SoTheRoundTripIsClean()
    {
        Deploy(Package());

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{Table}'";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader.GetString(0);
        var table = (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(((SqlServerColumn)table.Columns.Single(c => c.Name == "[Qty]")).CheckExpression, Is.EqualTo("[Qty]>=(0)"), "the system-named check is the column's");
            Assert.That(((SqlServerColumn)table.Columns.Single(c => c.Name == "[Price]")).CheckExpression, Is.Null, "a named check is not folded into the column, even on one column");
            Assert.That(table.CheckConstraints.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(new[] { $"[CK_{Table}_Price]", $"[CK_{Table}_QtyNamed]" }), "both named checks keep their names");
        });
    }

    [Test]
    public void ANamedCheckLeavingThePackage_IsDroppedByAbsence_AndTheColumnsOwnCheckStays()
    {
        Deploy(Package());

        Deploy(Package(qtyNamed: false));

        Assert.That(Checks(), Is.EqualTo($"Price:CK_{Table}_Price,Qty:system"));
    }
}
