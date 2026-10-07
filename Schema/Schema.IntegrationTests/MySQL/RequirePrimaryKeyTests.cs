// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.MySQL;

/// <summary>
/// <c>sql_require_primary_key=ON</c> refuses any table, base or TEMPORARY, created or altered without a primary key
/// (MySQL 8.0.13+). The kindle and the quench procedures created such tables, so nothing deployed at all. The variable is
/// session-settable, so this fixture turns it on for its own connection rather than needing a server started with it.
/// </summary>
[Category("MySQL")]
[Category("Integration")]
[TestFixture]
[NonParallelizable] // creates and drops its own database: a from-scratch kindle is part of what is under test
public class RequirePrimaryKeyTests
{
    private const string Product = "RequirePrimaryKeyProduct";
    private const string TestDb = "ss_require_pk_mysql";
    private IDbConnection _connection = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.MySQL).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM performance_schema.global_variables WHERE VARIABLE_NAME = 'sql_require_primary_key'")) == 0)
            Assert.Ignore("sql_require_primary_key arrived in MySQL 8.0.13.");
    }

    [SetUp]
    public void SetUp()
    {
        Exec("SET SESSION sql_require_primary_key = 0");
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
                Exec("SET SESSION sql_require_primary_key = 0");
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
    public void KindleDeployRedeployAndExtract_SucceedUnderRequirePrimaryKey()
    {
        Exec("SET SESSION sql_require_primary_key = 1");

        Assert.DoesNotThrow(() => Kindle(), "the kindle creates its tables with primary keys");
        Assert.DoesNotThrow(() => Deploy(FirstDeploy, whatIf: 0), "first deploy");
        Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 1), "WhatIf of the changes");
        Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 0), "the changes");
        Assert.DoesNotThrow(() => Deploy(SecondDeploy, whatIf: 0), "an unchanged redeploy");

        Assert.Multiple(() =>
        {
            Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = 'rpk_child' AND COLUMN_NAME = 'memo'"), Is.EqualTo(1));
            Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = 'rpk_child' AND COLUMN_NAME IN ('note', 'extra')"), Is.Zero);
            Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = '{TestDb}' AND CONSTRAINT_TYPE = 'FOREIGN KEY'"), Is.EqualTo(1));
            Assert.That(Scalar($"SELECT DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = '{TestDb}'")?.ToString(), Is.EqualTo("CASCADE"));
            Assert.That(Scalar($"CALL SchemaSmith_GenerateTableJSON('{TestDb}', 'rpk_parent')")?.ToString(), Does.Contain("`added`"));
        });
    }

    // A database kindled by an earlier SchemaSmith has a stamp table and an expression map without primary keys. Once
    // the variable is on, re-kindling has to give them one rather than fail on the first ALTER.
    [Test]
    public void ReKindle_GivesTheOlderKindleTablesAPrimaryKey()
    {
        Kindle();
        Exec("ALTER TABLE SchemaSmith_KindleStamp DROP PRIMARY KEY");
        Exec("ALTER TABLE SchemaSmith_ExpressionMap DROP PRIMARY KEY, " +
             "ADD UNIQUE KEY uk_expressionmap (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot)");
        Exec("SET SESSION sql_require_primary_key = 1");

        Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(Command(), Platform.MySQL, forceReKindle: true));

        Assert.Multiple(() =>
        {
            Assert.That(PrimaryKeyColumns("SchemaSmith_KindleStamp"), Is.EqualTo("Stamp"));
            Assert.That(PrimaryKeyColumns("SchemaSmith_ExpressionMap"), Is.EqualTo("ObjectSchema,ObjectTable,ObjectKind,ObjectName,Slot"));
            Assert.That(Count($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = 'SchemaSmith_ExpressionMap' AND INDEX_NAME <> 'PRIMARY'"),
                Is.Zero, "the unique key the primary key replaces must not be left behind beside it");
        });
    }

    private string PrimaryKeyColumns(string table) =>
        Scalar($"SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS " +
               $"WHERE TABLE_SCHEMA = '{TestDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'PRIMARY'")?.ToString() ?? "(none)";

    private void Kindle() => ForgeKindler.KindleTheForge(Command(), Platform.MySQL, forceReKindle: true);

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
