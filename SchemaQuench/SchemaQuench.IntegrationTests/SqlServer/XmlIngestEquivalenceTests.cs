// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    [TestFixture]
    [Category("SqlServer")]
    public class XmlIngestEquivalenceTests : BaseTableQuenchTests
    {
        // A rich representative model exercising all 9 nested collections the parse shreds.
        // A second table leaves the nullable flags out: unset must stay NULL on both paths (EnableCDC unset is unmanaged).
        private const string RichModelJson = @"[{
  ""Schema"":""dbo"",""Name"":""XmlEquivTable"",""CompressionType"":""PAGE"",""IsTemporal"":true,
  ""HistoryTableSchema"":""history"",""HistoryTableName"":""XmlEquivTable_Archive"",""HistoryRetentionPeriod"":""5 YEARS"",""FileGroup"":""FG_Test"",""UpdateFillFactor"":false,
  ""OldName"":null,""EnableCDC"":false,""PreventDrop"":false,
  ""DropColumnsRemovedFromProduct"":true,""DropForeignKeysRemovedFromProduct"":false,
  ""DropCheckConstraintsRemovedFromProduct"":false,""DropExcludeConstraintsRemovedFromProduct"":false,
  ""DropStatisticsRemovedFromProduct"":false,""DropIndexesRemovedFromProduct"":true,
  ""Columns"":[
    {""Name"":""Id"",""DataType"":""INT"",""Nullable"":false},
    {""Name"":""Amount"",""DataType"":""DECIMAL(10,2)"",""Nullable"":true,""Default"":""0""},
    {""Name"":""Note"",""DataType"":""NVARCHAR(200)"",""Nullable"":true,""Collation"":""SQL_Latin1_General_CP1_CI_AS""},
    {""Name"":""Backfilled"",""DataType"":""INT"",""Nullable"":true,""Default"":""3"",""BackfillExistingRows"":true},
    {""Name"":""Computed"",""DataType"":""INT"",""ComputedExpression"":""Id + 1"",""Persisted"":true,""Nullable"":false},
    {""Name"":""ComputedUndeclared"",""DataType"":""INT"",""ComputedExpression"":""Id + 2"",""Persisted"":true},
    {""Name"":""PlainUndeclared"",""DataType"":""INT""},
    {""Name"":""SysStart"",""DataType"":""DATETIME2(2)"",""Nullable"":false,""GeneratedAlways"":""RowStart"",""Hidden"":true},
    {""Name"":""SysEnd"",""DataType"":""DATETIME2"",""Nullable"":false,""GeneratedAlways"":""RowEnd""}
  ],
  ""Indexes"":[
    {""Name"":""PK_XmlEquivTable"",""PrimaryKey"":true,""Unique"":true,""Clustered"":true,""IndexColumns"":""Id""},
    {""Name"":""IX_Amount"",""Unique"":false,""IndexColumns"":""Amount DESC"",""IncludeColumns"":""Note"",""FileGroup"":""FG_Test""}
  ],
  ""XmlIndexes"":[
    {""Name"":""XI_Data"",""IsPrimary"":true,""Column"":""DataXml"",""PrimaryIndex"":null,""SecondaryIndexType"":null}
  ],
  ""SpatialIndexes"":[
    {""Name"":""sx_shape"",""Column"":""Shape"",""Tessellation"":""geometry_grid"",""BoundingBox"":{""XMin"":-1.5,""YMin"":0,""XMax"":500.25,""YMax"":200},
     ""Grids"":{""Level1"":""medium"",""Level2"":""LOW""},""CellsPerObject"":64,""FillFactor"":0},
    {""Name"":""SX_Location"",""Column"":""Location"",""Tessellation"":""GEOGRAPHY_AUTO_GRID""}
  ],
  ""ForeignKeys"":[
    {""Name"":""FK_XmlEquivTable_Other"",""Columns"":""Id"",""RelatedTableSchema"":""dbo"",""RelatedTable"":""OtherTable"",""RelatedColumns"":""OtherId"",""DeleteAction"":""CASCADE""}
  ],
  ""CheckConstraints"":[
    {""Name"":""CK_Amount"",""Expression"":""Amount >= 0""}
  ],
  ""Statistics"":[
    {""Name"":""ST_Note"",""Columns"":""Note"",""SampleSize"":50}
  ],
  ""FullTextIndex"":[
    {""Columns"":""Note LANGUAGE 1033 STATISTICAL_SEMANTICS, Body STATISTICAL_SEMANTICS"",""FullTextCatalog"":""ftCat"",""KeyIndex"":""PK_XmlEquivTable"",""ChangeTracking"":""AUTO"",""StopList"":""SYSTEM""}
  ]
},
{
  ""Schema"":""dbo"",""Name"":""XmlEquivUnset"",
  ""Columns"":[ {""Name"":""Id"",""DataType"":""INT"",""Nullable"":false} ]
}]";

        // The 9 temp tables both parse scripts produce and every downstream proc consumes. #TableDefinitions
        // is internal parse scratch (shape differs by encoding) and is deliberately not compared.
        private static readonly string[] ConsumedTables =
        {
            "#Tables", "#Columns", "#Indexes", "#XmlIndexes", "#SpatialIndexes",
            "#ForeignKeys", "#CheckConstraints", "#Statistics", "#FullTextIndexes"
        };

        [Test]
        public void XmlShred_ProducesTempTablesIdenticalTo_JsonShred()
        {
            var xml = ModelXmlSerializer.ToIngestXml(RichModelJson, "Tables", "Table");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            var jsonTables = ShredAndCapture(conn,
                "DECLARE @TableDefinitions VARCHAR(MAX) = @payload;\nDECLARE @UpdateFillFactor BIT = 0;\n" +
                ForgeKindler.GetParseTableJsonScript(Platform.SqlServer), RichModelJson);

            var xmlTables = ShredAndCapture(conn,
                "DECLARE @TableDefinitions XML = @payload;\nDECLARE @UpdateFillFactor BIT = 0;\n" +
                ForgeKindler.GetParseTableXmlScript(Platform.SqlServer), xml);

            Assert.Multiple(() =>
            {
                for (var i = 0; i < ConsumedTables.Length; i++)
                {
                    var name = ConsumedTables[i];
                    var jsonRows = jsonTables[i];
                    var xmlRows = xmlTables[i];
                    Assert.That(jsonRows.Count, Is.GreaterThan(0), $"{name}: JSON shred produced 0 rows — fixture does not exercise this table");
                    Assert.That(xmlRows, Is.EqualTo(jsonRows), $"{name}: XML shred rows differ from JSON shred");
                }
                // The declared period columns are lifted onto the table row and off the column rows, on both tiers.
                var tablesRows = string.Join("\n", jsonTables[System.Array.IndexOf(ConsumedTables, "#Tables")]);
                var columnRows = string.Join("\n", jsonTables[System.Array.IndexOf(ConsumedTables, "#Columns")]);
                Assert.That(tablesRows, Does.Contain("[SysStart]").And.Contain("DATETIME2(2)").And.Contain("[SysEnd]").And.Contain("DATETIME2(7)"), "the pair is on #Tables");
                Assert.That(columnRows, Does.Not.Contain("SysStart").And.Not.Contain("SysEnd"), "and not on #Columns");
                // Spatial rows arrive normalized on both tiers: names wrapped, scheme and densities upper-cased, fill factor 0 as unset.
                var spatialRows = string.Join("\n", jsonTables[System.Array.IndexOf(ConsumedTables, "#SpatialIndexes")]);
                Assert.That(spatialRows, Does.Contain("IndexName=[sx_shape]|Column=[Shape]|Tessellation=GEOMETRY_GRID|XMin=-1.5|")
                                             .And.Contain("XMax=500.25|").And.Contain("Level1=MEDIUM|Level2=LOW|Level3=<null>|")
                                             .And.Contain("FillFactor=<null>|"), "the spatial rows are normalized");
            });
        }

        // Runs a parse batch (which builds the 9 temp tables), then selects each and returns, per table, a
        // sorted list of row signatures (all columns except the non-deterministic _RowId). One self-contained
        // command per encoding, so no cross-command temp-table persistence is relied on.
        private static List<List<string>> ShredAndCapture(IDbConnection conn, string parseBatch, string payload)
        {
            var selects = string.Concat(ConsumedTables.Select(t => $"SELECT * FROM {t};\n"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = parseBatch + "\n" + selects;
            var p = cmd.CreateParameter();
            p.ParameterName = "@payload";
            p.Value = payload;
            p.DbType = DbType.String;
            cmd.Parameters.Add(p);

            var perTable = new List<List<string>>();
            using var reader = cmd.ExecuteReader();
            do
            {
                var rowIdOrdinal = -1;
                for (var c = 0; c < reader.FieldCount; c++)
                    if (reader.GetName(c) == "_RowId") rowIdOrdinal = c;

                var rows = new List<string>();
                while (reader.Read())
                {
                    var sb = new StringBuilder();
                    for (var c = 0; c < reader.FieldCount; c++)
                    {
                        if (c == rowIdOrdinal) continue;
                        sb.Append(reader.GetName(c)).Append('=');
                        sb.Append(reader.IsDBNull(c) ? "<null>" : System.Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture));
                        sb.Append('|');
                    }
                    rows.Add(sb.ToString());
                }
                rows.Sort();
                perTable.Add(rows);
            } while (reader.NextResult());

            return perTable;
        }
    }
}
