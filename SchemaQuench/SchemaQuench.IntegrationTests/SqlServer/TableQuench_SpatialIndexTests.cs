// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Domain.SqlServer;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// Spatial indexes are declared in their own list, <c>SpatialIndexes</c>, and deploy, redeploy, extract and converge like
/// any other index. A declared attribute that differs re-creates the index; an omitted one is the engine's choice and
/// is not compared. A change to the clustered primary key takes the spatial indexes with it and the deploy puts them
/// back, since SQL Server will not keep a spatial index without one. One the package stops declaring is dropped.
/// </summary>
[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_SpatialIndexTests : BaseTableQuenchTests
{
    private const string GeometryGrid = """
        { "Name": "[SX_Shape]", "Column": "[Shape]", "Tessellation": "GEOMETRY_GRID",
          "BoundingBox": { "XMin": 0, "YMin": 0, "XMax": 500, "YMax": 200 },
          "Grids": { "Level1": "MEDIUM", "Level2": "LOW", "Level3": "HIGH", "Level4": "MEDIUM" },
          "CellsPerObject": 64, "FillFactor": 80 }
        """;

    private const string GeographyAuto = """{ "Name": "[SX_Location]", "Column": "[Location]", "Tessellation": "GEOGRAPHY_AUTO_GRID" }""";

    private static string Json(string table, string spatial, string pkColumns = "[Id]") => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{table}}]",
            "Columns": [
                { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                { "Name": "[Region]", "DataType": "INT", "Nullable": false },
                { "Name": "[Shape]", "DataType": "GEOMETRY", "Nullable": true },
                { "Name": "[Location]", "DataType": "GEOGRAPHY", "Nullable": true }
            ],
            "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "{{pkColumns}}" } ],
            "SpatialIndexes": [ {{spatial}} ]
        }]
        """;

    private static string Shape(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"""
            SELECT STRING_AGG(CAST(i.name + ':' + i.tessellation_scheme + ':' + ISNULL(CONVERT(VARCHAR(20), tz.bounding_box_xmax), '-') + ':' +
                              ISNULL(tz.level_2_grid_desc, '-') + ':' + CONVERT(VARCHAR(10), tz.cells_per_object) + ':' + CONVERT(VARCHAR(10), i.fill_factor) AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, ',')
                   WITHIN GROUP (ORDER BY i.name)
              FROM sys.spatial_indexes i JOIN sys.spatial_index_tessellations tz ON tz.[object_id] = i.[object_id] AND tz.index_id = i.index_id
             WHERE i.[object_id] = OBJECT_ID('dbo.{table}')
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static int Audits(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID AND ObjectName LIKE '%{table}%'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ClearAudit(IDbCommand cmd)
    {
        cmd.CommandText = "DELETE FROM SchemaSmith.ChangeAudit WHERE SessionId = @@SPID";
        cmd.ExecuteNonQuery();
    }

    private static SqlServerTable Extract(IDbCommand cmd, string table)
    {
        cmd.CommandText = $"EXEC [SchemaSmith].GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var json = "";
        while (reader.Read()) json += reader.GetString(0);
        return (SqlServerTable)PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);
    }

    private void WithTables(Action<IDbCommand> body, params string[] tables)
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
            foreach (var t in tables)
            {
                cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{t}";
                cmd.ExecuteNonQuery();
            }
        }
    }

    [Test]
    public void DeclaredSpatialIndexes_AreCreatedAsWritten_RedeployAsANoOp_AndRoundTripThroughExtraction()
    {
        const string table = "SpatialDeclared";
        const string copy = "SpatialDeclaredCopy";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeometryGrid + "," + GeographyAuto));
            var shape = Shape(cmd, table);
            Assert.That(shape, Is.EqualTo("SX_Location:GEOGRAPHY_AUTO_GRID:-:-:12:0,SX_Shape:GEOMETRY_GRID:500:LOW:64:80"), "created as declared, defaults where omitted");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, GeometryGrid + "," + GeographyAuto));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, table), Is.Zero, "a redeploy touches nothing");
                Assert.That(Shape(cmd, table), Is.EqualTo(shape));
            });

            var extracted = Extract(cmd, table);
            var geometry = extracted.SpatialIndexes.Single(s => s.Name == "[SX_Shape]");
            Assert.Multiple(() =>
            {
                Assert.That(extracted.SpatialIndexes.Select(s => s.Name), Is.EquivalentTo(new[] { "[SX_Shape]", "[SX_Location]" }));
                Assert.That($"{geometry.Column}|{geometry.Tessellation}|{geometry.BoundingBox?.XMax}|{geometry.Grids?.Level3}|{geometry.CellsPerObject}|{geometry.FillFactor}",
                    Is.EqualTo("[Shape]|GEOMETRY_GRID|500|HIGH|64|80"));
                Assert.That(extracted.SpatialIndexes.Single(s => s.Name == "[SX_Location]").BoundingBox, Is.Null, "a geography index has no box");
                Assert.That(extracted.Indexes.Select(i => i.Name), Is.EqualTo(new[] { $"[PK_{table}]" }), "and they are not ordinary indexes");
            });

            // Back to source: the extracted list re-creates the same indexes on a second table.
            var spatialJson = Newtonsoft.Json.JsonConvert.SerializeObject(extracted.SpatialIndexes);
            RunTableQuenchProc(cmd, Json(copy, spatialJson.Substring(1, spatialJson.Length - 2)));
            Assert.That(Shape(cmd, copy), Is.EqualTo(shape), "the extracted package deploys the same spatial indexes");
        }, table, copy);
    }

    [Test]
    public void AChangedDeclaredAttribute_RecreatesTheIndex_AndAnOmittedOneIsNotCompared()
    {
        const string table = "SpatialChange";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeometryGrid));
            RunTableQuenchProc(cmd, Json(table, GeometryGrid.Replace("\"CellsPerObject\": 64", "\"CellsPerObject\": 32")));
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:32:80"), "the changed cells per object is applied");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, """{ "Name": "[SX_Shape]", "Column": "[Shape]", "BoundingBox": { "XMin": 0, "YMin": 0, "XMax": 500, "YMax": 200 } }"""));
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, table), Is.Zero, "a package naming only the column and box leaves the rest as it is");
                Assert.That(Shape(cmd, table), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:32:80"));
            });
        }, table);
    }

    // Each declared attribute is compared, not only the cells per object: a changed box, grid level or fill factor that
    // compared equal would leave the old index in place.
    [TestCase("\"XMax\": 500", "\"XMax\": 600", "SX_Shape:GEOMETRY_GRID:600:LOW:64:80", TestName = "AChangedBoundingBox_RecreatesTheIndex")]
    [TestCase("\"Level2\": \"LOW\"", "\"Level2\": \"HIGH\"", "SX_Shape:GEOMETRY_GRID:500:HIGH:64:80", TestName = "AChangedGridLevel_RecreatesTheIndex")]
    [TestCase("\"FillFactor\": 80", "\"FillFactor\": 70", "SX_Shape:GEOMETRY_GRID:500:LOW:64:70", TestName = "AChangedFillFactor_RecreatesTheIndex")]
    public void AChangedSpatialAttribute_RecreatesTheIndex(string before, string after, string expected)
    {
        const string table = "SpatialAttr";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeometryGrid));
            var changed = GeometryGrid.Replace(before, after);
            Assert.That(changed, Is.Not.EqualTo(GeometryGrid), "setup: the declaration changes");
            RunTableQuenchProc(cmd, Json(table, changed));
            Assert.That(Shape(cmd, table), Is.EqualTo(expected));
        }, table);
    }

    private static string Compression(IDbCommand cmd, string table, string index)
    {
        cmd.CommandText = $"""
            SELECT MAX(p.data_compression_desc)
              FROM sys.indexes i
              JOIN sys.internal_tables it ON it.parent_object_id = i.[object_id] AND it.parent_minor_id = i.index_id
              JOIN sys.partitions p ON p.[object_id] = it.[object_id] AND p.index_id = 1
             WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.name = '{index}'
            """;
        return cmd.ExecuteScalar() as string ?? "(no index)";
    }

    private static string Compressed(string type) => GeometryGrid.Replace("\"FillFactor\": 80", $"\"FillFactor\": 80, \"CompressionType\": \"{type}\"");

    // DATA_COMPRESSION lands on the spatial index's internal table. A declared one is created and compared; an omitted one
    // is not compared, so an index compressed by hand keeps its compression.
    [TestCase(false)]
    [TestCase(true)]
    public void ADeclaredCompression_IsCreatedComparedAndExtracted_AndAnOmittedOneIsNotCompared(bool indexOnly)
    {
        var table = $"SpxComp_{Guid.NewGuid().ToString("N")[..8]}";
        WithTables(cmd =>
        {
            if (indexOnly) RunTableQuenchProc(cmd, Json(table, GeographyAuto));
            RunTableQuenchProc(cmd, Json(table, Compressed("page")), indexOnly: indexOnly);
            Assert.That(Compression(cmd, table, "SX_Shape"), Is.EqualTo("PAGE"), "created compressed");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, Compressed("PAGE")), indexOnly: indexOnly);
            Assert.That(Audits(cmd, table), Is.Zero, "a redeploy leaves it alone");

            RunTableQuenchProc(cmd, Json(table, Compressed("ROW")), indexOnly: indexOnly);
            Assert.That(Compression(cmd, table, "SX_Shape"), Is.EqualTo("ROW"), "a changed compression re-creates the index");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, GeometryGrid), indexOnly: indexOnly);
            Assert.Multiple(() =>
            {
                Assert.That(Audits(cmd, table), Is.Zero, "an omitted compression is not compared");
                Assert.That(Compression(cmd, table, "SX_Shape"), Is.EqualTo("ROW"));
                Assert.That(Extract(cmd, table).SpatialIndexes.Single(s => s.Name == "[SX_Shape]").CompressionType, Is.EqualTo("ROW"), "extracted when compressed");
            });
        }, table);
    }

    [Test]
    public void AChangedClusteredKey_TakesTheSpatialIndexWithIt_AndTheDeployPutsItBack()
    {
        const string table = "SpatialPkChange";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeometryGrid));
            RunTableQuenchProc(cmd, Json(table, GeometryGrid, "[Id],[Region]"));
            Assert.Multiple(() =>
            {
                Assert.That(Shape(cmd, table), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:64:80"), "the spatial index is back");
                cmd.CommandText = $"SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id WHERE i.[object_id] = OBJECT_ID('dbo.{table}') AND i.is_primary_key = 1";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "on the new key");
            });
        }, table);
    }

    [Test]
    public void ASpatialIndexThePackageStopsDeclaring_IsDropped()
    {
        const string table = "SpatialRemoved";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeometryGrid + "," + GeographyAuto));
            RunTableQuenchProc(cmd, Json(table, GeographyAuto));
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Location:GEOGRAPHY_AUTO_GRID:-:-:12:0"), "the one no longer declared is gone; the other is untouched");
        }, table);
    }

    [Test]
    public void AnIndexOnlyDeploy_CreatesChangesAndDropsSpatialIndexes_TheWayAFullDeployDoes()
    {
        const string table = "SpatialIndexOnly";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeographyAuto));
            RunTableQuenchProc(cmd, Json(table, GeometryGrid + "," + GeographyAuto), indexOnly: true);
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Location:GEOGRAPHY_AUTO_GRID:-:-:12:0,SX_Shape:GEOMETRY_GRID:500:LOW:64:80"), "a declared spatial index is created");

            ClearAudit(cmd);
            RunTableQuenchProc(cmd, Json(table, GeometryGrid + "," + GeographyAuto), indexOnly: true);
            Assert.That(Audits(cmd, table), Is.Zero, "and a redeploy touches nothing");

            RunTableQuenchProc(cmd, Json(table, GeometryGrid.Replace("\"CellsPerObject\": 64", "\"CellsPerObject\": 16")), indexOnly: true);
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:16:80"), "a changed one is re-created and one no longer declared is dropped");

            RunTableQuenchProc(cmd, Json(table, GeometryGrid, "[Id],[Region]"), indexOnly: true);
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Shape:GEOMETRY_GRID:500:LOW:64:80"), "a clustered key change takes the spatial index with it and puts it back");
        }, table);
    }

    [Test]
    public void ASpatialIndexThePackageNeverDeclared_IsDroppedOnlyWhenUnknownIndexesAreDropped()
    {
        const string table = "SpatialUnknown";
        WithTables(cmd =>
        {
            RunTableQuenchProc(cmd, Json(table, GeographyAuto));
            cmd.CommandText = $"CREATE SPATIAL INDEX SX_Adhoc ON dbo.{table} ([Shape]) WITH (BOUNDING_BOX = (0, 0, 10, 10))";
            cmd.ExecuteNonQuery();

            RunTableQuenchProc(cmd, Json(table, GeographyAuto));
            Assert.That(Shape(cmd, table), Does.Contain("SX_Adhoc"), "kept by default: the package does not own it");

            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{_productName}', @TableDefinitions = '{Json(table, GeographyAuto).Replace("'", "''")}', @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 1";
            cmd.ExecuteNonQuery();
            Assert.That(Shape(cmd, table), Is.EqualTo("SX_Location:GEOGRAPHY_AUTO_GRID:-:-:12:0"), "dropped under DropUnknownIndexes");
        }, table);
    }
}
