// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// The live index list read sys.partitions row by row, so a partitioned index appeared once per partition: a
// compression change rebuilt the whole index once per differing partition, and fill-factor and option-only rebuilds
// ran once per partition. Each fixup now rebuilds a partitioned index once, and only the partitions that differ.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class TableQuench_PartitionedIndexRebuildTests : BaseTableQuenchTests
{
    private const string Function = "PF_SchemaSmithMixed4";
    private const string Scheme = "PS_SchemaSmithMixed4";

    public enum Tier { TableQuench, IndexOnly, IndexOnlyXml }

    [OneTimeSetUp]
    public void EnsurePartitionScheme()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE [name] = '{Function}')
  CREATE PARTITION FUNCTION [{Function}] (INT) AS RANGE RIGHT FOR VALUES (10, 20, 30);
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE [name] = '{Scheme}')
  CREATE PARTITION SCHEME [{Scheme}] AS PARTITION [{Function}] ALL TO ([PRIMARY]);";
        cmd.ExecuteNonQuery();
    }

    private static string Json(string table, string compression = "NONE", int fillFactor = 80, bool padIndex = false) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "PartitionScheme": "[{{Scheme}}]",
    "PartitionColumn": "[Id]",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false },
      { "Name": "[V]",  "DataType": "INT", "Nullable": true }
    ],
    "Indexes": [
      { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]",
        "PartitionScheme": "[{{Scheme}}]", "PartitionColumn": "[Id]" },
      { "Name": "[IX_{{table}}_V]", "IndexColumns": "[V]", "PartitionScheme": "[{{Scheme}}]", "PartitionColumn": "[Id]",
        "CompressionType": "{{compression}}", "FillFactor": {{fillFactor}}, "UpdateFillFactor": true, "PadIndex": {{(padIndex ? "true" : "false")}} }
    ]
  }
]
""";

    private sealed class Deploy : IDisposable
    {
        public readonly SqlConnection Conn;
        public readonly List<string> Messages = new();
        public readonly string Table = $"MixCmp_{Guid.NewGuid().ToString("N")[..8]}";
        private readonly string _xmlProc;
        private readonly string _product;

        public Deploy(string connectionString, string db, string product)
        {
            _product = product;
            Conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(connectionString);
            Conn.Open();
            Conn.ChangeDatabase(db);
            Conn.InfoMessage += (_, e) => Messages.AddRange(e.Errors.Cast<SqlError>().Select(x => x.Message));
            _xmlProc = $"IndexOnlyXml_{Table}";
        }

        public void Exec(string sql, params (string name, string value)[] parameters)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            foreach (var (n, v) in parameters) cmd.Parameters.Add(new SqlParameter(n, SqlDbType.NVarChar, -1) { Value = v });
            cmd.ExecuteNonQuery();
        }

        public string Scalar(string sql)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString();
        }

        public void Quench(Tier tier, string json)
        {
            Messages.Clear();
            Exec("DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID");
            switch (tier)
            {
                case Tier.TableQuench:
                    Exec("EXEC SchemaSmith.TableQuench @ProductName = @n, @TableDefinitions = @p, @WhatIf = 0, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0", ("@p", json), ("@n", _product));
                    break;
                case Tier.IndexOnly:
                    Exec("EXEC SchemaSmith.IndexOnlyQuench @ProductName = @n, @TableDefinitions = @p, @DropUnknownIndexes = 0", ("@p", json), ("@n", _product));
                    break;
                default:
                    if (string.IsNullOrEmpty(Scalar($"SELECT OBJECT_ID('SchemaSmith.{_xmlProc}')")))
                        foreach (var b in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench().Replace("SchemaSmith.IndexOnlyQuenchXmlTest", $"SchemaSmith.{_xmlProc}")))
                            Exec(b);
                    Exec($"EXEC SchemaSmith.{_xmlProc} @ProductName = @n, @TableDefinitions = @p, @DropUnknownIndexes = 0",
                        ("@p", ModelXmlSerializer.ToIngestXml(json, "Tables", "Table")), ("@n", _product));
                    break;
            }
        }

        public int Lines(string fragment) => Messages.Count(m => m.Contains(fragment));

        public string Log => string.Join(" | ", Messages.Where(m => m.Contains(Table)));

        public int Audits(string actionType) => int.Parse(Scalar(
            $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectType = 'index' AND ActionType = '{actionType}' AND ObjectName LIKE '%IX[_]{Table}[_]V%'"));

        public string Partitions() => Scalar(
            $"SELECT STRING_AGG(CAST(p.partition_number AS VARCHAR(3)) + ':' + p.data_compression_desc, ',') WITHIN GROUP (ORDER BY p.partition_number) " +
            $"FROM sys.partitions p JOIN sys.indexes i ON i.[object_id] = p.[object_id] AND i.index_id = p.index_id WHERE i.[name] = 'IX_{Table}_V'");

        public void Dispose()
        {
            Exec($"DROP TABLE IF EXISTS [dbo].[{Table}]; DROP PROCEDURE IF EXISTS SchemaSmith.{_xmlProc};");
            Conn.Dispose();
        }
    }

    private Deploy Seeded(string compression = "NONE", int fillFactor = 80, bool padIndex = false)
    {
        var d = new Deploy(_connectionString, _mainDb, _productName);
        d.Quench(Tier.TableQuench, Json(d.Table, compression, fillFactor, padIndex));
        d.Exec($"INSERT INTO [dbo].[{d.Table}] ([Id], [V]) VALUES (5, 1), (15, 2), (25, 3), (35, 4)");
        return d;
    }

    [TestCase(Tier.TableQuench)]
    [TestCase(Tier.IndexOnly)]
    [TestCase(Tier.IndexOnlyXml)]
    public void ACompressionChangeOnEveryPartition_RebuildsTheIndexOnce(Tier tier)
    {
        using var d = Seeded();
        d.Quench(tier, Json(d.Table, "PAGE"));
        Assert.Multiple(() =>
        {
            Assert.That(d.Partitions(), Is.EqualTo("1:PAGE,2:PAGE,3:PAGE,4:PAGE"));
            Assert.That(d.Lines("Altering index compression for"), Is.EqualTo(1), () => "one rebuild for four partitions: " + d.Log);
            Assert.That(d.Audits("modified"), Is.EqualTo(1), "and one 'modified' row for the index");
        });
    }

    [TestCase(Tier.TableQuench)]
    [TestCase(Tier.IndexOnly)]
    [TestCase(Tier.IndexOnlyXml)]
    public void ACompressionDriftOnOnePartition_RebuildsOnlyThatPartition(Tier tier)
    {
        using var d = Seeded("PAGE");
        d.Exec($"ALTER INDEX [IX_{d.Table}_V] ON [dbo].[{d.Table}] REBUILD PARTITION = 3 WITH (DATA_COMPRESSION = NONE)");
        d.Quench(tier, Json(d.Table, "PAGE"));
        Assert.Multiple(() =>
        {
            Assert.That(d.Partitions(), Is.EqualTo("1:PAGE,2:PAGE,3:PAGE,4:PAGE"));
            Assert.That(d.Messages.Where(m => m.Contains("Altering index compression for")), Has.Exactly(1).Contains("partitions 3 of 4"),
                "one rebuild, naming the one partition that differed");
            Assert.That(d.Audits("modified"), Is.EqualTo(1));
        });
    }

    [TestCase(Tier.TableQuench)]
    [TestCase(Tier.IndexOnly)]
    [TestCase(Tier.IndexOnlyXml)]
    public void AFillFactorDrift_RebuildsAPartitionedIndexOnce(Tier tier)
    {
        using var d = Seeded();
        d.Exec($"ALTER INDEX [IX_{d.Table}_V] ON [dbo].[{d.Table}] REBUILD WITH (FILLFACTOR = 90)");
        d.Quench(tier, Json(d.Table));
        Assert.Multiple(() =>
        {
            Assert.That(d.Scalar($"SELECT fill_factor FROM sys.indexes WHERE [name] = 'IX_{d.Table}_V'"), Is.EqualTo("80"));
            Assert.That(d.Messages.Count(m => m.Contains("fillfactor in") && m.Contains($"IX_{d.Table}_V")), Is.EqualTo(1), () => "one rebuild for four partitions: " + d.Log);
        });
    }

    [TestCase(Tier.TableQuench)]
    [TestCase(Tier.IndexOnly)]
    [TestCase(Tier.IndexOnlyXml)]
    public void AnOptionOnlyChange_RebuildsAPartitionedIndexOnce(Tier tier)
    {
        using var d = Seeded();
        d.Quench(tier, Json(d.Table, padIndex: true));
        Assert.Multiple(() =>
        {
            Assert.That(d.Scalar($"SELECT CAST(is_padded AS VARCHAR(1)) FROM sys.indexes WHERE [name] = 'IX_{d.Table}_V'"), Is.EqualTo("1"));
            Assert.That(d.Messages.Count(m => m.Contains("Rebuilding index") && m.Contains($"IX_{d.Table}_V")), Is.EqualTo(1), () => "one rebuild for four partitions: " + d.Log);
            Assert.That(d.Audits("modified"), Is.EqualTo(1), "and one 'modified' row");
        });
    }

    private static string HeapJson(string table, string compression) => $$"""
