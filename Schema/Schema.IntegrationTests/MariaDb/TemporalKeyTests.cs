// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// A unique key declared <c>WithoutOverlaps</c> over one of the table's periods deploys as <c>WITHOUT OVERLAPS</c>
/// (MariaDB 10.5.3+), survives a redeploy untouched -- the catalog lists the key over the period's end and start
/// columns, and the compare reads the declared key the same way -- extracts with the flag and the period named last
/// where the catalog can say so (11.4+), and is re-created when the flag changes. Below 10.5.3 the key is created over
/// the period's columns and the downgrade is recorded. Assertions read SHOW CREATE TABLE and the change audit. Uses the
/// suite's kindled database and isolates by object name.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class TemporalKeyTests
{
    private const string Product = "TkTest";
    private const string Table = "tk_rates";
    private const string Key = "uk_tk_rates";
    private IDbConnection _connection;
    private string _db;
    private long _version;

    private bool HasWithoutOverlaps => _version >= 100503;
    private bool CatalogReportsIt => _version >= 110400;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _version = VersionHelper.ParsePatchComparable(Str("SELECT VERSION()"), Platform.MariaDb) ?? 0;
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

    private static string Package(bool withoutOverlaps) =>
        "[{ \"Name\": \"`" + Table + "`\", \"Engine\": \"InnoDB\", \"Columns\": ["
        + " { \"Name\": \"`id`\", \"DataType\": \"INT\", \"Nullable\": false },"
        + " { \"Name\": \"`s`\", \"DataType\": \"DATE\", \"Nullable\": false },"
        + " { \"Name\": \"`e`\", \"DataType\": \"DATE\", \"Nullable\": false } ],"
        + " \"Periods\": [ { \"Name\": \"`p`\", \"StartColumn\": \"`s`\", \"EndColumn\": \"`e`\" } ],"
        + " \"Indexes\": [ { \"Name\": \"`pk_" + Table + "`\", \"IndexColumns\": \"`id`,`s`\", \"PrimaryKey\": true, \"Unique\": true },"
        + " { \"Name\": \"`" + Key + "`\", \"IndexColumns\": \"" + (withoutOverlaps ? "`id`,`p`" : "`id`,`e`,`s`") + "\", \"Unique\": true, \"UniqueConstraint\": true"
        + (withoutOverlaps ? ", \"WithoutOverlaps\": true" : "") + " } ] }]";

    /// <summary>The unique key's line of SHOW CREATE TABLE, which is the only place every version prints the clause.</summary>
    private string KeyLine()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE `{_db}`.`{Table}`";
        using var reader = cmd.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        var ddl = reader.GetString(1);
        return ddl.Split('\n').Select(l => l.Trim().TrimEnd(',')).SingleOrDefault(l => l.Contains($"`{Key}`"));
    }

    private int IndexAudits(string action) =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'index' AND ActionType = '{action}' AND ObjectName LIKE '%{Table}%{Key}%'");

    private int Downgrades() =>
        Count($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'UNIQUE key (WITHOUT OVERLAPS, MariaDB 10.5.3)' AND ActionType = 'downgraded' AND ObjectName LIKE '%{Table}%{Key}%'");

    private void ClearAudit() => Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{Table}%'");

    [Test]
    public void ATemporalKey_Deploys_AndARedeployLeavesIt()
    {
        if (!HasWithoutOverlaps) Assert.Ignore("WITHOUT OVERLAPS is MariaDB 10.5.3+");

        Deploy(Package(withoutOverlaps: true));
        Assert.Multiple(() =>
        {
            Assert.That(KeyLine(), Is.EqualTo($"UNIQUE KEY `{Key}` (`id`,`p` WITHOUT OVERLAPS)"));
            Assert.That(Downgrades(), Is.Zero);
        });

        ClearAudit();
        Deploy(Package(withoutOverlaps: true));
        Assert.Multiple(() =>
        {
            Assert.That(IndexAudits("dropped"), Is.Zero, "the catalog lists the key over the period's columns; the compare reads the declared key the same way");
            Assert.That(IndexAudits("created"), Is.Zero);
            Assert.That(KeyLine(), Is.EqualTo($"UNIQUE KEY `{Key}` (`id`,`p` WITHOUT OVERLAPS)"));
        });
    }

    [Test]
    public void Extraction_NamesThePeriodLast_WithTheFlag_WhereTheCatalogCanSay()
    {
        if (!HasWithoutOverlaps) Assert.Ignore("WITHOUT OVERLAPS is MariaDB 10.5.3+");
        Deploy(Package(withoutOverlaps: true));

        var table = Extract();
        var key = (MariaDbIndex)table.Indexes.Single(i => i.Name == Key);
        Assert.Multiple(() =>
        {
            if (CatalogReportsIt)
            {
                Assert.That(key.IndexColumns, Is.EqualTo("`id`,`p`"), "the period's columns fold into the period, named last");
                Assert.That(key.WithoutOverlaps, Is.True);
            }
            else
            {
                Assert.That(key.IndexColumns, Is.EqualTo("`id`,`e`,`s`"), "below 11.4 the catalog cannot say which period a key is over, so it reads back over the period's columns");
                Assert.That(key.WithoutOverlaps, Is.False);
            }
        });
    }

    [Test]
    public void DroppingTheFlag_RecreatesTheKeyAsAPlainUniqueKey()
    {
        if (!CatalogReportsIt) Assert.Ignore("the flag is compared where the catalog reports it: MariaDB 11.4+");
        Deploy(Package(withoutOverlaps: true));
        ClearAudit();

        Deploy(Package(withoutOverlaps: false));
        Assert.Multiple(() =>
        {
            Assert.That(KeyLine(), Is.EqualTo($"UNIQUE KEY `{Key}` (`id`,`e`,`s`)"), "the same columns the engine listed for the temporal key, now a plain unique key");
            Assert.That(IndexAudits("dropped") + IndexAudits("created"), Is.GreaterThan(0), "the columns compare equal; only the flag differs, and that is a drop and re-create");
        });
    }

    [Test]
    public void Below1053_TheKeyIsCreatedOverThePeriodsColumns_AndTheDowngradeIsRecorded()
    {
        if (HasWithoutOverlaps) Assert.Ignore("the degrade is for servers below MariaDB 10.5.3");

        Deploy(Package(withoutOverlaps: true));

        Assert.Multiple(() =>
        {
            Assert.That(KeyLine(), Is.EqualTo($"UNIQUE KEY `{Key}` (`id`,`e`,`s`)"), "the columns the engine itself would list for the key, without the clause");
            Assert.That(Downgrades(), Is.EqualTo(1));
        });
    }

    private MariaDbTable Extract()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_db}', '{Table}')";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader[0];
        return (MariaDbTable)PlatformDeserializer.DeserializeTable(json, Platform.MariaDb);
    }
}
