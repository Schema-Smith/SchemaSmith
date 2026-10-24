// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

// A declared VECTOR index (MariaDB 11.7) used to deploy as a plain B-tree over the vector column, which then compared
// unequal on every redeploy. It is created as a vector index with the M and DISTANCE it declares, on both index paths.
[Category("MariaDb")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_VectorIndexTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();
    protected override string MainDbName => FixtureSetup.MainDb;

    private static string Json(string table, string options = """, "M": 8, "Distance": "cosine" """) => $$"""
        [{
            "Name": "{{table}}",
            "Columns": [
                { "Name": "id", "DataType": "INT", "Nullable": false },
                { "Name": "v", "DataType": "VECTOR(3)", "Nullable": false }
            ],
            "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "`id`" },
                { "Name": "vx", "IndexType": "VECTOR", "IndexColumns": "`v`"{{options}} }
            ]
        }]
        """;

    private void WithVectors(string prefix, Action<IDbCommand, string> body)
    {
        var table = $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        var version = cmd.ExecuteScalar()?.ToString() ?? "";
        var parts = version.Split('.', '-');
        if (parts.Length < 2 || int.Parse(parts[0]) * 100 + int.Parse(parts[1]) < 1107)
            Assert.Ignore("Vector indexes arrived in MariaDB 11.7.");
        cmd.CommandText = $"USE `{_mainDb}`";
        cmd.ExecuteNonQuery();
        try
        {
            body(cmd, table);
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{_mainDb}`.`{table}`";
            cmd.ExecuteNonQuery();
        }
    }

    private string IndexType(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT MAX(INDEX_TYPE) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = '{_mainDb}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'vx'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string CreateTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SHOW CREATE TABLE `{table}`";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? reader.GetString(1) : "";
    }

    private static string IndexAudit(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COALESCE(GROUP_CONCAT(ActionType ORDER BY Id), '') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectType = 'index' AND ObjectName = '{table}.vx'";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ADeclaredVectorIndex_IsCreatedWithItsOptions_AndARedeployLeavesItAlone(bool indexOnly)
    {
        WithVectors("VecIdx", (cmd, table) =>
        {
            if (indexOnly)
            {
                cmd.CommandText = $"CREATE TABLE `{table}` (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL)";
                cmd.ExecuteNonQuery();
            }
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table), indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(IndexType(cmd, table), Is.EqualTo("VECTOR"), "the index is a vector index, not a B-tree over the vector");
                Assert.That(CreateTable(cmd, table), Does.Contain("`M`='8'").And.Contain("`DISTANCE`='cosine'"), "with the options it declares");
                Assert.That(IndexAudit(cmd, table), Is.EqualTo("created"));
            });

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table), indexOnly);
            Assert.That(IndexAudit(cmd, table), Is.Empty, "a redeploy leaves the vector index alone");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WhatIf_PreviewsTheVectorIndexWithItsOptions_AndCreatesNothing(bool indexOnly)
    {
        WithVectors("VecWi", (cmd, table) =>
        {
            cmd.CommandText = $"CREATE TABLE `{table}` (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table), indexOnly, whatIf: true);
            cmd.CommandText = "SELECT COALESCE(GROUP_CONCAT(Message SEPARATOR ' | '), '') FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%CREATE %INDEX%vx%'";
            var preview = cmd.ExecuteScalar()?.ToString() ?? "";
            Assert.Multiple(() =>
            {
                Assert.That(preview, Does.Contain("CREATE VECTOR INDEX `vx`").And.Contain("M=8 DISTANCE='cosine'"), "the preview shows the statement the deploy would run");
                Assert.That(IndexType(cmd, table), Is.Empty, "and nothing is created");
            });
        });
    }

    [Test]
    public void ADeclaredVectorIndexWithoutOptions_TakesTheServerDefaults()
    {
        WithVectors("VecDef", (cmd, table) =>
        {
            RunTableQuenchProc(cmd, Json(table, ""));
            Assert.Multiple(() =>
            {
                Assert.That(IndexType(cmd, table), Is.EqualTo("VECTOR"));
                Assert.That(CreateTable(cmd, table), Does.Not.Contain("`M`=").And.Not.Contain("`DISTANCE`="));
            });
        });
    }

    // What an earlier release made of a declared vector index: a B-tree over the column. The deploy replaces it.
    [Test]
    public void ABTreeWhereAVectorIndexIsDeclared_IsReplacedByOne()
    {
        WithVectors("VecFix", (cmd, table) =>
        {
            cmd.CommandText = $"CREATE TABLE `{table}` (id INT NOT NULL PRIMARY KEY, v VECTOR(3) NOT NULL, KEY vx (v))";
            cmd.ExecuteNonQuery();
            Assert.That(IndexType(cmd, table), Is.EqualTo("BTREE"));

            RunTableQuenchProc(cmd, Json(table));
            Assert.That(IndexType(cmd, table), Is.EqualTo("VECTOR"));
        });
    }
}
