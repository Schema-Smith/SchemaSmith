// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.IntegrationTests.MariaDb;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.MariaDb;

/// <summary>
/// A system-versioned table's explicit ROW START / ROW END columns are declared by role in <c>GeneratedAlways</c>.
/// Extraction left them out and nothing re-created them, so such a table deployed as a plain implicit one, and one keyed
/// on its row-end column did not deploy at all (#487). Declared, they are created with <c>PERIOD FOR SYSTEM_TIME</c>,
/// extracted with their role, and MariaDB's own addition of the ROW END column to every unique key compares equal to the
/// key as declared. A table that still has the implicit pair takes declared ones from MariaDB 11.7 with KEEP.
/// </summary>
[Category("MariaDb")]
[TestFixture]
public class ExplicitSystemTimeColumnTests : BaseTableQuenchTests
{
    protected override Platform Platform => Platform.MariaDb;
    protected override string MainDbName => FixtureSetup.MainDb;
    protected override string MainConnectionString => FixtureSetup.GetMainDbConnectionString();

    private static string Json(string table, bool explicitColumns = true, string pk = "Id") => $$"""
        [{
            "Name": "{{table}}",
            "IsSystemVersioned": true,
            "Columns": [
                { "Name": "Id", "DataType": "INT", "Nullable": false },
                { "Name": "Code", "DataType": "VARCHAR(20)", "Nullable": true }{{(explicitColumns ? """
                ,{ "Name": "rs", "DataType": "TIMESTAMP(6)", "Nullable": false, "GeneratedAlways": "RowStart" }
                ,{ "Name": "re", "DataType": "TIMESTAMP(6)", "Nullable": false, "GeneratedAlways": "RowEnd", "Invisible": true }
                """ : "")}}
            ],
            "Indexes": [
                { "Name": "PRIMARY", "IndexColumns": "{{pk}}", "PrimaryKey": true, "Unique": true },
                { "Name": "uq_code", "IndexColumns": "Code", "Unique": true }
            ]
        }]
        """;

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string Keys(IDbCommand cmd, string table) => Scalar(cmd,
        $"SELECT GROUP_CONCAT(CONCAT(INDEX_NAME, ':', cols) ORDER BY INDEX_NAME SEPARATOR ' ') FROM (SELECT INDEX_NAME, GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) cols " +
        $"FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' GROUP BY INDEX_NAME) k");

    private static string PeriodColumns(IDbCommand cmd, string table) => Scalar(cmd,
        $"SELECT GROUP_CONCAT(CONCAT(COLUMN_NAME, ':', GENERATION_EXPRESSION, ':', IF(EXTRA LIKE '%INVISIBLE%', 'invisible', 'visible')) ORDER BY GENERATION_EXPRESSION DESC SEPARATOR ' ') " +
        $"FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND GENERATION_EXPRESSION IN ('ROW START', 'ROW END')");

