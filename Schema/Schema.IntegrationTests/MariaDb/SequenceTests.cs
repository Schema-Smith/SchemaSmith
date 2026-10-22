// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// Declared MariaDB sequences converge through SchemaSmith_SequenceQuench: created with their attributes, altered
/// in place when an attribute changes, left alone when nothing changed, never moved from their current value.
/// Each test owns a uniquely named sequence on the suite's main database.
/// </summary>
[Category("MariaDb")]
[TestFixture]
[Category("Integration")]
public class SequenceTests
{
    private IDbConnection _connection;
    private string _db = "";
    private int _versionNum;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = FixtureSetup.MainDb;
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _versionNum = Convert.ToInt32(Scalar("SELECT SchemaSmith_ServerVersionNum()"));
        if (_versionNum < 1003) Assert.Ignore("Sequences arrived in MariaDB 10.3.");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

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

    private long Long(string sql) => Convert.ToInt64(Scalar(sql));

    private void Deploy(string json, bool whatIf = false) =>
        Exec($"CALL SchemaSmith_SequenceQuench('{_db}', '{json.Replace("'", "''")}', {(whatIf ? 1 : 0)})");

    private string Attributes(string name) =>
        Scalar($"SELECT CONCAT(start_value, '|', increment, '|', minimum_value, '|', maximum_value, '|', cache_size, '|', cycle_option) FROM `{name}`")?.ToString();

    private long Audits(string name, string action) =>
        Long($"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName = '{name}' AND ActionType = '{action}'");

    private bool Exists(string name) =>
        Long($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{_db}' AND TABLE_NAME = '{name}' AND TABLE_TYPE = 'SEQUENCE'") == 1;

    [Test]
    public void ADeclaredSequence_IsCreatedWithItsAttributes()
    {
        var name = "seq_attrs_" + Uid();
        try
        {
            Deploy($$"""[{ "Name": "{{name}}", "Start": 100, "Increment": 5, "MinValue": 10, "MaxValue": 10000, "Cache": 20, "Cycle": true }]""");
            Assert.Multiple(() =>
            {
                Assert.That(Exists(name), Is.True, "the sequence must be created");
                Assert.That(Attributes(name), Is.EqualTo("100|5|10|10000|20|1"), "every declared attribute must be on the sequence");
                Assert.That(Long($"SELECT NEXTVAL(`{name}`)"), Is.EqualTo(100), "it starts where it was declared to");
                Assert.That(Audits(name, "created"), Is.EqualTo(1), "the manifest records the creation");
            });
        }
        finally
        {
            Exec($"DROP SEQUENCE IF EXISTS `{name}`");
        }
    }

    [Test]
    public void ChangedAttributes_Converge_AndTheCurrentValueIsNeverReset()
    {
        var name = "seq_conv_" + Uid();
        try
        {
            Deploy($$"""[{ "Name": "{{name}}", "Start": 1, "Increment": 1 }]""");
            Long($"SELECT NEXTVAL(`{name}`)");
            Long($"SELECT NEXTVAL(`{name}`)");

            // The new bound stays above the values the first cache reserved: MariaDB itself cycles a sequence whose cached
            // position has passed a lowered MaxValue, which is the engine's rule and not a reset.
            Deploy($$"""[{ "Name": "{{name}}", "Start": 50, "Increment": 2, "MaxValue": 500000, "Cache": 10, "Cycle": true }]""");

            Assert.Multiple(() =>
            {
                Assert.That(Attributes(name), Is.EqualTo("50|2|1|500000|10|1"), "each changed attribute is altered in place; MinValue was left out, so the engine's stays");
                Assert.That(Long($"SELECT NEXTVAL(`{name}`)"), Is.GreaterThan(2), "converging attributes must not move the sequence back: the numbers it handed out are in use");
                Assert.That(Audits(name, "modified"), Is.EqualTo(1), "the manifest records one alteration");
            });
        }
        finally
        {
            Exec($"DROP SEQUENCE IF EXISTS `{name}`");
        }
    }

    [Test]
    public void RedeployingAnUnchangedSequence_DoesNothing()
    {
        var name = "seq_same_" + Uid();
        try
        {
            var json = $$"""[{ "Name": "{{name}}", "Start": 7, "Increment": 3, "MinValue": 1, "MaxValue": 9000, "Cache": 1000, "Cycle": false }]""";
            Deploy(json);
            var before = Attributes(name);
            Deploy(json);
            Assert.Multiple(() =>
            {
                Assert.That(Attributes(name), Is.EqualTo(before));
                Assert.That(Audits(name, "modified"), Is.Zero, "an unchanged sequence must not be altered");
                Assert.That(Audits(name, "created"), Is.EqualTo(1));
            });
        }
        finally
        {
            Exec($"DROP SEQUENCE IF EXISTS `{name}`");
        }
    }

    [Test]
    public void WhatIf_ReportsAndChangesNothing()
    {
        var name = "seq_whatif_" + Uid();
        try
        {
            Deploy($$"""[{ "Name": "{{name}}", "Start": 1 }]""", whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(Exists(name), Is.False, "WhatIf creates nothing");
                Assert.That(Audits(name, "wouldCreate"), Is.EqualTo(1), "the preview says what the real run would create");
            });
            Deploy($$"""[{ "Name": "{{name}}", "Start": 1 }]""");
            Deploy($$"""[{ "Name": "{{name}}", "Start": 1, "Increment": 9 }]""", whatIf: true);
            Assert.Multiple(() =>
            {
                Assert.That(Attributes(name), Does.StartWith("1|1|"), "WhatIf alters nothing");
                Assert.That(Audits(name, "wouldModify"), Is.EqualTo(1), "the preview says what the real run would alter");
            });
        }
        finally
        {
            Exec($"DROP SEQUENCE IF EXISTS `{name}`");
        }
    }

    [Test]
    public void ADeclaredDataType_IsAppliedFrom115_AndADowngradeBelowIt()
    {
        var name = "seq_type_" + Uid();
        try
        {
            Deploy($$"""[{ "Name": "{{name}}", "DataType": "int", "Start": 1 }]""");
            Assert.That(Exists(name), Is.True, "the sequence is created either way");
            if (_versionNum >= 1105)
            {
                Assert.That(Scalar($"SELECT LOWER(DATA_TYPE) FROM INFORMATION_SCHEMA.SEQUENCES WHERE SEQUENCE_SCHEMA = '{_db}' AND SEQUENCE_NAME = '{name}'")?.ToString(), Is.EqualTo("int"),
                    "from 11.5 the declared type is the sequence's type");
                Assert.That(Audits(name, "downgraded"), Is.Zero);
            }
            else
            {
                Assert.That(Audits(name, "downgraded"), Is.EqualTo(1), "below 11.5 the type cannot be set, and the manifest says so");
            }
        }
        finally
        {
            Exec($"DROP SEQUENCE IF EXISTS `{name}`");
        }
    }
}
