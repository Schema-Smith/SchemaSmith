// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.SqlServer;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

// STOPLIST = OFF and the system stoplist both read back as [SYSTEM], and a declared "OFF" was emitted as a stoplist
// NAME, [OFF]. Probed on 2022: OFF is stored as a NULL stoplist_id and the system stoplist as 0.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_FullTextStopListOffTests : BaseTableQuenchTests
{
    [Test]
    public void StopListOff_IsCreated_RedeploysAsANoOp_AndExtractsAsOff()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"FtOff_{suffix}";
        var catalog = $"ftc_{suffix}";
        var json = $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Columns": [
      { "Name": "[Id]",  "DataType": "INT",           "Nullable": false },
      { "Name": "[Txt]", "DataType": "NVARCHAR(100)", "Nullable": true }
    ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]" } ],
    "FullTextIndex": { "FullTextCatalog": "{{catalog}}", "KeyIndex": "PK_{{table}}", "ChangeTracking": "AUTO", "StopList": "OFF", "Columns": "[Txt]" }
  }
]
""";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS INT)";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) Assert.Ignore("Full-Text Search is not installed on this server.");
        cmd.CommandText = $"CREATE FULLTEXT CATALOG [{catalog}]";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT ISNULL(CAST(stoplist_id AS VARCHAR(10)), 'NULL') FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID('dbo.{table}')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("NULL"), "created with STOPLIST = OFF");

            cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, json);
            cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%' AND ObjectType LIKE '%ull%ext%'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(0), "the unchanged full-text index is left as it is");

            var extracted = GenerateTable(cmd, table);
            Assert.That(extracted.FullTextIndex.Single().StopList, Is.EqualTo("OFF"), "extraction writes OFF, not the system stoplist");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]; DROP FULLTEXT CATALOG [{catalog}]";
            cmd.ExecuteNonQuery();
        }
    }

    private static SqlServerTable GenerateTable(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = TransientRetry.Run(() => cmd.ExecuteReader());
        var json = string.Empty;
        while (reader.Read()) json += $"{reader.GetString(0)}\r\n";
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }
}
