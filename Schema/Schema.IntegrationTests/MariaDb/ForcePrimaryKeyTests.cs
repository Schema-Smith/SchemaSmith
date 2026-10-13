// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MariaDb;

/// <summary>
/// <c>innodb_force_primary_key=ON</c> refuses an InnoDB table created without a primary key. The kindle and the quench
/// procedures created such tables, so nothing deployed at all. The variable is global only, so this fixture turns it on
/// for the server and restores it, and runs alone.
/// </summary>
[Category("MariaDb")]
[Category("Integration")]
[TestFixture]
[NonParallelizable] // sets a global the whole server sees
public class ForcePrimaryKeyTests
{
    private const string Product = "ForcePrimaryKeyProduct";
    private const string TestDb = "ss_force_pk_maria";
    private IDbConnection _connection = null!;
    private string _original;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        _original = Scalar("SELECT @@GLOBAL.innodb_force_primary_key")?.ToString();
        Exec($"DROP DATABASE IF EXISTS `{TestDb}`");
        Exec($"CREATE DATABASE `{TestDb}`");
        Exec($"USE `{TestDb}`");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            if (_connection is { State: ConnectionState.Open })
            {
                Exec($"SET GLOBAL innodb_force_primary_key = {(_original is "1" or "ON" ? 1 : 0)}");
                Exec($"DROP DATABASE IF EXISTS `{TestDb}`");
            }
        }
        finally
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }

    private const string FirstDeploy = """
        [{ "Name": "`rpk_parent`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`code`", "DataType": "VARCHAR(20)", "Nullable": false } ],
           "Indexes": [ { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                        { "Name": "`ix_code`", "Unique": false, "IndexColumns": "`code`" } ],
           "CheckConstraints": [ { "Name": "`ck_rpk_code`", "Expression": "CHAR_LENGTH(`code`) > 0" } ] },
         { "Name": "`rpk_child`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`parent_id`", "DataType": "INT", "Nullable": true },
                        { "Name": "`note`", "DataType": "VARCHAR(50)", "Nullable": true }, { "Name": "`extra`", "DataType": "INT", "Nullable": true } ],
           "Indexes": [ { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "ForeignKeys": [ { "Name": "`fk_rpk_child_parent`", "Columns": "`parent_id`", "RelatedTable": "`rpk_parent`", "RelatedColumns": "`id`" } ] }]
        """;

    // Every kind of change the modified-table, index and constraint passes handle: a column added, one renamed, one
    // dropped, an index reshaped, a check changed.
    private const string SecondDeploy = """
        [{ "Name": "`rpk_parent`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`code`", "DataType": "VARCHAR(30)", "Nullable": false },
                        { "Name": "`added`", "DataType": "INT", "Nullable": true } ],
           "Indexes": [ { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                        { "Name": "`ix_code`", "Unique": true, "IndexColumns": "`code`,`id`" } ],
           "CheckConstraints": [ { "Name": "`ck_rpk_code`", "Expression": "CHAR_LENGTH(`code`) > 1" } ] },
         { "Name": "`rpk_child`", "Engine": "InnoDB",
           "Columns": [ { "Name": "`id`", "DataType": "INT", "Nullable": false }, { "Name": "`parent_id`", "DataType": "INT", "Nullable": true },
                        { "Name": "`memo`", "DataType": "VARCHAR(50)", "Nullable": true, "OldName": "`note`" } ],
           "Indexes": [ { "Name": "`PRIMARY`", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" } ],
           "ForeignKeys": [ { "Name": "`fk_rpk_child_parent`", "Columns": "`parent_id`", "RelatedTable": "`rpk_parent`", "RelatedColumns": "`id`", "DeleteAction": "CASCADE" } ] }]
        """;

    [Test]
    public void KindleDeployRedeployAndExtract_SucceedUnderForcePrimaryKey()
    {
        try
        {
            Exec("SET GLOBAL innodb_force_primary_key = 1");
            Assert.That(() => Exec($"CREATE TABLE `{TestDb}`.`no_pk_probe` (a INT) ENGINE=InnoDB"), Throws.Exception,
                "the server must refuse a table without a primary key, or this test proves nothing");

            Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(Command(), Platform.MariaDb, forceReKindle: true), "the kindle creates its tables with primary keys");
            Assert.DoesNotThrow(() => Deploy(FirstDeploy, whatIf: 0), "first deploy");
            Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 1), "WhatIf of the changes");
            Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 0), "the changes");
            Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 0), "an unchanged redeploy");

            Assert.Multiple(() =>
            {
                Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = 'rpk_child' AND COLUMN_NAME = 'memo'"), Is.EqualTo(1));
                Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = 'rpk_child' AND COLUMN_NAME IN ('note', 'extra')"), Is.Zero);
                Assert.That(Scalar($"SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{TestDb}'")?.ToString(), Is.EqualTo("CASCADE"));
                Assert.That(Scalar($"CALL SchemaSmith_GenerateTableJSON('{TestDb}', 'rpk_parent')")?.ToString(), Does.Contain("`added`"));
            });
        }
        finally
        {
            Exec($"SET GLOBAL innodb_force_primary_key = {(_original is "1" or "ON" ? 1 : 0)}");
        }
    }

    private void Deploy(string json, int whatIf)
    {
        using var cmd = Command();
        cmd.CommandText = $"CALL SchemaSmith_TableQuench('{Product}', '{TestDb}', '{json.Replace("'", "''")}', {whatIf}, 1, 1)";
        cmd.ExecuteNonQuery();
    }

    private IDbCommand Command()
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private long Count(string sql) => Convert.ToInt64(Scalar(sql));
}
