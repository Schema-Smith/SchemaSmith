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
        // A rich representative model exercising all 8 nested collections the parse shreds.
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
    {""Name"":""PlainUndeclared"",""DataType"":""INT""}
  ],
  ""Indexes"":[
    {""Name"":""PK_XmlEquivTable"",""PrimaryKey"":true,""Unique"":true,""Clustered"":true,""IndexColumns"":""Id""},
    {""Name"":""IX_Amount"",""Unique"":false,""IndexColumns"":""Amount DESC"",""IncludeColumns"":""Note"",""FileGroup"":""FG_Test""}
  ],
  ""XmlIndexes"":[
    {""Name"":""XI_Data"",""IsPrimary"":true,""Column"":""DataXml"",""PrimaryIndex"":null,""SecondaryIndexType"":null}
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
}]";

        // The 8 temp tables both parse scripts produce and every downstream proc consumes. #TableDefinitions
        // is internal parse scratch (shape differs by encoding) and is deliberately not compared.
        private static readonly string[] ConsumedTables =
        {
            "#Tables", "#Columns", "#Indexes", "#XmlIndexes",
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
            });
        }

        // Runs a parse batch (which builds the 8 temp tables), then selects each and returns, per table, a
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