[
  {
    "Schema": "[dbo]",
    "Name": "[{{table}}]",
    "PartitionScheme": "[{{Scheme}}]",
    "PartitionColumn": "[Id]",
    "CompressionType": "{{compression}}",
    "Columns": [
      { "Name": "[Id]", "DataType": "INT", "Nullable": false },
      { "Name": "[V]",  "DataType": "INT", "Nullable": true }
    ]
  }
]
""";

    [Test]
    public void ATableCompressionChangeOnAPartitionedHeap_RebuildsTheTableOnce()
    {
        using var d = new Deploy(_connectionString, _mainDb, _productName);
        d.Quench(Tier.TableQuench, HeapJson(d.Table, "NONE"));
        d.Exec($"INSERT INTO [dbo].[{d.Table}] ([Id], [V]) VALUES (5, 1), (15, 2), (25, 3), (35, 4)");
        d.Exec($"ALTER TABLE [dbo].[{d.Table}] REBUILD PARTITION = 2 WITH (DATA_COMPRESSION = PAGE)");

        d.Quench(Tier.TableQuench, HeapJson(d.Table, "PAGE"));

        Assert.Multiple(() =>
        {
            Assert.That(d.Scalar($"SELECT STRING_AGG(CAST(partition_number AS VARCHAR(3)) + ':' + data_compression_desc, ',') WITHIN GROUP (ORDER BY partition_number) FROM sys.partitions WHERE [object_id] = OBJECT_ID('dbo.{d.Table}') AND index_id = 0"),
                Is.EqualTo("1:PAGE,2:PAGE,3:PAGE,4:PAGE"));
            Assert.That(d.Messages.Where(m => m.Contains("Altering table compression for")), Has.Exactly(1).Contains("partitions 1, 3, 4 of 4"),
                () => "one rebuild, of the partitions that differed: " + d.Log);
        });
    }

    [TestCase(Tier.TableQuench)]
    [TestCase(Tier.IndexOnly)]
    [TestCase(Tier.IndexOnlyXml)]
    public void AUniformRedeploy_TouchesNothing(Tier tier)
    {
        using var d = Seeded("PAGE");
        d.Quench(tier, Json(d.Table, "PAGE"));
        Assert.Multiple(() =>
        {
            Assert.That(d.Lines("Altering index compression for"), Is.Zero);
            Assert.That(d.Lines("Rebuilding index"), Is.Zero);
            Assert.That(d.Lines("Dropping index"), Is.Zero, () => "a redeploy drops nothing: " + d.Log);
            Assert.That(d.Audits("modified"), Is.Zero);
        });
    }
}
