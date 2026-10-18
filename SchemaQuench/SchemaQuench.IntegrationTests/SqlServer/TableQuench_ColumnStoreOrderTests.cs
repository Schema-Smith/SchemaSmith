// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Newtonsoft.Json.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// An ordered columnstore index (<c>ORDER (…)</c>, clustered from 2022, nonclustered from 2025) arrived unordered on the
/// target: the order was neither extracted nor emitted. It is now declared as <c>Order</c>, created, compared and
/// extracted; below its version the index is created unordered and the downgrade recorded. A clustered columnstore over a
/// (max) or non-persisted computed column, which the engine refuses below 2017, is skipped there with a downgrade.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ColumnStoreOrderTests : BaseTableQuenchTests
{
    private static string Json(string table, string order, string extraColumn = "") => $$"""
        [{
            "Schema": "dbo",
            "Name": "{{table}}",
            "Columns": [
                { "Name": "A", "DataType": "INT", "Nullable": false },
                { "Name": "B", "DataType": "INT", "Nullable": false },
                { "Name": "C", "DataType": "VARCHAR(10)", "Nullable": true }{{extraColumn}}
            ],
            "Indexes": [
                { "Name": "CCI_{{table}}", "Clustered": true, "ColumnStore": true{{(order == null ? "" : $", \"Order\": \"{order}\"")}} }
            ]
        }]
        """;

    private static int Major(IDbCommand cmd)
    {
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string LiveOrder(IDbCommand cmd, string table, int major)
    {
        if (major < 16) return "";
        cmd.CommandText = $"""
            SELECT ISNULL(STUFF((SELECT ',' + COL_NAME(ic.[object_id], ic.column_id) FROM sys.indexes si
              JOIN sys.index_columns ic ON ic.[object_id] = si.[object_id] AND ic.index_id = si.index_id
             WHERE si.[object_id] = OBJECT_ID('dbo.{table}') AND si.[type] IN (5, 6) AND ic.column_store_order_ordinal > 0
             ORDER BY ic.column_store_order_ordinal FOR XML PATH('')), 1, 1, ''), '')
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string Audit(IDbCommand cmd, string objectName, string action)
    {
        cmd.CommandText = $"SELECT ISNULL(STUFF((SELECT ',' + CONVERT(NVARCHAR(MAX), ObjectType) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName = '{objectName}' AND ActionType = '{action}' FOR XML PATH('')), 1, 1, ''), '')";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private void WithTable(string table, Action<IDbCommand> body)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        try
        {
            body(cmd);
        }
        finally
        {
            cmd.CommandText = $"IF OBJECT_ID('dbo.{table}') IS NOT NULL DROP TABLE dbo.{table}";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void AnOrderedClusteredColumnstore_IsCreatedOrdered_RedeploysAsANoOp_AndExtractsItsOrder()
    {
        const string table = "CsOrderCreate";
        WithTable(table, cmd =>
        {
            var major = Major(cmd);
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "[B],[A]"));
            if (major < 16)
            {
                Assert.That(Audit(cmd, $"[dbo].[{table}].[CCI_{table}]", "downgraded"), Is.EqualTo("columnstore index without its ORDER"));
                return;
            }
            Assert.That(LiveOrder(cmd, table, major), Is.EqualTo("B,A"), "the index is created in the declared order");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, "B, A"));
            Assert.That(Audit(cmd, $"[dbo].[{table}].[CCI_{table}]", "created") + Audit(cmd, $"[dbo].[{table}].[CCI_{table}]", "dropped"), Is.Empty,
                "the same order, written differently, is not a change");

            cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
            var json = new System.Text.StringBuilder();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) json.Append(reader.GetString(0));
            var index = JObject.Parse(json.ToString())["Indexes"]?[0];
            Assert.That((string)index?["Order"], Is.EqualTo("[B],[A]"), json.ToString());
        });
    }

    [Test]
    public void ChangingTheOrder_ReCreatesTheIndex_AndDroppingItLeavesItUnordered()
    {
        const string table = "CsOrderChange";
        WithTable(table, cmd =>
        {
            var major = Major(cmd);
            if (major < 16) Assert.Ignore("Ordered columnstore needs SQL Server 2022.");
            RunTableQuenchProc(cmd, Json(table, "[B],[A]"));
            RunTableQuenchProc(cmd, Json(table, "[A]"));
            Assert.That(LiveOrder(cmd, table, major), Is.EqualTo("A"));
            RunTableQuenchProc(cmd, Json(table, null));
            Assert.That(LiveOrder(cmd, table, major), Is.Empty, "an Order no longer declared is removed");
        });
    }

    [Test]
    public void IndexOnly_CreatesTheOrder_OnBothIngestTiers()
    {
        const string table = "CsOrderIdxOnly";
        WithTable(table, cmd =>
        {
            var major = Major(cmd);
            if (major < 16) Assert.Ignore("Ordered columnstore needs SQL Server 2022.");
            cmd.CommandText = $"CREATE TABLE dbo.{table} (A INT NOT NULL, B INT NOT NULL, C VARCHAR(10) NULL)";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json(table, "[B],[A]"), indexOnly: true);
            Assert.That(LiveOrder(cmd, table, major), Is.EqualTo("B,A"), "JSON tier");

            cmd.CommandText = $"DROP INDEX CCI_{table} ON dbo.{table}";
            cmd.ExecuteNonQuery();
            var xmlScript = ForgeKindler.ResolveKindleScript("SchemaSmith.IndexOnlyXmlQuench.sql", Platform.SqlServer, false, false, serverMajorVersion: major)
                .Replace("SchemaSmith.IndexOnlyQuench", "SchemaSmith.IndexOnlyQuenchOrderTest");
            try
            {
                foreach (var batch in SqlServerBatchSplitter.Split(xmlScript))
                {
                    cmd.CommandText = batch;
                    cmd.ExecuteNonQuery();
                }
                cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuenchOrderTest @ProductName = 'OrderTest', @TableDefinitions = @p";
                var p = cmd.CreateParameter();
                p.ParameterName = "@p";
                p.Value = ModelXmlSerializer.ToIngestXml(Json(table, "[B],[A]"), "Tables", "Table");
                p.DbType = DbType.String;
                cmd.Parameters.Add(p);
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
                Assert.That(LiveOrder(cmd, table, major), Is.EqualTo("B,A"), "XML tier");
            }
            finally
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "DROP PROCEDURE IF EXISTS SchemaSmith.IndexOnlyQuenchOrderTest";
                cmd.ExecuteNonQuery();
            }
        });
    }

    // A columnstore index declared without CompressionType normalized to NONE, so the comparison expected no
    // DATA_COMPRESSION while the live index reports COLUMNSTORE: every redeploy dropped and rebuilt it. Extraction writes
    // the compression, which is why only a hand-written declaration churned.
    [TestCase(true)]
    [TestCase(false)]
    public void AColumnstoreDeclaredWithoutCompressionType_RedeploysAsANoOp(bool clustered)
    {
        var table = clustered ? "CsNoCompressionC" : "CsNoCompressionN";
        var json = clustered ? Json(table, null) : $$"""
            [{
                "Schema": "dbo", "Name": "{{table}}",
                "Columns": [ { "Name": "A", "DataType": "INT", "Nullable": false }, { "Name": "B", "DataType": "INT", "Nullable": false } ],
                "Indexes": [ { "Name": "PK_{{table}}", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[A]" },
                             { "Name": "CCI_{{table}}", "ColumnStore": true, "IncludeColumns": "[A],[B]" } ]
            }]
            """;
        WithTable(table, cmd =>
        {
            if (Major(cmd) < 13 && !clustered) Assert.Ignore("A nonclustered columnstore index is writable from SQL Server 2016.");
            RunTableQuenchProc(cmd, json);
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, json);
            Assert.That(Audit(cmd, $"[dbo].[{table}].[CCI_{table}]", "created") + Audit(cmd, $"[dbo].[{table}].CCI_{table}", "dropped"), Is.Empty,
                "an unchanged columnstore index is not rebuilt");
        });
    }

    // 35343 and 35307 on SQL Server 2016.
    [Test]
    public void AClusteredColumnstoreOverAMaxColumn_IsSkippedBelow2017()
    {
        const string table = "CsOrderMax";
        WithTable(table, cmd =>
        {
            var major = Major(cmd);
            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, null, ", { \"Name\": \"D\", \"DataType\": \"NVARCHAR(MAX)\", \"Nullable\": true }"));
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.{table}') AND [type] = 5";
            var created = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Multiple(() =>
            {
                Assert.That(created, Is.EqualTo(major >= 14 ? 1 : 0));
                Assert.That(Audit(cmd, $"[dbo].[{table}].[CCI_{table}]", "downgraded"),
                    Is.EqualTo(major >= 14 ? "" : "columnstore over a (max) or computed column (SQL Server 2017)"));
            });
        });
    }
}
