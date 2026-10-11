// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// A system-versioned table with explicit ROW START / ROW END columns. Extraction leaves the period columns out (the
/// versioning model regenerates them), but kept a primary key that named the row-end column, so the package failed to
/// deploy: "Key column 're' doesn't exist in table" (verified on released 2.7.1). An index never names a column the
/// extraction left out; on a system-versioned table MariaDB adds the row-end column to unique keys itself.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
public class ExplicitPeriodColumnRoundTripTests
{
    private IDbConnection _connection = null!;
    private string _testDb = null!;
    private readonly string _source = $"vsrc_{Guid.NewGuid():N}"[..20];
    private readonly string _target = $"vdst_{Guid.NewGuid():N}"[..20];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _testDb = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP TABLE IF EXISTS `{_source}`, `{_target}`");
        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void AnExtractedTableWithExplicitPeriodColumns_Deploys()
    {
        Exec($@"CREATE TABLE `{_source}` (id INT NOT NULL, v INT NULL,
  rs TIMESTAMP(6) GENERATED ALWAYS AS ROW START, re TIMESTAMP(6) GENERATED ALWAYS AS ROW END,
  PERIOD FOR SYSTEM_TIME (rs, re), PRIMARY KEY (id, re)) WITH SYSTEM VERSIONING");

        var json = Extract(_source);
        var table = JObject.Parse(json);
        var columns = table["Columns"]!.Select(c => (string)c["Name"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var index in table["Indexes"]!)
            foreach (var column in ((string)index["IndexColumns"]!).Split(','))
                Assert.That(columns, Does.Contain(column.Trim()), $"index {index["Name"]} names a column the extraction left out: {json}");

        Exec($"CALL SchemaSmith_TableQuench('ExplicitPeriodProduct', '{_testDb}', '{json.Replace(_source, _target).Replace("'", "''")}', 0, 0, 0)");

        Assert.That(Scalar($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{_testDb}' AND TABLE_NAME = '{_target}'"), Is.EqualTo(1));
    }

    // A temporal column still in MariaDB's pre-10.0 storage format reports `datetime(3) /* mariadb-5.3 */`. Extraction
    // copied the note into the package and the compare never matched it, so the column was rebuilt every deploy. The
    // old format is a global setting, so it is switched off only for the one CREATE.
    [Test]
    [NonParallelizable]
    public void AnOldFormatTemporalColumn_ExtractsAndRedeploysWithoutChange()
    {
        var table = $"old53_{Guid.NewGuid():N}"[..20];
        try
        {
            Exec("SET @ss_saved = @@GLOBAL.mysql56_temporal_format");
            Exec("SET GLOBAL mysql56_temporal_format = OFF");
            try { Exec($"CREATE TABLE `{table}` (id INT NOT NULL PRIMARY KEY, d DATETIME(3) NULL)"); }
            finally { Exec("SET GLOBAL mysql56_temporal_format = @ss_saved"); }

            var json = Extract(table);
            Assert.That(json, Does.Not.Contain("mariadb-5.3"), json);

            Exec($"DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()");
            Exec($"CALL SchemaSmith_TableQuench('OldFormatProduct', '{_testDb}', '{json.Replace("'", "''")}', 0, 0, 0)");
            Assert.That(Scalar($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '{table}%'"), Is.EqualTo(0),
                "the extract deployed back onto its own table must change nothing");
        }
        finally
        {
            Exec($"DROP TABLE IF EXISTS `{table}`");
        }
    }

    private string Extract(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_testDb}', '{tableName}')";
        using var reader = cmd.ExecuteReader();
        var json = "";
        while (reader.Read())
            if (!reader.IsDBNull(0)) json += reader.GetString(0);
        return json;
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
