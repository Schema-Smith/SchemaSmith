// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// MY-022: a generated column switched between VIRTUAL and STORED was compared nowhere, so it kept its old kind at exit
/// 0. Neither engine can MODIFY between the two; the column now joins the generated-status drop and re-add.
/// </summary>
public abstract class TableQuench_GeneratedColumnKindSharedTests : BaseTableQuenchTests
{
    [Test]
    public void AGeneratedColumnSwitchedBetweenVirtualAndStored_TakesTheDeclaredKind_ThenTheRedeployIsANoOp()
    {
        var t = $"genkind_{Guid.NewGuid():N}"[..16];
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        string Json(string kind) => $$"""
            [{ "Name": "{{t}}",
               "Columns": [ { "Name": "Id", "DataType": "INT", "Nullable": false }, { "Name": "A", "DataType": "INT", "Nullable": true },
                            { "Name": "G", "DataType": "INT", "Nullable": true, "GenerationExpression": "A * 2", "Generated": "{{kind}}" } ],
               "Indexes": [ { "Name": "PRIMARY", "PrimaryKey": true, "Unique": true, "IndexColumns": "Id" } ] }]
            """;
        string Scalar(string sql)
        {
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }
        string Kind() => Scalar($"SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{t}' AND COLUMN_NAME = 'G'").ToUpperInvariant();
        string Audits() => Scalar($"SELECT IFNULL(GROUP_CONCAT(CONCAT(ObjectType, ':', ActionType)), '') FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{t}%'");
        void Deploy(string kind)
        {
            cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(kind));
        }
        try
        {
            Deploy("VIRTUAL");
            Assert.That(Kind(), Does.Contain("VIRTUAL"), "precondition");
            cmd.CommandText = $"INSERT INTO `{t}` (Id, A) VALUES (1, 21)";
            cmd.ExecuteNonQuery();

            Deploy("STORED");
            Assert.Multiple(() =>
            {
                Assert.That(Kind(), Does.Contain("STORED"), "VIRTUAL became STORED");
                Assert.That(Scalar($"SELECT G FROM `{t}` WHERE Id = 1"), Is.EqualTo("42"), "the row keeps its value");
            });
            Deploy("STORED");
            Assert.That(Audits(), Is.Empty, "then a redeploy is a no-op");
            Deploy("VIRTUAL");
            Assert.That(Kind(), Does.Contain("VIRTUAL"), "and back");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS `{t}`; DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectName LIKE '{t}%'";
            cmd.ExecuteNonQuery();
        }
    }
}
