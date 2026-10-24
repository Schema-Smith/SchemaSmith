// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A foreign key's and a check constraint's <c>Enforced</c>, <c>Validated</c> and <c>NotForReplication</c> are the
/// catalog's <c>is_disabled</c>, <c>is_not_trusted</c> and <c>is_not_for_replication</c>. The first two converge in place;
/// the third is create-time state and converges by drop and re-create. Every assertion reads the catalog, never the file.
/// Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ConstraintAttributeTests
{
    private const string Product = "CaTest";
    private const string Parent = "CaParent";
    private const string Table = "CaOrders";
    private static string Fk => $"FK_{Table}_Parent";
    private static string Ck => $"CK_{Table}_Qty";
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
        Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE ObjectName LIKE 'dbo.Ca%' OR ObjectName LIKE '[[]dbo].[[]Ca%'");
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

    /// <param name="fk">Attribute keys appended to the foreign key, e.g. <c>, "Enforced": false</c>.</param>
    /// <param name="ck">Attribute keys appended to the check constraint.</param>
    /// <param name="withCheck">false leaves the check constraint out of the package.</param>
    private static string Package(string fk = "", string ck = "", bool withCheck = true) =>
        $"[{{ \"Schema\": \"[dbo]\", \"Name\": \"[{Parent}]\", \"Columns\": [ {{ \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false }} ],"
        + $" \"Indexes\": [ {{ \"Name\": \"[PK_{Parent}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true }} ] }},"
        + $" {{ \"Schema\": \"[dbo]\", \"Name\": \"[{Table}]\", \"Columns\": ["
        + " { \"Name\": \"[Id]\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"[ParentId]\", \"DataType\": \"INT\", \"Nullable\": true },"
        + " { \"Name\": \"[Qty]\", \"DataType\": \"INT\", \"Nullable\": true } ], \"Indexes\": ["
        + $" {{ \"Name\": \"[PK_{Table}]\", \"IndexColumns\": \"[Id]\", \"PrimaryKey\": true, \"Unique\": true, \"Clustered\": true }} ],"
        + $" \"ForeignKeys\": [ {{ \"Name\": \"[{Fk}]\", \"Columns\": \"[ParentId]\", \"RelatedTableSchema\": \"[dbo]\", \"RelatedTable\": \"[{Parent}]\", \"RelatedColumns\": \"[Id]\"{fk} }} ]"
        + (withCheck ? $", \"CheckConstraints\": [ {{ \"Name\": \"[{Ck}]\", \"Expression\": \"[Qty] >= 0 OR [ParentId] IS NULL\"{ck} }} ]" : "")
        + " }]";

    private (bool Disabled, bool Untrusted, bool NotForReplication) FkState() => State("sys.foreign_keys", Fk);
    private (bool Disabled, bool Untrusted, bool NotForReplication) CkState() => State("sys.check_constraints", Ck);

    private (bool Disabled, bool Untrusted, bool NotForReplication) State(string view, string name)
    {
        var s = ScalarStr($"SELECT CONVERT(VARCHAR(1), is_disabled) + CONVERT(VARCHAR(1), is_not_trusted) + CONVERT(VARCHAR(1), is_not_for_replication) FROM {view} WHERE parent_object_id = OBJECT_ID('dbo.{Table}') AND name = '{name}'");
        Assert.That(s, Is.Not.Null, $"{name} must exist");
        return (s[0] == '1', s[1] == '1', s[2] == '1');
    }

    private int Audits(string objectType, string action, string name) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = '{objectType}' AND ActionType = '{action}' AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') = 'dbo.{Table}.{name}'");

    private void ClearAudit() => Exec($"DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}%'");

    [Test]
    public void DeclaredAttributes_ArriveInTheCatalog_AndARedeployRecordsNothing()
    {
        const string attrs = ", \"Enforced\": false, \"Validated\": false, \"NotForReplication\": true";
        Deploy(Package(fk: attrs, ck: attrs));

        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((true, true, true)), "foreign key: disabled, untrusted, not for replication");
            Assert.That(CkState(), Is.EqualTo((true, true, true)), "check constraint: disabled, untrusted, not for replication");
        });

        ClearAudit();
        Deploy(Package(fk: attrs, ck: attrs));
        Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}.%'"), Is.Zero,
            "the same declaration a second time must touch nothing");
    }

    // A package that says nothing about these attributes leaves the live state alone: one written before they existed must
    // not start re-validating constraints created WITH NOCHECK (which fails on violating rows), re-enabling ones someone
    // disabled, or stripping NOT FOR REPLICATION. A new constraint is still created enforced and trusted.
    [Test]
    public void OmittedAttributes_CreateTheDefaults_AndLeaveAConstraintChangedByHandAsItIs_UntilTheyAreDeclared()
    {
        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((false, false, false)));
            Assert.That(CkState(), Is.EqualTo((false, false, false)));
        });

        Exec($"ALTER TABLE dbo.{Table} NOCHECK CONSTRAINT [{Fk}]; ALTER TABLE dbo.{Table} NOCHECK CONSTRAINT [{Ck}];");
        Assert.That(FkState().Disabled, Is.True, "setup: disabled by hand");
        ClearAudit();

        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((true, true, false)), "an omitted Enforced leaves the key disabled");
            Assert.That(CkState(), Is.EqualTo((true, true, false)));
            Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}.%'"), Is.Zero);
        });

        ClearAudit();
        const string converge = ", \"Enforced\": true, \"Validated\": true";
        Deploy(Package(fk: converge, ck: converge));
        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((false, false, false)), "declared, enabling validates the rows, so the key is trusted again");
            Assert.That(CkState(), Is.EqualTo((false, false, false)));
            Assert.That(Audits("foreignKey", "modified", Fk), Is.EqualTo(1));
            Assert.That(Audits("constraint", "modified", Ck), Is.EqualTo(1));
            Assert.That(Audits("foreignKey", "dropped", Fk) + Audits("constraint", "dropped", Ck), Is.Zero, "converged in place, not re-created");
        });

        Exec($"ALTER TABLE dbo.{Table} NOCHECK CONSTRAINT [{Fk}]; ALTER TABLE dbo.{Table} WITH NOCHECK CHECK CONSTRAINT [{Fk}];");
        Exec($"ALTER TABLE dbo.{Table} NOCHECK CONSTRAINT [{Ck}]; ALTER TABLE dbo.{Table} WITH NOCHECK CHECK CONSTRAINT [{Ck}];");
        Assert.That(FkState(), Is.EqualTo((false, true, false)), "setup: re-enabled by hand without checking the rows");

        Deploy(Package());
        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((false, true, false)), "an omitted Validated leaves an enabled key untrusted");
            Assert.That(CkState(), Is.EqualTo((false, true, false)));
        });
    }

    // SQL Server never trusts a NOT FOR REPLICATION constraint, so validating one ran WITH CHECK CHECK on every deploy.
    [Test]
    public void NotForReplicationWithValidatedTrue_IsNotRevalidatedOnEveryDeploy()
    {
        const string attrs = ", \"NotForReplication\": true, \"Validated\": true";
        Deploy(Package(fk: attrs, ck: attrs));
        Assert.That(FkState().Untrusted, Is.True, "setup: the engine does not trust a NOT FOR REPLICATION key");
        ClearAudit();

        Deploy(Package(fk: attrs, ck: attrs));

        Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}.%'"), Is.Zero,
            "a redeploy touches nothing");
    }

    [Test]
    public void AnOmittedNotForReplication_LeavesTheConstraintAsItIs()
    {
        Deploy(Package(fk: ", \"NotForReplication\": true", ck: ", \"NotForReplication\": true"));
        ClearAudit();

        Deploy(Package());

        Assert.Multiple(() =>
        {
            Assert.That(FkState().NotForReplication, Is.True, "not re-created to strip it");
            Assert.That(CkState().NotForReplication, Is.True);
            Assert.That(Audits("foreignKey", "dropped", Fk) + Audits("constraint", "dropped", Ck), Is.Zero);
        });
    }

    [Test]
    public void EnforcedTrueOverADisabledConstraint_WithValidatedFalse_EnablesWithoutChecking()
    {
        Deploy(Package(ck: ", \"Enforced\": false"));
        Assert.That(CkState().Disabled, Is.True, "setup");
        Exec($"INSERT dbo.{Parent} (Id) VALUES (1); INSERT dbo.{Table} (Id, ParentId, Qty) VALUES (1, 1, -5)");

        Deploy(Package(ck: ", \"Enforced\": true, \"Validated\": false"));

        Assert.That(CkState(), Is.EqualTo((false, true, false)), "enabled for new rows, the violating row left unchecked, so untrusted");
    }

    [Test]
    public void ValidatedTrue_OverAnUntrustedConstraint_ChecksTheRows_AndFailsOnAViolation()
    {
        Deploy(Package(withCheck: false));
        Exec($"INSERT dbo.{Parent} (Id) VALUES (1); INSERT dbo.{Table} (Id, ParentId, Qty) VALUES (1, 1, -5)");
        Deploy(Package(ck: ", \"Validated\": false"));
        Assert.That(CkState(), Is.EqualTo((false, true, false)), "setup: created WITH NOCHECK over a violating row");
        ClearAudit();

        var ex = Assert.Throws<SqlException>(() => Deploy(Package(ck: ", \"Validated\": true")), "validating a constraint the rows violate must fail, not silently skip");
        Assert.That(ex!.Message, Does.Contain(Ck));
        Assert.That(CkState().Untrusted, Is.True, "still untrusted after the failed validation");

        Exec($"DELETE dbo.{Table} WHERE Id = 1");
        Deploy(Package(ck: ", \"Validated\": true"));
        Assert.Multiple(() =>
        {
            Assert.That(CkState(), Is.EqualTo((false, false, false)), "with the violation gone, the rows validate and the constraint is trusted");
            Assert.That(Audits("constraint", "modified", Ck), Is.EqualTo(1));
        });
    }

    [Test]
    public void ValidatedFalse_OverATrustedConstraint_ChangesNothing()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(fk: ", \"Validated\": false", ck: ", \"Validated\": false"));

        Assert.Multiple(() =>
        {
            Assert.That(FkState(), Is.EqualTo((false, false, false)), "the engine cannot un-validate, and nothing should try");
            Assert.That(CkState(), Is.EqualTo((false, false, false)));
            Assert.That(Count($"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND REPLACE(REPLACE(ObjectName, '[', ''), ']', '') LIKE 'dbo.{Table}.%'"), Is.Zero);
        });
    }

    [Test]
    public void NotForReplicationChange_DropsAndRecreates_TheConstraint()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(fk: ", \"NotForReplication\": true", ck: ", \"NotForReplication\": true"));

        Assert.Multiple(() =>
        {
            Assert.That(FkState().NotForReplication, Is.True);
            Assert.That(CkState().NotForReplication, Is.True);
            Assert.That(Audits("foreignKey", "dropped", Fk), Is.EqualTo(1), "create-time state: the key is re-created");
            Assert.That(Audits("foreignKey", "created", Fk), Is.EqualTo(1));
            Assert.That(Audits("constraint", "dropped", Ck), Is.EqualTo(1));
            Assert.That(Audits("constraint", "created", Ck), Is.EqualTo(1));
        });

        ClearAudit();
        Deploy(Package(fk: ", \"NotForReplication\": true", ck: ", \"NotForReplication\": true"));
        Assert.That(Audits("foreignKey", "dropped", Fk) + Audits("constraint", "dropped", Ck), Is.Zero, "and not again on the next deploy");
    }

    [Test]
    public void WhatIf_RecordsWhatItWouldChange_AndChangesNothing()
    {
        Deploy(Package());
        Exec($"ALTER TABLE dbo.{Table} NOCHECK CONSTRAINT [{Fk}]");
        ClearAudit();

        Deploy(Package(fk: ", \"Enforced\": true"), whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(FkState().Disabled, Is.True, "WhatIf must not enable it");
            Assert.That(Audits("foreignKey", "wouldModify", Fk), Is.EqualTo(1));
            Assert.That(Audits("foreignKey", "modified", Fk), Is.Zero);
        });
    }

    [Test]
    public void Extraction_WritesOnlyTheAttributesThatAreNotTheDefault()
    {
        Deploy(Package());
        var defaults = Extract();
        Assert.Multiple(() =>
        {
            Assert.That(defaults.Json, Does.Not.Contain("Enforced").And.Not.Contain("Validated").And.Not.Contain("NotForReplication"),
                "a constraint in its default state carries no attribute keys");
            // Omitted, so a fresh deploy creates them enforced and validated and a redeploy leaves them as they are.
            Assert.That(((SqlServerForeignKey)defaults.Table.ForeignKeys.Single()).Enforced, Is.Null);
            Assert.That(((SqlServerCheckConstraint)defaults.Table.CheckConstraints.Single()).Validated, Is.Null);
        });

        const string attrs = ", \"Enforced\": false, \"Validated\": false, \"NotForReplication\": true";
        Deploy(Package(fk: attrs, ck: attrs));
        var declared = Extract();
        var fk = (SqlServerForeignKey)declared.Table.ForeignKeys.Single();
        var ck = (SqlServerCheckConstraint)declared.Table.CheckConstraints.Single();
        Assert.Multiple(() =>
        {
            Assert.That((fk.Enforced, fk.Validated, fk.NotForReplication), Is.EqualTo((false, false, true)));
            Assert.That((ck.Enforced, ck.Validated, ck.NotForReplication), Is.EqualTo((false, false, true)));
        });
    }

    private (string Json, SqlServerTable Table) Extract()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{Table}'";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader.GetString(0);
        return (json, (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer));
    }
}
