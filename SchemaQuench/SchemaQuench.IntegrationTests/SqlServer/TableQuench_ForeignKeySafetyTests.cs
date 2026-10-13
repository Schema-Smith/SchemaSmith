// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

// Dropping or changing a unique index used to drop, first, every foreign key referencing the index's TABLE -- keys on
// another column included. A full deploy then put back only the keys the package declares, and an index-only deploy
// put back none, so a key created outside the package was lost. A deploy now never drops a key it will not re-create:
// an index such a key references is kept as it is.
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_ForeignKeySafetyTests : BaseTableQuenchTests
{
    private sealed record Tables(string Parent, string ByCode, string ById, string UqCode, string UqOther, string PkName);

    private static Tables Create(IDbCommand cmd)
    {
        var u = Guid.NewGuid().ToString("N")[..8];
        var t = new Tables($"FkSafeParent_{u}", $"FkSafeByCode_{u}", $"FkSafeById_{u}", $"UQ_Code_{u}", $"UQ_Other_{u}", $"PK_FkSafeParent_{u}");
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"
CREATE TABLE dbo.[{t.Parent}] (Id INT NOT NULL CONSTRAINT [{t.PkName}] PRIMARY KEY, Code INT NOT NULL, Other INT NOT NULL);
CREATE UNIQUE INDEX [{t.UqCode}] ON dbo.[{t.Parent}] (Code);
CREATE UNIQUE INDEX [{t.UqOther}] ON dbo.[{t.Parent}] (Other);
CREATE TABLE dbo.[{t.ByCode}] (Id INT NOT NULL PRIMARY KEY, ParentCode INT NOT NULL,
  CONSTRAINT [FK_{t.ByCode}] FOREIGN KEY (ParentCode) REFERENCES dbo.[{t.Parent}] (Code));
CREATE TABLE dbo.[{t.ById}] (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL,
  CONSTRAINT [FK_{t.ById}] FOREIGN KEY (ParentId) REFERENCES dbo.[{t.Parent}] (Id));";
        cmd.ExecuteNonQuery();
        return t;
    }

    private static void Drop(IDbCommand cmd, Tables t)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.[{t.ByCode}], dbo.[{t.ById}]; DROP TABLE IF EXISTS dbo.[{t.Parent}];";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"DROP PROCEDURE IF EXISTS {XmlProc};";
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    private static string ForeignKeys(IDbCommand cmd, Tables t) => Scalar(cmd,
        $"SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID('dbo.[{t.Parent}]')");

    private static string ParentIndexes(IDbCommand cmd, Tables t) => Scalar(cmd,
        $"SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.indexes WHERE [object_id] = OBJECT_ID('dbo.[{t.Parent}]') AND index_id > 0");

    private static string ParentJson(Tables t, string extraIndexes = "", bool withColumns = true) => $$"""
        [{ "Schema": "[dbo]", "Name": "[{{t.Parent}}]",
           {{(withColumns ? """ "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[Code]", "DataType": "INT", "Nullable": false }, { "Name": "[Other]", "DataType": "INT", "Nullable": false } ], """ : "")}}
           "Indexes": [ { "Name": "[{{t.PkName}}]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]" }{{extraIndexes}} ] }]
        """;

    public enum Mode { Full, IndexOnly, IndexOnlyXml }

    // The XML-tier index-only procedure, kindled under its own name so it cannot collide with IndexOnlyXmlQuenchTests'.
    private const string XmlProc = "SchemaSmith.IndexOnlyQuenchXmlFkTest";

    private void Run(IDbCommand cmd, Mode mode, string json)
    {
        cmd.CommandTimeout = 300;
        if (mode == Mode.IndexOnlyXml)
        {
            foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench().Replace("SchemaSmith.IndexOnlyQuenchXmlTest", XmlProc)))
            {
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }
            json = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");
        }
        var proc = mode switch { Mode.Full => "SchemaSmith.TableQuench", Mode.IndexOnly => "SchemaSmith.IndexOnlyQuench", _ => XmlProc };
        cmd.CommandText = $"EXEC {proc} @ProductName = 'FkSafety', @TableDefinitions = N'{json.Replace("'", "''")}', @DropUnknownIndexes = 1";
        cmd.ExecuteNonQuery();
    }

    [TestCase(Mode.Full, TestName = "FullDeploy_DroppingUnknownUniqueIndexes_KeepsForeignKeysOutsideThePackage")]
    [TestCase(Mode.IndexOnly, TestName = "IndexOnly_DroppingUnknownUniqueIndexes_NeverDropsAForeignKey")]
    [TestCase(Mode.IndexOnlyXml, TestName = "IndexOnlyXml_DroppingUnknownUniqueIndexes_NeverDropsAForeignKey")]
    public void DroppingUnknownUniqueIndexes_KeepsForeignKeysOutsideThePackage(Mode mode)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        var t = Create(cmd);
        try
        {
            Assert.DoesNotThrow(() => Run(cmd, mode, ParentJson(t, withColumns: mode == Mode.Full)));

            Assert.Multiple(() =>
            {
                Assert.That(ForeignKeys(cmd, t), Is.EqualTo($"FK_{t.ByCode},FK_{t.ById}"), "keys outside the package are not the deploy's to drop");
                Assert.That(ParentIndexes(cmd, t), Is.EqualTo($"{t.PkName},{t.UqCode}"),
                    "the index a key references is kept; the unreferenced unknown index is dropped");
            });
        }
        finally
        {
            Drop(cmd, t);
        }
    }

    [TestCase(Mode.Full, TestName = "FullDeploy_ChangingAUniqueIndexAKeyOutsideThePackageReferences_KeepsBoth")]
    [TestCase(Mode.IndexOnly, TestName = "IndexOnly_ChangingAUniqueIndexAForeignKeyReferences_KeepsBoth")]
    [TestCase(Mode.IndexOnlyXml, TestName = "IndexOnlyXml_ChangingAUniqueIndexAForeignKeyReferences_KeepsBoth")]
    public void ChangingAReferencedUniqueIndex_KeepsTheIndexAndTheKey(Mode mode)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        var t = Create(cmd);
        try
        {
            var changed = $$""", { "Name": "[{{t.UqCode}}]", "Unique": true, "IndexColumns": "[Code], [Other]" }, { "Name": "[{{t.UqOther}}]", "Unique": true, "IndexColumns": "[Other]" }""";
            Assert.DoesNotThrow(() => Run(cmd, mode, ParentJson(t, changed, withColumns: mode == Mode.Full)));

            Assert.Multiple(() =>
            {
                Assert.That(ForeignKeys(cmd, t), Is.EqualTo($"FK_{t.ByCode},FK_{t.ById}"));
                Assert.That(Scalar(cmd, $"SELECT STRING_AGG(COL_NAME(ic.[object_id], ic.column_id), ',') WITHIN GROUP (ORDER BY ic.key_ordinal) " +
                                        $"FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id " +
                                        $"WHERE i.[object_id] = OBJECT_ID('dbo.[{t.Parent}]') AND i.name = '{t.UqCode}'"),
                    Is.EqualTo("Code"), "a key the deploy will not re-create references it, so it is kept as it is");
            });
        }
        finally
        {
            Drop(cmd, t);
        }
    }
}
