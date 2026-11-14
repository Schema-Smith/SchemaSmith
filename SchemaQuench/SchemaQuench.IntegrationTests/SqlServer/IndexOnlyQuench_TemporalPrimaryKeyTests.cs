// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

// B6: the index-only quench dropped a system-versioned table's primary key without suspending versioning first, so a
// changed key failed with 13557 ("Could not drop constraint"); the full quench has always suspended it.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class IndexOnlyQuench_TemporalPrimaryKeyTests : BaseTableQuenchTests
{
    private static string Json(string table, string keyColumns) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{keyColumns}}" }
    ]
  }
]
""";

    [Test]
    public void ChangingTheKeyOfASystemVersionedTable_KeepsItsHistory()
    {
        var table = $"IdxTemporal_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT)";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 13) Assert.Ignore("system-versioned tables arrive in SQL Server 2016");
        try
        {
            cmd.CommandText = $@"
CREATE TABLE [dbo].[{table}] (
  [Id] INT NOT NULL, [Code] INT NOT NULL,
  [ValidFrom] DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL, [ValidTo] DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
  PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
  CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED ([Id]))
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[{table}_History]));
INSERT [dbo].[{table}] ([Id], [Code]) VALUES (1, 1);
UPDATE [dbo].[{table}] SET [Code] = 2;";
            cmd.ExecuteNonQuery();

            Assert.DoesNotThrow(() => RunTableQuenchProc(cmd, Json(table, "[Id], [Code]"), indexOnly: true));

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.is_primary_key = 1";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "the key has its new shape");
            cmd.CommandText = $"SELECT OBJECT_NAME(history_table_id) FROM sys.tables WHERE [object_id] = OBJECT_ID('dbo.{table}') AND temporal_type = 2";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo($"{table}_History"), "versioning is back on, against the same history table");
            cmd.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}_History]";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1), "the history row survived");
        }
        finally
        {
            cmd.CommandText = $@"IF OBJECTPROPERTY(OBJECT_ID('dbo.{table}'), 'TableTemporalType') = 2 ALTER TABLE [dbo].[{table}] SET (SYSTEM_VERSIONING = OFF);
DROP TABLE IF EXISTS [dbo].[{table}]; DROP TABLE IF EXISTS [dbo].[{table}_History];";
            cmd.ExecuteNonQuery();
        }
    }
}
