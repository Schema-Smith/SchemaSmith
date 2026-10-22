// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A partially contained database keeps its catalog in a fixed collation (CATALOG_DEFAULT) while its own default, which
/// its table variables and temp tables take, can be anything. A comparison of a catalog name with one of those was a
/// compile-time collation conflict, so the forge could not even be kindled there.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class ContainedDatabaseTests : BaseTableQuenchTests
{
    [TestCase(IngestEncoding.Json)]
    [TestCase(IngestEncoding.Xml)]
    public void TheForgeKindles_InAPartiallyContainedDatabaseWithItsOwnCollation(IngestEncoding encoding)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE [name] = 'contained database authentication'";
        var containmentWas = Convert.ToInt32(cmd.ExecuteScalar());
        var db = $"Contained_{Guid.NewGuid():N}"[..28];
        try
        {
            if (containmentWas == 0) Run(cmd, "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
            Run(cmd, $"CREATE DATABASE [{db}] CONTAINMENT = PARTIAL COLLATE Japanese_CS_AS_KS");
            conn.ChangeDatabase(db);
            Assert.DoesNotThrow(() => ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding));
            cmd.CommandText = "SELECT COUNT(*) FROM sys.procedures WHERE SCHEMA_NAME([schema_id]) = 'SchemaSmith' AND [name] IN ('BootstrapTableQuench', 'IndexedViewQuench', 'TableQuench')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(3), "the procedures that failed to compile there exist");
        }
        finally
        {
            conn.ChangeDatabase("master");
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
            // Left on: fixtures in other shard processes create contained databases too, and switching it off under them fails both.
        }
    }

    // The deploy, a redeploy, the index-only path and extraction, each in a partially contained database whose own collation
    // is not the catalog's: every comparison of a catalog name with a temp table there is a collation conflict.
    [TestCase(IngestEncoding.Json)]
    [TestCase(IngestEncoding.Xml)]
    public void TheTablePipeline_DeploysRedeploysAndExtracts_InAPartiallyContainedDatabase(IngestEncoding encoding)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE [name] = 'contained database authentication'";
        var containmentWas = Convert.ToInt32(cmd.ExecuteScalar());
        var db = $"Contained_{Guid.NewGuid():N}"[..28];
        const string json = """
            [{ "Schema": "[dbo]", "Name": "[Parent]",
               "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Code]", "DataType": "NVARCHAR(20)", "Nullable": false } ],
               "Indexes": [ { "Name": "[PK_Parent]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                            { "Name": "[UX_Parent_Code]", "Unique": true, "IndexColumns": "[Code]" } ] },
             { "Schema": "[dbo]", "Name": "[Child]",
               "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[ParentId]", "DataType": "INT", "Nullable": true },
                            { "Name": "[Qty]", "DataType": "INT", "Nullable": false, "Default": "0", "CheckExpression": "[Qty] >= 0" },
                            { "Name": "[Label]", "DataType": "NVARCHAR(40)", "Nullable": true, "Description": "What the row is" },
                            { "Name": "[Twice]", "DataType": "INT", "Nullable": true, "ComputedExpression": "[Qty] * 2" } ],
               "Indexes": [ { "Name": "[PK_Child]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" },
                            { "Name": "[IX_Child_Parent]", "IndexColumns": "[ParentId]", "IncludeColumns": "[Label]" },
                            { "Name": "[IX_Child_Positive]", "IndexColumns": "[Qty]", "FilterExpression": "[Qty] > 0" } ],
               "Statistics": [ { "Name": "[ST_Child_Label]", "Columns": "[Label]" } ],
               "ForeignKeys": [ { "Name": "[FK_Child_Parent]", "Columns": "[ParentId]", "RelatedTable": "[Parent]", "RelatedTableSchema": "[dbo]", "RelatedColumns": "[Id]" } ],
               "CheckConstraints": [ { "Name": "[CK_Child_Label]", "Expression": "[Label] <> N''" } ] }]
            """;
        try
        {
            if (containmentWas == 0) Run(cmd, "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;");
            Run(cmd, $"CREATE DATABASE [{db}] CONTAINMENT = PARTIAL COLLATE Japanese_CS_AS_KS");
            conn.ChangeDatabase(db);
            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, forceReKindle: true, encoding);
            // Below compatibility level 130 the deploy hands the procedures XML, not JSON.
            string Payload(string tables) => encoding == IngestEncoding.Xml ? ModelXmlSerializer.ToIngestXml(tables, "Tables", "Table") : tables;

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Payload(json)), "the first deploy");
            var changed = json.Replace("NVARCHAR(40)", "NVARCHAR(80)").Replace("IX_Child_Positive", "IX_Child_Qty");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Payload(changed)), "a redeploy that changes a column and renames an index");
            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Payload(changed), indexOnly: true), "the index-only path");
            cmd.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = 'FK_Child_Parent'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the deploy built the key");

            cmd.CommandText = $"EXEC SchemaSmith.{(encoding == IngestEncoding.Xml ? "GenerateTableXml" : "GenerateTableJSON")} @p_Schema = 'dbo', @p_Table = 'Child'";
            string extracted = "";
            Assert.DoesNotThrow(() =>
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) extracted += reader.GetValue(0)?.ToString();
            }, "extraction");
            Assert.That(extracted, Does.Contain("FK_Child_Parent").And.Contain("IX_Child_Parent").And.Contain("CK_Child_Label")
                .And.Contain("IX_Child_Qty").And.Contain("ST_Child_Label").And.Contain("Twice").And.Contain("What the row is"));
        }
        finally
        {
            conn.ChangeDatabase("master");
            Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
            // Left on: fixtures in other shard processes create contained databases too, and switching it off under them fails both.
        }
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }
}