    private static int Audits(IDbCommand cmd, string table, string action = null) => Convert.ToInt32(Scalar(cmd,
        $"SELECT COUNT(*) FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID() AND ObjectName LIKE '%{table}%'" + (action == null ? "" : $" AND ActionType = '{action}'")));

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith_ChangeAudit WHERE SessionId = CONNECTION_ID()";
        cmd.ExecuteNonQuery();
    }

    private MariaDbTable Extract(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"CALL SchemaSmith_GenerateTableJSON('{_mainDb}', '{table}')";
        var json = "";
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                json += reader[0];
        return (MariaDbTable)PlatformDeserializer.DeserializeTable(json, Platform.MariaDb);
    }

    private void WithTables(Action<IDbCommand> body, params string[] tables)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.MariaDb).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        if (!FixtureSetup.SupportsSystemVersioning(Scalar(cmd, "SELECT VERSION()")))
            Assert.Ignore("System-versioned tables are MariaDB 10.3+.");
        void Drop()
        {
            cmd.CommandText = "SET @ss_system_versioning_alter_history = NULL; " + string.Concat(tables.Select(t => $"DROP TABLE IF EXISTS `{t}`; "));
            cmd.ExecuteNonQuery();
        }
        Drop();
        try { body(cmd); }
        finally { Drop(); }
    }

    [Test]
    public void DeclaredSystemTimeColumns_AreCreated_RedeployAsANoOp_AndRoundTripThroughExtraction()
    {
        const string table = "SysTimeExplicit";
        const string copy = "SysTimeExplicitCopy";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table));
            Assert.Multiple(() =>
            {
                Assert.That(PeriodColumns(cmd, table), Is.EqualTo("rs:ROW START:visible re:ROW END:invisible"), "created as declared");
                Assert.That(Keys(cmd, table), Is.EqualTo("PRIMARY:Id,re uq_code:Code,re"), "MariaDB puts the ROW END column on every unique key");
            });

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table));
            Assert.That(Audits(cmd, table), Is.Zero, "a redeploy touches nothing: the keys as declared compare equal to the keys MariaDB keeps");

            var extracted = Extract(cmd, table);
            Assert.Multiple(() =>
            {
                Assert.That(extracted.Columns.OfType<MariaDbColumn>().Where(c => c.GeneratedAlways != null).Select(c => $"{c.Name}:{c.GeneratedAlways}:{c.Invisible}"),
                    Is.EquivalentTo(new[] { "`rs`:RowStart:False", "`re`:RowEnd:True" }), "extracted with their role");
                Assert.That(extracted.Columns.OfType<MariaDbColumn>().Single(c => c.Name == "`rs`").GenerationExpression, Is.Null, "not as a generated column");
                Assert.That(extracted.Indexes.Select(i => $"{i.Name}:{i.IndexColumns}"), Is.EquivalentTo(new[] { "PRIMARY:`Id`", "uq_code:`Code`" }),
                    "the keys without the ROW END column MariaDB adds");
            });

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new[] { extracted }).Replace($"`{table}`", $"`{copy}`");
            RunTableQuenchProc(cmd, json);
            Assert.Multiple(() =>
            {
                Assert.That(PeriodColumns(cmd, copy), Is.EqualTo("rs:ROW START:visible re:ROW END:invisible"), "the extracted package builds the same table");
                Assert.That(Keys(cmd, copy), Is.EqualTo("PRIMARY:Id,re uq_code:Code,re"));
            });
        }, table, copy);
    }

    // #487: a package whose key names the ROW END column itself deploys, and redeploys clean.
    [Test]
    public void AKeyThatNamesTheRowEndColumn_Deploys_AndRedeploysClean()
    {
        const string table = "SysTimeKeyedOnEnd";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, pk: "Id,re"));
            Assert.That(Keys(cmd, table), Does.StartWith("PRIMARY:Id,re"));
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, pk: "Id,re"));
            Assert.That(Audits(cmd, table), Is.Zero);
        }, table);
    }

    [Test]
    public void ATableWithTheImplicitPair_TakesDeclaredColumns_FromMariaDb117_WithKeep_AndIsDowngradedBelow()
    {
        const string table = "SysTimeConvert";
        WithTables(cmd =>
        {
            var conversion = Scalar(cmd, "SELECT SchemaSmith_SupportsExplicitSystemTimeConversion()") == "1";
            RunTableQuenchProc(cmd, Json(table, explicitColumns: false));
            cmd.CommandText = $"INSERT INTO `{table}` (Id, Code) VALUES (1, 'a'); UPDATE `{table}` SET Code = 'b'";
            cmd.ExecuteNonQuery();
            Assert.That(PeriodColumns(cmd, table), Is.Empty, "precondition: the implicit pair");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table));
            if (!conversion)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(Audits(cmd, table, "downgraded"), Is.EqualTo(1), "below 11.7 the declared columns are recorded as a downgrade");
                    Assert.That(PeriodColumns(cmd, table), Is.Empty, "and the table keeps its implicit pair");
                });
                return;
            }

            Assert.That(PeriodColumns(cmd, table), Is.Empty, "without KEEP the history is not rewritten");
            cmd.CommandText = "SET @ss_system_versioning_alter_history = 'KEEP'";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table));
            Assert.Multiple(() =>
            {
                Assert.That(PeriodColumns(cmd, table), Is.EqualTo("rs:ROW START:visible re:ROW END:visible"),
                    "with KEEP the declared columns are added; MariaDB adds them visible, whatever is declared");
                Assert.That(Scalar(cmd, "SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = CONNECTION_ID() AND Message LIKE '%WARNING%visible%'"),
                    Is.Not.EqualTo("0"), "and the deploy says so");
                Assert.That(Scalar(cmd, $"SELECT COUNT(*) FROM `{table}` FOR SYSTEM_TIME ALL"), Is.EqualTo("2"), "and the history is kept");
            });
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table));
            Assert.That(Audits(cmd, table), Is.Zero, "then a redeploy touches nothing");
        }, table);
    }
}
