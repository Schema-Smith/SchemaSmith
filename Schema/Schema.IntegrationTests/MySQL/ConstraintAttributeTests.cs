// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Utility;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// A MySQL check constraint's <c>Enforced</c> is <c>TABLE_CONSTRAINTS.ENFORCED</c>. It is read on extraction and
/// deployed as <c>NOT ENFORCED</c>; a declared change drops and re-creates the constraint, and an omitted one leaves it as
/// it is. MariaDB has no such state, so this fixture is MySQL only, and MySQL keeps checks from 8.0.16. Every assertion
/// reads the catalog, never the file. Uses the suite's kindled database and isolates by object name.
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class ConstraintAttributeTests
{
    private const string Product = "CaTest";
    private const string Table = "ca_orders";
    private const string Ck = "ck_ca_orders_qty";
    private IDbConnection _connection;
    private string _db;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        if ((VersionHelper.ParsePatchComparable(Str("SELECT VERSION()"), Platform.MySQL) ?? 0) < 80016)
            Assert.Ignore("MySQL keeps check constraints from 8.0.16.");
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
        Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE ObjectName LIKE '%{Table}%'");
    }

    private void DropOwnObjects() => Exec($"DROP TABLE IF EXISTS `{_db}`.`{Table}`");

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

    private void Deploy(string json) =>
        Exec($"CALL SchemaSmith_TableQuench('{Product}', '{_db}', '{json.Replace("'", "''")}', 0, 0, 0)");

    /// <param name="ck">Attribute keys appended to the check constraint, e.g. <c>, "Enforced": false</c>.</param>
    private static string Package(string ck = "") =>
        "[{ \"Name\": \"`" + Table + "`\", \"Engine\": \"InnoDB\", \"Columns\": ["
        + " { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"`qty`\", \"DataType\": \"INT\", \"Nullable\": true } ],"
        + " \"Indexes\": [ { \"Name\": \"`pk_" + Table + "`\", \"IndexColumns\": \"`id`\", \"PrimaryKey\": true, \"Unique\": true } ],"
        + " \"CheckConstraints\": [ { \"Name\": \"`" + Ck + "`\", \"Expression\": \"`qty` >= 0\"" + ck + " } ] }]";

    private string Enforced()
    {
        var s = Str($"SELECT ENFORCED FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{_db}' AND TABLE_NAME = '{Table}' AND CONSTRAINT_NAME = '{Ck}' AND CONSTRAINT_TYPE = 'CHECK'");
        Assert.That(s, Is.Not.Null, $"{Ck} must exist");
        return s;
    }

    private int Audits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'constraint' AND ActionType = '{action}' AND ObjectName = '{Table}.{Ck}'");

    private void ClearAudit() => Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{Table}%'");

    [Test]
    public void EnforcedFalse_ArrivesInTheCatalog_AndARedeployRecordsNothing()
    {
        Deploy(Package(", \"Enforced\": false"));
        Assert.That(Enforced(), Is.EqualTo("NO"));

        ClearAudit();
        Deploy(Package(", \"Enforced\": false"));
        Assert.That(Audits("dropped") + Audits("created"), Is.Zero, "the same declaration a second time must touch nothing");
    }

    [Test]
    public void OmittedEnforced_IsEnforced()
    {
        Deploy(Package());
        Assert.That(Enforced(), Is.EqualTo("YES"));
    }

    // A package written before Enforced existed must not re-create a check someone made NOT ENFORCED: the re-create
    // re-checks the rows, and fails on any that violate it.
    [Test]
    public void AnOmittedEnforced_LeavesANotEnforcedCheckAsItIs()
    {
        Deploy(Package(", \"Enforced\": false"));
        Exec($"INSERT INTO `{_db}`.`{Table}` (id, qty) VALUES (1, -5)");
        ClearAudit();

        Deploy(Package());

        Assert.Multiple(() =>
        {
            Assert.That(Enforced(), Is.EqualTo("NO"));
            Assert.That(Audits("dropped") + Audits("created"), Is.Zero);
        });
    }

    [Test]
    public void AnEnforcedChange_DropsAndRecreates_InBothDirections()
    {
        Deploy(Package());
        ClearAudit();

        Deploy(Package(", \"Enforced\": false"));
        Assert.Multiple(() =>
        {
            Assert.That(Enforced(), Is.EqualTo("NO"));
            Assert.That(Audits("dropped"), Is.EqualTo(1), "re-created, since the state is part of the definition");
            Assert.That(Audits("created"), Is.EqualTo(1));
        });

        Exec($"INSERT INTO `{_db}`.`{Table}` (id, qty) VALUES (1, -5)");
        ClearAudit();
        var ex = Assert.Catch<DbException>(() => Deploy(Package(", \"Enforced\": true")), "re-creating the check enforced re-checks the rows, and a violating row must fail it, not be skipped");
        Assert.That(ex!.Message, Does.Contain(Ck));

        Exec($"DELETE FROM `{_db}`.`{Table}` WHERE id = 1");
        ClearAudit();
        Deploy(Package(", \"Enforced\": true"));
        Assert.Multiple(() =>
        {
            Assert.That(Enforced(), Is.EqualTo("YES"), "a declared Enforced true is applied");
            Assert.That(Audits("created"), Is.EqualTo(1));
        });
    }

    [Test]
    public void ARowTheConstraintRejects_IsAcceptedWhileNotEnforced()
    {
        Deploy(Package(", \"Enforced\": false"));
        Assert.DoesNotThrow(() => Exec($"INSERT INTO `{_db}`.`{Table}` (id, qty) VALUES (1, -5)"), "NOT ENFORCED means the engine does not check it");
    }

    [Test]
    public void Extraction_ReadsEnforced_AndOmitsItWhenTheConstraintIsEnforced()
    {
        Deploy(Package(", \"Enforced\": false"));
        var ck = (MySqlCheckConstraint)Extract().CheckConstraints.Single();
        Assert.That(ck.Enforced, Is.False);

        Deploy(Package(", \"Enforced\": true"));
        ck = (MySqlCheckConstraint)Extract().CheckConstraints.Single();
        Assert.That(ck.Enforced, Is.Null, "enforced is the default, so it is omitted");
    }

    private MySqlTable Extract()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_db}', '{Table}')";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader[0];
        return (MySqlTable)PlatformDeserializer.DeserializeTable(json, Platform.MySQL);
    }
}
